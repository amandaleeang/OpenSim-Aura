/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using TeleportFlags = OpenSim.Framework.Constants.TeleportFlags;

namespace OpenSim.Region.OptionalModules.Aura
{
    /// <summary>
    /// Sends a local-chat line only to the arriving avatar on grid login
    /// and Hypergrid arrival. Other avatars do not see it; this is not a dialog.
    /// Estate owners and estate managers also hear when a newer GitHub release exists.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "AuraLoginChatModule")]
    public class AuraLoginChatModule : INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const string ProjectUrl = "https://github.com/amandaleeang/OpenSim-Aura";
        private const string LatestReleaseUrl = "https://api.github.com/repos/amandaleeang/OpenSim-Aura/releases/latest";
        private const string ReleaseFileName = "aura-release.txt";
        private const string LoginChatFromName = "OpenSim-Aura";
        private const string DevLabel = "dev";
        private const TeleportFlags LoginArrivalFlags = TeleportFlags.ViaLogin | TeleportFlags.ViaHGLogin;

        // v1.0.8 or 1.0.8. Build tags that are not a release number do not compare.
        private static readonly Regex ReleaseTagPattern = new Regex(
            @"^[vV]?(\d+\.\d+(?:\.\d+){0,2})$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Firestorm treats UUID.Zero as the region and shows "RegionName (FromName)".
        private static readonly UUID LoginChatSourceId = new UUID("a01a0000-0000-4000-8000-00000000a01a");

        private static readonly object s_gate = new object();
        private static Timer s_timer;
        private static bool s_localReady;
        private static bool s_checkStarted;
        private static string s_localTag;
        private static string s_localLabel = DevLabel;
        private static Version s_localVersion;
        private static string s_latestTag;
        private static string s_latestLabel;
        private static Version s_latestVersion;

        public string Name { get { return "AuraLoginChatModule"; } }

        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource config)
        {
            EnsureLocalLabel();

            IConfig section = config?.Configs["AuraLogin"];
            bool check = section == null || section.GetBoolean("CheckForUpdates", true);
            if (check)
                StartReleaseCheck();
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
        }

        public void RemoveRegion(Scene scene)
        {
            scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        private void OnMakeRootAgent(ScenePresence sp)
        {
            if (sp == null || sp.IsNPC || sp.IsChildAgent || sp.ControllingClient == null)
                return;

            if ((sp.TeleportFlags & LoginArrivalFlags) == 0)
                return;

            // OnMakeRootAgent runs inside CompleteMovement, before RegionHandshake
            // and AgentMovementComplete. Sending chat here makes the viewer still
            // attribute the line to the previous region. Wait for the handshake
            // reply, then a short delay so the viewer has finished the switch.
            IClientAPI client = sp.ControllingClient;
            UUID agentId = sp.UUID;
            Scene scene = sp.Scene;

            Action<IClientAPI> handler = null;
            handler = (c) =>
            {
                c.OnRegionHandShakeReply -= handler;
                Util.FireAndForget(_ => SendLoginChat(scene, agentId));
            };
            client.OnRegionHandShakeReply += handler;
        }

        private static void SendLoginChat(Scene scene, UUID agentId)
        {
            Thread.Sleep(1000);

            ScenePresence sp = scene.GetScenePresence(agentId);
            if (sp == null || sp.IsDeleted || sp.IsNPC || sp.IsChildAgent || sp.ControllingClient == null)
                return;

            string localLabel = LocalLabel();
            SendPrivateChat(sp, BrandingLine(localLabel));

            if (sp.Scene.RegionInfo?.EstateSettings == null
                || !sp.Scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(sp.UUID))
                return;

            if (!TryGetNewerRelease(out string latestLabel, out string latestTag))
                return;

            SendPrivateChat(sp,
                "This region is running OpenSim-Aura " + localLabel + ". " + latestLabel + " is available.\n  "
                + ProjectUrl + "/releases/tag/" + latestTag);
        }

        private static string BrandingLine(string versionLabel)
        {
            return "This Sim is running on OpenSim-Aura " + versionLabel + "\n  " + ProjectUrl;
        }

        private static void SendPrivateChat(ScenePresence sp, string message)
        {
            sp.ControllingClient.SendChatMessage(
                message,
                (byte)ChatTypeEnum.Say,
                sp.AbsolutePosition,
                LoginChatFromName,
                LoginChatSourceId,
                sp.UUID,
                (byte)ChatSourceType.Object,
                (byte)ChatAudibleLevel.Fully);
        }

        private static void EnsureLocalLabel()
        {
            lock (s_gate)
            {
                if (s_localReady)
                    return;
                s_localReady = true;
                s_localTag = ReadReleaseFile();
                if (TryParseRelease(s_localTag, out Version parsed, out string label))
                {
                    s_localVersion = parsed;
                    s_localLabel = label;
                }
                else if (!string.IsNullOrEmpty(s_localTag))
                {
                    s_localLabel = s_localTag;
                }
            }
        }

        private static string LocalLabel()
        {
            lock (s_gate)
                return s_localLabel;
        }

        private static bool TryGetNewerRelease(out string latestLabel, out string latestTag)
        {
            lock (s_gate)
            {
                latestLabel = s_latestLabel;
                latestTag = s_latestTag;
                if (s_localVersion == null || s_latestVersion == null || string.IsNullOrEmpty(s_latestTag))
                    return false;
                return s_latestVersion > s_localVersion;
            }
        }

        // A source build has no aura-release.txt. It must not be told to install an older GitHub zip.
        private static void StartReleaseCheck()
        {
            lock (s_gate)
            {
                if (s_checkStarted)
                    return;
                s_checkStarted = true;
                if (s_localVersion == null)
                {
                    m_log.Debug("[AURA LOGIN]: No release tag; GitHub update check skipped");
                    return;
                }
            }

            s_timer = new Timer(RefreshLatest, null, TimeSpan.Zero, TimeSpan.FromHours(6));
        }

        private static void RefreshLatest(object state)
        {
            try
            {
                using HttpClient client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(20);
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
                request.Headers.UserAgent.ParseAdd("OpenSim-Aura");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");

                using HttpResponseMessage response = client.Send(request);
                response.EnsureSuccessStatusCode();
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                Match tagMatch = Regex.Match(body, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (!tagMatch.Success)
                    return;

                string tag = tagMatch.Groups[1].Value;
                if (!TryParseRelease(tag, out Version latest, out string label))
                    return;

                bool announce;
                lock (s_gate)
                {
                    announce = s_latestTag != tag && s_localVersion != null && latest > s_localVersion;
                    s_latestTag = tag;
                    s_latestLabel = label;
                    s_latestVersion = latest;
                }

                if (announce)
                    m_log.Info("[AURA LOGIN]: GitHub release " + label + " is newer than this build " + LocalLabel());
            }
            catch (Exception e)
            {
                m_log.Debug("[AURA LOGIN]: Release check failed: " + e.Message);
            }
        }

        private static string ReadReleaseFile()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
            string[] paths = new string[]
            {
                ReleaseFileName,
                Path.Combine(baseDir, ReleaseFileName)
            };

            string previous = null;
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || path == previous)
                    continue;
                previous = path;

                try
                {
                    if (!File.Exists(path))
                        continue;

                    string text = File.ReadAllText(path).Trim().TrimStart('\uFEFF');
                    int cut = text.IndexOfAny(new char[] { '\r', '\n', ' ', '\t' });
                    if (cut >= 0)
                        text = text.Substring(0, cut);
                    if (text.Length > 0)
                        return text;
                }
                catch (Exception e)
                {
                    m_log.Debug("[AURA LOGIN]: Could not read " + path + ": " + e.Message);
                }
            }

            return null;
        }

        private static bool TryParseRelease(string tag, out Version version, out string label)
        {
            version = null;
            label = null;
            if (string.IsNullOrWhiteSpace(tag))
                return false;

            Match match = ReleaseTagPattern.Match(tag.Trim());
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out version))
                return false;

            label = "v" + match.Groups[1].Value;
            return true;
        }
    }
}
