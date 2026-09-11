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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Imaging;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBake
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ServerSideBakeModule")]
    public class ServerSideBakeModule : ISharedRegionModule, IServerSideBakeModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly List<Scene> m_scenes = new List<Scene>();
        private readonly ConcurrentDictionary<UUID, byte> m_inProgress = new ConcurrentDictionary<UUID, byte>();
        private readonly ConcurrentDictionary<UUID, int> m_bakeGeneration = new ConcurrentDictionary<UUID, int>();
        private readonly ConcurrentDictionary<UUID, int> m_completedBakeGen = new ConcurrentDictionary<UUID, int>();
        private readonly ConcurrentDictionary<UUID, CancellationTokenSource> m_bakeCts =
            new ConcurrentDictionary<UUID, CancellationTokenSource>();
        private readonly ConcurrentDictionary<UUID, UUID[]> m_lastBakeIds = new ConcurrentDictionary<UUID, UUID[]>();
        // Viewer's Current Outfit folder version from UpdateAvatarAppearance
        // or IncrementCOFVersion. Packet CofVersion must not run ahead of
        // this or Firestorm skips the next UpdateAvatarAppearance.
        private readonly ConcurrentDictionary<UUID, int> m_viewerCof =
            new ConcurrentDictionary<UUID, int>();
        // CofVersion actually packed on AvatarAppearance. After IncrementCOFVersion
        // the viewer slams lastRcv to the returned version, so the following
        // appearance packet must be that value + 1 or it is dropped.
        private readonly ConcurrentDictionary<UUID, int> m_packetCof =
            new ConcurrentDictionary<UUID, int>();
        // First UpdateAvatarAppearance after MakeRoot is login/TP.
        private readonly ConcurrentDictionary<UUID, byte> m_seenAppearanceCap =
            new ConcurrentDictionary<UUID, byte>();
        private readonly ConcurrentDictionary<UUID, ConcurrentQueue<PendingCacheReply>> m_pendingCacheReplies =
            new ConcurrentDictionary<UUID, ConcurrentQueue<PendingCacheReply>>();
        // Source-region GET /appearance/ base, captured at MakeRoot while
        // CallbackURI is still set (ReleaseAgent clears it before seed).
        private readonly ConcurrentDictionary<UUID, string> m_originAppearance =
            new ConcurrentDictionary<UUID, string>();
        // Environment.TickCount64 after which a force rebake is allowed again.
        private readonly ConcurrentDictionary<UUID, long> m_forceBakeReadyAt =
            new ConcurrentDictionary<UUID, long>();
        private readonly SemaphoreSlim m_bakeGate = new SemaphoreSlim(2);

        private const int BakeSlotWaitMs = 90000;
        private const int OutfitBurstSettleMs = 800;
        private const int ForceBakeCooldownMs = 5000;
        // CompleteMovement sets IsInTransit, then MakeRootAgent, then clears it.
        // The bake worker must wait for that window to close (~30ms typical).
        private const int TransitWaitMs = 10000;
        // HG incoming attachments are gathered asynchronously. Wait for that
        // batch (or an empty GotAttachmentsData) before publishing SSA.
        private const int AttachmentWaitMs = 15000;

        private sealed class PendingCacheReply
        {
            public IClientAPI Client;
            public int Serial;
            public List<CachedTextureRequestArg> Request;
        }

        private bool m_enabled;
        private bool m_commandsAdded;
        private bool m_appearanceHandlerRegistered;

        public string Name { get { return "ServerSideBakeModule"; } }

        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ServerSideBake"];
            if (config == null)
                return;

            m_enabled = config.GetBoolean("Enabled", false);
            if (!m_enabled)
                return;

            TextureBaker.EnsureResourceDir();
            ManagedImage lib = TextureBaker.TryLoadResource("head_color.tga");
            m_log.InfoFormat("[SSBAKE]: enabled (library={0}, RESOURCE_DIR={1})",
                lib != null ? lib.Width + "x" + lib.Height : "missing",
                Settings.RESOURCE_DIR);
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
            RemoveAppearanceHttpHandler();
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_scenes)
                m_scenes.Add(scene);

            scene.RegisterModuleInterface<IServerSideBakeModule>(this);
            scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
            scene.EventManager.OnMakeChildAgent += OnMakeChildAgent;
            scene.EventManager.OnNewClient += HandleNewClient;
            scene.EventManager.OnRegisterCaps += RegisterCaps;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;
            scene.EventManager.OnMakeChildAgent -= OnMakeChildAgent;
            scene.EventManager.OnNewClient -= HandleNewClient;
            scene.EventManager.OnRegisterCaps -= RegisterCaps;
            ISimulatorFeaturesModule features = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
            if (features != null)
                features.OnSimulatorFeaturesRequest -= HandleSimulatorFeaturesRequest;
            scene.UnregisterModuleInterface<IServerSideBakeModule>(this);

            lock (m_scenes)
            {
                m_scenes.Remove(scene);
                if (m_scenes.Count == 0)
                    RemoveAppearanceHttpHandler();
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            EnsureAppearanceHttpHandler();

            ISimulatorFeaturesModule features = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
            if (features != null)
                features.OnSimulatorFeaturesRequest += HandleSimulatorFeaturesRequest;

            if (m_commandsAdded)
                return;
            m_commandsAdded = true;

            scene.AddCommand(
                "Users", this, "ssb bake",
                "ssb bake <first-name> <last-name>",
                "Server-side bake the named avatar and publish the result.",
                HandleBakeCommand);
        }

        private void RegisterCaps(UUID agentID, Caps caps)
        {
            caps.RegisterSimpleHandler("UpdateAvatarAppearance",
                new SimpleOSDMapHandler("POST", "/" + UUID.Random(),
                    (req, resp, map) => HandleUpdateAvatarAppearance(req, resp, map, agentID)));
            // Firestorm Rebake Textures (Ctrl-Alt-R) does GET this cap when
            // present, then a local tex-refresh of current TEs. It does not
            // POST UpdateAvatarAppearance for the same COF.
            caps.RegisterSimpleHandler("IncrementCOFVersion",
                new SimpleStreamHandler("/" + UUID.Random(),
                    (req, resp) => HandleIncrementCofVersion(req, resp, agentID)));
        }

        private void HandleUpdateAvatarAppearance(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse,
            OSDMap map, UUID agentID)
        {
            ScenePresence sp = FindRootPresence(agentID);
            if (sp == null)
            {
                WriteCapFailure(httpResponse, "agent not in region");
                return;
            }

            OSDMap result = new OSDMap();
            int cof = 0;
            if (map != null && map.ContainsKey("cof_version"))
                cof = map["cof_version"].AsInteger();
            int lastCof = 0;
            m_viewerCof.TryGetValue(sp.UUID, out lastCof);
            if (cof > 0)
                m_viewerCof[sp.UUID] = cof;
            // Honour the COF the viewer asked us to bake. Packet CofVersion
            // is this value (see PrepareAppearancePacket), not Serial++.
            if (sp.Appearance != null && cof > sp.Appearance.Serial)
                sp.Appearance.Serial = cof;

            // Login, TP, outfit change, and leaving edit appearance all POST
            // this cap. Reuse cache/XBakes for unchanged slots; only Rebake
            // Textures (IncrementCOFVersion) forces a full render.
            bool firstCap = m_seenAppearanceCap.TryAdd(sp.UUID, 1);
            m_log.InfoFormat("[SSBAKE]: UpdateAvatarAppearance {0} cof={1} last={2} first={3}",
                sp.Name, cof, lastCof, firstCap);
            RequestBake(sp, "UpdateAvatarAppearance");
            result["success"] = true;
            if (map != null && map.ContainsKey("cof_version"))
                result["cof_version"] = map["cof_version"];
            WriteLlsd(httpResponse, result, HttpStatusCode.OK);
        }

        private void HandleIncrementCofVersion(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse,
            UUID agentID)
        {
            ScenePresence sp = FindRootPresence(agentID);
            if (sp == null)
            {
                WriteCapFailure(httpResponse, "agent not in region");
                return;
            }

            OSDMap result = new OSDMap();
            int lastCof = 0;
            m_viewerCof.TryGetValue(sp.UUID, out lastCof);
            int lastPacket = 0;
            m_packetCof.TryGetValue(sp.UUID, out lastPacket);
            int serial = sp.Appearance != null ? sp.Appearance.Serial : 0;
            int n = lastCof;
            if (lastPacket > n)
                n = lastPacket;
            if (serial > n)
                n = serial;
            n++;
            m_viewerCof[sp.UUID] = n;
            // Viewer slams lastRcv to this returned version, then drops any
            // AvatarAppearance with CofVersion <= that. Publish one ahead.
            m_packetCof[sp.UUID] = n;
            if (sp.Appearance != null && n > sp.Appearance.Serial)
                sp.Appearance.Serial = n;

            result["version"] = n;
            m_log.InfoFormat("[SSBAKE]: IncrementCOFVersion {0} version={1} (viewer rebake)",
                sp.Name, n);
            RequestBake(sp, "IncrementCOFVersion", true);
            WriteLlsd(httpResponse, result, HttpStatusCode.OK);
        }

        private ScenePresence FindRootPresence(UUID agentID)
        {
            lock (m_scenes)
            {
                foreach (Scene scene in m_scenes)
                {
                    ScenePresence sp = scene.GetScenePresence(agentID);
                    if (sp != null && !sp.IsChildAgent && !sp.IsNPC)
                        return sp;
                }
            }
            return null;
        }

        private ScenePresence FindPresence(UUID agentID)
        {
            lock (m_scenes)
            {
                foreach (Scene scene in m_scenes)
                {
                    ScenePresence sp = scene.GetScenePresence(agentID);
                    if (sp != null)
                        return sp;
                }
            }
            return null;
        }

        public void PrepareAppearancePacket(UUID agentId, byte[] visualParams,
            out byte[] visualParamsForSend, out int cofVersion)
        {
            visualParamsForSend = visualParams ?? Array.Empty<byte>();
            cofVersion = 0;
            ScenePresence sp = FindPresence(agentId);
            if (sp?.Appearance == null)
                return;

            byte[] src = sp.Appearance.VisualParams ?? visualParamsForSend;
            const int vp11000 = (int)AvatarAppearance.VPElement._APPEARANCEMESSAGE_VERSION;
            int n = src.Length;
            int sendLen = n > vp11000 ? n : vp11000 + 1;
            byte[] copy = new byte[sendLen];
            if (n > 0)
                Buffer.BlockCopy(src, 0, copy, 0, n);
            copy[vp11000] = 1;
            visualParamsForSend = copy;
            // Prefer the CofVersion chosen at PublishAppearance. After
            // IncrementCOFVersion that is viewer COF + 1 so the packet is
            // not dropped when Firestorm slams lastRcv to the GET result.
            int packetCof;
            if (m_packetCof.TryGetValue(agentId, out packetCof) && packetCof > 0)
                cofVersion = packetCof;
            else
            {
                int viewerCof;
                if (m_viewerCof.TryGetValue(agentId, out viewerCof) && viewerCof > 0)
                    cofVersion = viewerCof;
                else
                    cofVersion = 1;
            }
        }

        public int WriteAppearanceAttachmentBlock(UUID agentId, UUID toAgentId, byte[] data, int pos)
        {
            int attachCountPos = pos;
            data[pos++] = 0;
            byte attachCount = 0;
            ScenePresence target = FindPresence(agentId);
            if (target == null)
                return pos;

            bool toSelf = agentId.Equals(toAgentId);
            List<SceneObjectGroup> attachments = target.GetAttachments();
            for (int i = 0; i < attachments.Count && attachCount < 255; i++)
            {
                SceneObjectGroup sog = attachments[i];
                if (sog == null || sog.IsDeleted)
                    continue;
                if (!toSelf && sog.HasPrivateAttachmentPoint)
                    continue;
                uint point = sog.AttachmentPoint;
                if (point > 255)
                    continue;
                sog.UUID.ToBytes(data, pos); pos += 16;
                data[pos++] = (byte)point;
                attachCount++;
            }
            data[attachCountPos] = attachCount;
            return pos;
        }

        private static void WriteCapFailure(IOSHttpResponse httpResponse, string error)
        {
            OSDMap result = new OSDMap();
            result["success"] = false;
            result["error"] = error;
            WriteLlsd(httpResponse, result, HttpStatusCode.OK);
        }

        private static void WriteLlsd(IOSHttpResponse httpResponse, OSDMap result, HttpStatusCode status)
        {
            httpResponse.StatusCode = (int)status;
            httpResponse.ContentType = "application/llsd+xml";
            httpResponse.RawBuffer = Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(result));
        }

        private void EnsureAppearanceHttpHandler()
        {
            if (m_appearanceHandlerRegistered || MainServer.Instance == null)
                return;

            MainServer.Instance.AddSimpleStreamHandler(
                new SimpleStreamHandler("/appearance", HandleAppearanceTextureRequest), true);
            m_appearanceHandlerRegistered = true;
            m_log.Info("[SSBAKE]: RegionProtocols bit 0 on; rebake is IncrementCOFVersion; textures from this sim /appearance and GetTexture");
        }

        private void HandleSimulatorFeaturesRequest(UUID agentID, ref OSDMap features)
        {
            if (features == null)
                return;

            ScenePresence sp = FindRootPresence(agentID);
            Scene scene = sp?.Scene;
            if (scene == null)
            {
                lock (m_scenes)
                {
                    if (m_scenes.Count > 0)
                        scene = m_scenes[0];
                }
            }
            if (scene == null)
                return;

            string url = scene.RegionInfo.ServerURI + "appearance/";
            features["AgentAppearanceService"] = url;
            OSDMap extras = features.TryGetValue("OpenSimExtras", out OSD extra) ? extra as OSDMap : new OSDMap();
            if (extras == null)
                extras = new OSDMap();
            extras["AgentAppearanceService"] = url;
            features["OpenSimExtras"] = extras;
        }

        private void RemoveAppearanceHttpHandler()
        {
            if (!m_appearanceHandlerRegistered || MainServer.Instance == null)
                return;

            MainServer.Instance.RemoveSimpleStreamHandler("/appearance");
            m_appearanceHandlerRegistered = false;
        }

        private void HandleAppearanceTextureRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            if (httpRequest.HttpMethod != "GET" && httpRequest.HttpMethod != "HEAD")
            {
                httpResponse.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (!TryParseAppearanceTextureId(httpRequest, out UUID textureID, out UUID agentId))
            {
                m_log.WarnFormat("[SSBAKE]: GET /appearance bad path={0}",
                    httpRequest.UriPath);
                httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            AssetBase asset = FindBakeAsset(textureID, agentId);
            if (asset?.Data == null || asset.Data.Length == 0)
            {
                // Instant 404. Do not AssetService.Get() a bake UUID: bakes are
                // local cache only, and a Robust miss is a ~10s hang that keeps
                // incoming avatars as clouds.
                m_log.WarnFormat("[SSBAKE]: GET /appearance miss {0}", textureID);
                httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            int len = asset.Data.Length;
            if (Util.TryParseHttpRange(httpRequest.Headers["range"] ?? httpRequest.Headers["Range"],
                    out int start, out int end))
            {
                if (start < len)
                {
                    if (end == -1)
                        end = len - 1;
                    else
                        end = Utils.Clamp(end, 0, len - 1);
                    start = Utils.Clamp(start, 0, end);
                    len = end - start + 1;
                    httpResponse.AddHeader("Content-Range",
                        string.Format("bytes {0}-{1}/{2}", start, end, asset.Data.Length));
                    httpResponse.StatusCode = (int)HttpStatusCode.PartialContent;
                    httpResponse.RawBufferStart = start;
                }
                else
                    httpResponse.StatusCode = (int)HttpStatusCode.OK;
            }
            else
                httpResponse.StatusCode = (int)HttpStatusCode.OK;

            httpResponse.ContentType = "image/x-j2c";
            httpResponse.AddHeader("Accept-Ranges", "bytes");
            httpResponse.RawBuffer = asset.Data;
            httpResponse.RawBufferLen = len;
            httpResponse.Priority = 2;
        }

        private static bool TryParseAppearanceTextureId(IOSHttpRequest httpRequest,
            out UUID textureID, out UUID agentId)
        {
            textureID = UUID.Zero;
            agentId = UUID.Zero;
            string qid = httpRequest.QueryString?["texture_id"];
            if (!string.IsNullOrEmpty(qid) && UUID.TryParse(qid, out textureID))
                return true;

            string path = httpRequest.UriPath ?? httpRequest.RawUrl ?? string.Empty;
            int q = path.IndexOf('?');
            if (q >= 0)
                path = path.Substring(0, q);
            path = path.Trim('/');
            if (string.IsNullOrEmpty(path))
                return false;

            string[] parts = path.Split('/');
            string last = parts[parts.Length - 1];
            if (last.Equals("appearance", StringComparison.OrdinalIgnoreCase)
                || last.Equals("texture", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!UUID.TryParse(last, out textureID))
                return false;

            // Firestorm: /appearance/texture/{avatar}/{slot}/{bake-uuid}
            if (parts.Length >= 4)
            {
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (parts[i].Equals("texture", StringComparison.OrdinalIgnoreCase)
                        && UUID.TryParse(parts[i + 1], out agentId))
                        break;
                }
            }
            return true;
        }

        private AssetBase FindBakeAsset(UUID textureID, UUID agentId)
        {
            string id = textureID.ToString();
            List<Scene> scenes;
            lock (m_scenes)
                scenes = new List<Scene>(m_scenes);

            foreach (Scene scene in scenes)
            {
                IAssetCache cache = scene.RequestModuleInterface<IAssetCache>();
                AssetBase cached = cache?.GetCached(id);
                if (cached?.Data != null && cached.Data.Length > 0)
                    return cached;

                cached = scene.AssetService?.GetCached(id);
                if (cached?.Data != null && cached.Data.Length > 0)
                    return cached;
            }

            if (agentId.IsZero())
                return null;

            foreach (Scene scene in scenes)
            {
                IBakedTextureModule xbakes = scene.RequestModuleInterface<IBakedTextureModule>();
                if (xbakes == null)
                    continue;
                try
                {
                    WearableCacheItem[] stored = xbakes.Get(agentId);
                    if (stored == null)
                        continue;
                    for (int i = 0; i < stored.Length; i++)
                    {
                        WearableCacheItem item = stored[i];
                        if (item?.TextureAsset?.Data == null || item.TextureAsset.Data.Length == 0)
                            continue;
                        if (item.TextureID.NotEqual(textureID) && item.CacheId.NotEqual(textureID))
                            continue;
                        IAssetCache cache = scene.RequestModuleInterface<IAssetCache>();
                        if (cache != null)
                            return CacheBakeAsset(cache, textureID, agentId, "SSBake", item.TextureAsset.Data);
                        return item.TextureAsset;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private void HandleNewClient(IClientAPI client)
        {
            client.OnSetAppearance += HandleSetAppearance;
            client.OnAvatarNowWearing += HandleNowWearing;
        }

        private void HandleNowWearing(IClientAPI client, AvatarWearingArgs e)
        {
            QueueFromClient(client, "NowWearing");
        }

        private void HandleSetAppearance(IClientAPI client, Primitive.TextureEntry textureEntry,
            byte[] visualParams, Vector3 avSize, WearableCacheItem[] cacheItems)
        {
            // AvatarFactory applies the viewer's TextureEntry (including a
            // Ctrl-Alt-R local rebake) then QueueAppearanceSend. Keep our
            // published bake IDs so others do not see viewer-local uploads.
            // If the viewer sent different bake IDs after we have already
            // published, treat that as a manual rebake request.
            Scene scene = client.Scene as Scene;
            ScenePresence sp = scene?.GetScenePresence(client.AgentId);
            if (sp == null || sp.IsChildAgent || sp.IsNPC)
                return;

            UUID id = sp.UUID;
            Util.FireAndForget(
                delegate
                {
                    try
                    {
                        Thread.Sleep(50);
                        if (BakePendingOrRunning(id))
                            return;

                        m_log.DebugFormat("[SSBAKE]: SetAppearance from {0}", sp.Name);
                        bool viewerAskedForRebake = ViewerBakesDifferFromLast(sp);
                        bool restored = RestoreLastBakes(sp);
                        if (viewerAskedForRebake)
                        {
                            m_log.InfoFormat("[SSBAKE]: viewer rebake for {0} (SetAppearance bake IDs changed); rebake",
                                sp.Name);
                            RequestBake(sp, "SetAppearance-force", true);
                        }
                        else if (restored)
                        {
                            m_log.InfoFormat("[SSBAKE]: restored server bake IDs after SetAppearance for {0}",
                                sp.Name);
                            PublishAppearance(sp);
                        }
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("[SSBAKE]: SetAppearance restore failed for {0}: {1}", id, e);
                    }
                }, null, "SSBake.Restore");
        }

        public bool HandleCachedTextureRequest(IClientAPI client, int serial, List<CachedTextureRequestArg> request)
        {
            Scene scene = client.Scene as Scene;
            ScenePresence sp = scene?.GetScenePresence(client.AgentId);
            if (sp == null || sp.IsChildAgent || sp.IsNPC || request == null)
                return false;

            WearableCacheItem[] wearableCache = sp.Appearance.WearableCacheItems;
            if (wearableCache == null)
            {
                wearableCache = WearableCacheItem.GetDefaultCacheItem();
                sp.Appearance.WearableCacheItems = wearableCache;
            }

            // Arrival bake is from wearables, not incoming TextureEntry IDs.
            // If that bake is already running, wait for it instead of
            // replying with leftover/default bake UUIDs.
            bool bakeRunning = BakePendingOrRunning(sp.UUID);
            if (!bakeRunning && HasCachedBakes(sp))
            {
                SendCachedTextureReply(client, sp, serial, request);
                return true;
            }

            ConcurrentQueue<PendingCacheReply> q = m_pendingCacheReplies.GetOrAdd(
                sp.UUID, _ => new ConcurrentQueue<PendingCacheReply>());
            q.Enqueue(new PendingCacheReply
            {
                Client = client,
                Serial = serial,
                Request = request
            });

            if (bakeRunning)
            {
                m_log.InfoFormat("[SSBAKE]: AgentCachedTexture from {0} ({1} slots serial={2}); wait for in-flight bake",
                    sp.Name, request.Count, serial);
                return true;
            }

            m_log.InfoFormat("[SSBAKE]: AgentCachedTexture from {0} ({1} slots serial={2}); rebake then reply",
                sp.Name, request.Count, serial);

            RequestBake(sp, "CachedTexture");
            return true;
        }

        // This viewer sends baked face indexes (8=Head, 9=Upper, 10=Lower,
        // 11=Eyes, 19=Skirt, 20=Hair). Do not run those through
        // BakeIndexToTextureIndex — that map is only for bake-type 0..5 and
        // remapping 8/9/10 produced Zero TextureIDs in the cache reply.
        private static int BakeFaceFromViewerIndex(int viewerIndex)
        {
            if (viewerIndex >= 0 && BakeLayerMap.IsBakeFace((AvatarTextureIndex)viewerIndex))
                return viewerIndex;

            byte[] map = AppearanceManager.BakeIndexToTextureIndex;
            if (viewerIndex >= 0 && map != null && viewerIndex < map.Length)
                return map[viewerIndex];
            return viewerIndex;
        }

        private void SendCachedTextureReply(IClientAPI client, ScenePresence sp,
            int serial, List<CachedTextureRequestArg> request)
        {
            if (client == null || sp == null || request == null)
                return;

            WearableCacheItem[] wearableCache = sp.Appearance?.WearableCacheItems;
            List<CachedTextureResponseArg> reply = new List<CachedTextureResponseArg>(request.Count);
            foreach (CachedTextureRequestArg arg in request)
            {
                UUID tex = UUID.Zero;
                int face = BakeFaceFromViewerIndex(arg.BakedTextureIndex);
                if (wearableCache != null && face >= 0 && face < wearableCache.Length)
                    tex = wearableCache[face].TextureID;
                if (BakeLayerMap.IsUnsetTexture(tex))
                    tex = UUID.Zero;
                reply.Add(new CachedTextureResponseArg
                {
                    BakedTextureIndex = arg.BakedTextureIndex,
                    BakedTextureID = tex,
                    HostName = null
                });
            }

            client.SendCachedTextureResponse(sp, serial, reply);
        }

        public bool HasCachedBakes(IScenePresence isp)
        {
            return AppearanceAlreadyServerBaked(isp as ScenePresence);
        }

        private static bool AppearanceAlreadyServerBaked(ScenePresence sp)
        {
            if (sp?.Appearance?.Texture?.FaceTextures == null)
                return false;

            IAssetCache cache = sp.Scene?.RequestModuleInterface<IAssetCache>();
            if (cache == null)
                return false;

            int found = 0;
            foreach (BakeSlot slot in BakeLayerMap.Slots)
            {
                Primitive.TextureEntryFace face = sp.Appearance.Texture.FaceTextures[(int)slot.FaceIndex];
                if (face == null || BakeLayerMap.IsUnsetTexture(face.TextureID))
                    continue;

                string id = face.TextureID.ToString();
                AssetBase cached = cache.GetCached(id);
                if (cached?.Data == null || cached.Data.Length == 0)
                    cached = sp.Scene.AssetService?.GetCached(id);
                if (cached?.Data == null || cached.Data.Length == 0)
                    return false;
                found++;
            }
            return found > 0;
        }

        private void QueueFromClient(IClientAPI client, string reason)
        {
            Scene scene = client.Scene as Scene;
            ScenePresence sp = scene?.GetScenePresence(client.AgentId);
            if (sp == null || sp.IsChildAgent || sp.IsNPC)
                return;
            RequestBake(sp, reason);
        }

        private void OnMakeRootAgent(ScenePresence sp)
        {
            if (sp == null)
                return;

            // TP-in may carry bake JPEG bytes on WearableCacheItems. Put them
            // in this region's cache before CompleteMovement announces the
            // avatar to others, matching SendOtherAgentsAvatarFullToMe.
            RecacheIncomingBakes(sp);
            m_seenAppearanceCap.TryRemove(sp.UUID, out _);
            m_packetCof.TryRemove(sp.UUID, out _);
            RememberOriginAppearance(sp);

            // Seed what the previous region already published, then bake
            // default/missing slots from the wearable list.
            RequestBake(sp, "MakeRootAgent");
        }

        private void OnMakeChildAgent(ScenePresence sp)
        {
            if (sp == null)
                return;
            m_packetCof.TryRemove(sp.UUID, out _);
            m_originAppearance.TryRemove(sp.UUID, out _);
            CancelBake(sp.UUID, "MakeChildAgent");
        }

        private void CancelBake(UUID id, string reason)
        {
            if (m_bakeCts.TryGetValue(id, out CancellationTokenSource cts) && cts != null)
            {
                m_log.InfoFormat("[SSBAKE]: cancelling in-flight bake for {0} ({1})", id, reason);
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>
        /// Seed this region's asset cache from bake bytes already on the
        /// presence (same-process child, or WearableCacheItem.TextureAsset).
        /// Runs inside CompleteMovement while IsInTransit is still true, so
        /// this must not use CanBake().
        /// </summary>
        private void RecacheIncomingBakes(ScenePresence sp)
        {
            if (sp?.Appearance?.Texture == null || sp.IsDeleted)
                return;

            IAssetCache cache = sp.Scene?.RequestModuleInterface<IAssetCache>();
            if (cache == null)
                return;

            WearableCacheItem[] items = sp.Appearance.WearableCacheItems;
            IAssetService assets = sp.Scene.AssetService;
            int recached = 0;
            foreach (BakeSlot slot in BakeLayerMap.Slots)
            {
                int idx = (int)slot.FaceIndex;
                UUID bakeId = UUID.Zero;
                byte[] data = null;

                if (items != null && idx >= 0 && idx < items.Length)
                {
                    WearableCacheItem item = items[idx];
                    bakeId = item.TextureID;
                    data = item.TextureAsset?.Data;
                }

                if (BakeLayerMap.IsUnsetTexture(bakeId))
                {
                    Primitive.TextureEntryFace[] faces = sp.Appearance.Texture.FaceTextures;
                    if (faces != null && idx >= 0 && idx < faces.Length && faces[idx] != null)
                        bakeId = faces[idx].TextureID;
                }

                if (BakeLayerMap.IsUnsetTexture(bakeId))
                    continue;

                if (data == null || data.Length == 0)
                {
                    AssetBase existing = cache.GetCached(bakeId.ToString());
                    if (existing?.Data == null || existing.Data.Length == 0)
                        existing = assets?.GetCached(bakeId.ToString());
                    data = existing?.Data;
                }
                if (data == null || data.Length == 0)
                    continue;

                AssetBase baked = CacheBakeAsset(cache, bakeId, sp.UUID, "SSBake " + slot.BakeType, data);
                if (items != null && idx >= 0 && idx < items.Length)
                    items[idx].TextureAsset = baked;

                Primitive.TextureEntryFace face = sp.Appearance.Texture.GetFace((uint)idx);
                if (face != null)
                    face.TextureID = bakeId;
                recached++;
            }

            if (recached > 0)
                m_log.InfoFormat("[SSBAKE]: recached {0} incoming bake(s) for {1} so others can fetch them now",
                    recached, sp.Name);
        }

        // Outfit / cache-check cancels an in-flight bake and starts over
        // from the current worn items. Force rebake (Ctrl-Alt-R) is ignored
        // while a bake is already queued or running, and for ForceBakeCooldownMs
        // after that force bake finishes, so repeated IncrementCOFVersion
        // does not start another full render.
        private void RequestBake(ScenePresence sp, string reason, bool force = false)
        {
            if (sp == null || sp.IsNPC || sp.IsChildAgent)
                return;

            // MakeRootAgent fires inside CompleteMovement while IsInTransit is
            // still true. Queue that bake and wait in the worker. Other
            // reasons (outfit / cache-check) must not start mid-transit.
            bool fromMakeRoot = string.Equals(reason, "MakeRootAgent", StringComparison.Ordinal);
            bool attachmentsArrived = string.Equals(reason, "AttachmentsArrived", StringComparison.Ordinal);
            bool viewerRebake = string.Equals(reason, "IncrementCOFVersion", StringComparison.Ordinal);
            bool arrivalPublish = fromMakeRoot || attachmentsArrived;
            if (sp.IsInTransit && !arrivalPublish)
                return;

            UUID id = sp.UUID;
            if (force && !AllowForceBake(id, sp.Name, reason))
                return;

            int gen = m_bakeGeneration.AddOrUpdate(id, 1, (k, v) => v + 1);
            CancellationTokenSource cts = new CancellationTokenSource();
            if (m_bakeCts.TryGetValue(id, out CancellationTokenSource previous) && previous != null)
            {
                try { previous.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            m_bakeCts[id] = cts;

            m_log.InfoFormat("[SSBAKE]: request bake for {0} ({1}, gen={2}){3}",
                sp.Name, reason, gen, sp.IsInTransit ? " (waiting for transit to end)" : "");

            CancellationToken token = cts.Token;
            bool forceBake = force;
            Util.FireAndForget(
                delegate
                {
                    try
                    {
                        if (!WaitForBakeSlot(id, BakeSlotWaitMs, token))
                        {
                            if (token.IsCancellationRequested || !IsLatestGen(id, gen))
                                return;
                            m_log.WarnFormat("[SSBAKE]: timed out waiting for previous bake to cancel for {0}",
                                sp.Name);
                        }

                        // Outfit bursts (NowWearing + cache-check) need a
                        // short settle. Incoming login and viewer Rebake
                        // Textures should publish as soon as the previous
                        // bake has cancelled.
                        if (!arrivalPublish && !viewerRebake)
                            Thread.Sleep(OutfitBurstSettleMs);
                        if (token.IsCancellationRequested || !IsLatestGen(id, gen))
                        {
                            m_log.DebugFormat("[SSBAKE]: cancelled bake gen {0} for {1} before start",
                                gen, sp.Name);
                            return;
                        }

                        if (!WaitUntilBakeable(sp, token, TransitWaitMs))
                        {
                            if (token.IsCancellationRequested || !IsLatestGen(id, gen))
                                return;
                            m_log.WarnFormat("[SSBAKE]: giving up bake for {0} ({1}): still in transit or left",
                                sp.Name, reason);
                            return;
                        }

                        if (arrivalPublish)
                        {
                            EnsureAttachmentsRezzed(sp, token);
                            // Ask the previous region for incoming bake UUIDs.
                            // Default or missing faces are not a full success:
                            // TryBake fills those from the wearable list.
                            TrySeedExistingBakes(sp);
                        }

                        bool ok = TryBake(sp, token, forceBake, arrivalPublish);

                        if (token.IsCancellationRequested || !IsLatestGen(id, gen))
                        {
                            m_log.InfoFormat("[SSBAKE]: cancelled bake gen {0} for {1}", gen, sp.Name);
                            return;
                        }

                        m_completedBakeGen.AddOrUpdate(id, gen, (k, v) => gen > v ? gen : v);
                        if (!ok)
                        {
                            m_log.WarnFormat("[SSBAKE]: bake gen {0} for {1} produced nothing; still replying to cache-check",
                                gen, sp.Name);
                            if (fromMakeRoot && CanBake(sp))
                            {
                                sp.SendAppearanceToAllOtherAgents();
                                m_log.InfoFormat("[SSBAKE]: bake produced nothing; sent existing appearance of {0} to others anyway",
                                    sp.Name);
                            }
                        }
                        FlushPendingCacheReplies(sp);
                    }
                    catch (OperationCanceledException)
                    {
                        m_log.InfoFormat("[SSBAKE]: cancelled bake gen {0} for {1}", gen, sp.Name);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("[SSBAKE]: bake failed for {0}: {1}", id, e);
                    }
                    finally
                    {
                        // Cooldown starts when the force bake finishes, not
                        // when it was requested. Set it before marking complete
                        // so a Ctrl-Alt-R in this window cannot sneak in.
                        if (forceBake && !token.IsCancellationRequested)
                            m_forceBakeReadyAt[id] = Environment.TickCount64 + ForceBakeCooldownMs;
                        // Unstick BakePendingOrRunning if this was the latest
                        // attempt (cancel/exception used to leave queued > done
                        // forever, so later viewer rebakes were ignored).
                        if (IsLatestGen(id, gen))
                            m_completedBakeGen.AddOrUpdate(id, gen, (k, v) => gen > v ? gen : v);
                        if (m_bakeCts.TryGetValue(id, out CancellationTokenSource cur) && ReferenceEquals(cur, cts))
                            m_bakeCts.TryRemove(id, out _);
                        try { cts.Dispose(); }
                        catch (ObjectDisposedException) { }
                    }
                }, null, "SSBake.Request");
        }

        private bool IsLatestGen(UUID id, int gen)
        {
            int latest;
            return !m_bakeGeneration.TryGetValue(id, out latest) || latest == gen;
        }

        private bool AllowForceBake(UUID id, string name, string reason)
        {
            if (BakePendingOrRunning(id))
            {
                m_log.InfoFormat("[SSBAKE]: ignoring force rebake for {0} ({1}); bake already in progress",
                    name, reason);
                return false;
            }

            if (m_forceBakeReadyAt.TryGetValue(id, out long readyAt))
            {
                long remain = readyAt - Environment.TickCount64;
                if (remain > 0)
                {
                    m_log.InfoFormat("[SSBAKE]: ignoring force rebake for {0} ({1}); cooldown {2}ms",
                        name, reason, remain);
                    return false;
                }
            }

            return true;
        }

        private bool BakePendingOrRunning(UUID id)
        {
            if (m_inProgress.ContainsKey(id))
                return true;
            int queued = 0;
            int done = 0;
            m_bakeGeneration.TryGetValue(id, out queued);
            m_completedBakeGen.TryGetValue(id, out done);
            return queued > done;
        }

        private bool WaitForBakeSlot(UUID id, int timeoutMs, CancellationToken token)
        {
            int waited = 0;
            while (m_inProgress.ContainsKey(id) && waited < timeoutMs)
            {
                if (token.IsCancellationRequested)
                    return false;
                Thread.Sleep(50);
                waited += 50;
            }
            return !m_inProgress.ContainsKey(id);
        }

        private static bool WaitUntilBakeable(ScenePresence sp, CancellationToken token, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (token.IsCancellationRequested || sp.IsDeleted)
                    return false;
                if (CanBake(sp))
                    return true;
                Thread.Sleep(50);
                waited += 50;
            }
            return CanBake(sp);
        }

        /// <summary>
        /// Wait for the incoming HG attachment batch, then rez from inventory
        /// if the source sent none. Must run after transit so RezAttachments
        /// is allowed to add objects.
        /// </summary>
        private void EnsureAttachmentsRezzed(ScenePresence sp, CancellationToken token)
        {
            int waited = 0;
            while (waited < AttachmentWaitMs)
            {
                if (token.IsCancellationRequested || sp.IsDeleted)
                    return;
                if (sp.GetAttachmentsCount() > 0 || sp.GotAttachmentsData)
                    break;
                Thread.Sleep(50);
                waited += 50;
            }

            if (sp.GetAttachmentsCount() > 0)
            {
                if (waited > 0)
                    m_log.InfoFormat("[SSBAKE]: {0} attachments arrived after {1}ms",
                        sp.Name, waited);
                return;
            }

            RestoreListedAttachmentsFromAvatarService(sp);

            int listed = sp.Appearance?.GetAttachments()?.Count ?? 0;
            if (listed == 0)
            {
                m_log.InfoFormat("[SSBAKE]: {0} has no live attachments and none listed in appearance",
                    sp.Name);
                return;
            }

            m_log.InfoFormat("[SSBAKE]: {0} still has 0 live attachments, {1} in appearance; rez from inventory",
                sp.Name, listed);
            sp.Scene.AttachmentsModule?.RezAttachments(sp);
            m_log.InfoFormat("[SSBAKE]: after rez {0} has {1} live attachment(s)",
                sp.Name, sp.GetAttachmentsCount());
        }

        /// <summary>
        /// HG return can arrive with an empty packed attachment list. Local
        /// users still have worn items in the avatar service.
        /// </summary>
        private static void RestoreListedAttachmentsFromAvatarService(ScenePresence sp)
        {
            if (sp.Appearance == null || sp.Appearance.GetAttachments().Count > 0)
                return;

            IUserManagement um = sp.Scene?.UserManagementModule;
            if (um == null || !um.IsLocalGridUser(sp.UUID))
                return;

            try
            {
                AvatarAppearance stored = sp.Scene.AvatarService?.GetAppearance(sp.UUID);
                List<AvatarAttachment> storedAtts = stored?.GetAttachments();
                if (storedAtts == null || storedAtts.Count == 0)
                    return;
                foreach (AvatarAttachment att in storedAtts)
                    sp.Appearance.SetAttachment(att.AttachPoint | 0x80, att.ItemID, att.AssetID);
                m_log.InfoFormat("[SSBAKE]: restored {0} attachment record(s) from avatar service for {1}",
                    storedAtts.Count, sp.Name);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[SSBAKE]: failed restoring attachments for {0}: {1}",
                    sp.Name, e.Message);
            }
        }

        public void PrepareAppearanceForTransfer(IScenePresence isp, AvatarAppearance outbound)
        {
            ScenePresence sp = isp as ScenePresence;
            if (sp == null || outbound == null)
                return;

            // Dest regions without SSA send AvatarAppearance version 0.
            // Firestorm 7.2 treats a non-zero vp 11000 as SSA and then
            // drops attachments when that packet has no AttachmentBlock.
            const int vp11000 = (int)AvatarAppearance.VPElement._APPEARANCEMESSAGE_VERSION;
            if (outbound.VisualParams != null && outbound.VisualParams.Length > vp11000)
                outbound.VisualParams[vp11000] = 0;

            SyncOutboundAttachments(sp, outbound);

            m_log.InfoFormat(
                "[SSBAKE]: transfer-out classic appearance for {0}: serial={1} listedAttachments={2} liveAttachments={3}",
                sp.Name,
                outbound.Serial,
                outbound.GetAttachments().Count,
                sp.GetAttachmentsCount());
        }

        /// <summary>
        /// packed_appearance.attachments is what dest writes into
        /// AvatarAppearance. Live SOGs go in attach_objects. They must agree
        /// or dest rezzes objects then a later appearance has an empty set.
        /// </summary>
        private static void SyncOutboundAttachments(ScenePresence sp, AvatarAppearance outbound)
        {
            List<SceneObjectGroup> live = sp.GetAttachments();
            if (live == null || live.Count == 0)
                return;

            outbound.ClearAttachments();
            foreach (SceneObjectGroup sog in live)
            {
                if (sog == null || sog.IsDeleted)
                    continue;
                UUID itemId = sog.FromItemID;
                if (itemId.IsZero())
                    continue;
                UUID assetId = UUID.Zero;
                AvatarAttachment existing = sp.Appearance?.GetAttachmentForItem(itemId);
                if (existing != null)
                    assetId = existing.AssetID;
                outbound.SetAttachment((int)sog.AttachmentPoint | 0x80, itemId, assetId);
            }
        }

        public void NotifyAttachmentsArrived(IScenePresence isp)
        {
            ScenePresence sp = isp as ScenePresence;
            if (sp == null || sp.IsDeleted || sp.IsChildAgent || sp.IsNPC)
                return;
            if (sp.GetAttachmentsCount() == 0)
                return;

            AdvertiseAttachments(sp);

            // MakeRootAgent bake still running: it will publish with the
            // live AttachmentBlock once it finishes. Otherwise this is a
            // late HG gather after we already sent AttachmentBlock=0.
            if (BakePendingOrRunning(sp.UUID))
                return;
            if (!CanBake(sp))
                return;

            m_log.InfoFormat("[SSBAKE]: attachments arrived for {0} after appearance; republish ({1} live)",
                sp.Name, sp.GetAttachmentsCount());
            RequestBake(sp, "AttachmentsArrived");
        }

        private static void AdvertiseAttachments(ScenePresence sp)
        {
            if (sp == null || sp.GetAttachmentsCount() == 0)
                return;
            List<ScenePresence> all = sp.Scene.GetScenePresences();
            foreach (ScenePresence p in all)
                sp.SendAttachmentsToAgentNF(p);
        }

        /// <summary>
        /// Copy bake JPEGs for incoming TextureEntry IDs: local Flotsam,
        /// local XBakes, then origin GET /appearance. Skips default faces.
        /// Does not replace TryBake — missing slots are filled from wearables.
        /// </summary>
        private void TrySeedExistingBakes(ScenePresence sp)
        {
            if (sp?.Appearance?.Texture?.FaceTextures == null)
                return;

            Scene scene = sp.Scene;
            IAssetCache cache = scene?.RequestModuleInterface<IAssetCache>();
            if (cache == null)
                return;

            WearableCacheItem[] xbakesStored = LoadXBakes(scene, sp.UUID, out _);
            string appearance = OriginAppearanceUrl(sp);
            if (!string.IsNullOrEmpty(appearance))
                m_log.InfoFormat("[SSBAKE]: seeding bakes for {0} via {1}", sp.Name, appearance);
            BakeAssetFetcher fetcher = new BakeAssetFetcher(m_log, scene.AssetService, cache);
            int seeded = 0;
            int missing = 0;
            foreach (BakeSlot slot in BakeLayerMap.Slots)
            {
                Primitive.TextureEntryFace face = sp.Appearance.Texture.FaceTextures[(int)slot.FaceIndex];
                if (face == null || BakeLayerMap.IsUnsetTexture(face.TextureID))
                {
                    missing++;
                    continue;
                }

                AssetBase asset = fetcher.GetBake(face.TextureID, sp.UUID, (int)slot.FaceIndex,
                    appearance, xbakesStored, out string src);
                if (asset?.Data == null || asset.Data.Length == 0)
                {
                    missing++;
                    m_log.DebugFormat("[SSBAKE]: seed miss {0} bake {1} for {2}",
                        slot.BakeType, face.TextureID, sp.Name);
                    continue;
                }

                seeded++;
                m_log.DebugFormat("[SSBAKE]: seeded {0} bake {1} from {2} for {3}",
                    slot.BakeType, face.TextureID, src, sp.Name);
            }

            if (seeded > 0 || missing > 0)
                m_log.InfoFormat("[SSBAKE]: seeded {0} existing bake(s) for {1} ({2} default/missing; will bake from wearables)",
                    seeded, sp.Name, missing);
        }

        private void RememberOriginAppearance(ScenePresence sp)
        {
            string url = AppearanceUrlFromOrigin(sp);
            if (string.IsNullOrEmpty(url))
                return;
            m_originAppearance[sp.UUID] = url;
            m_log.DebugFormat("[SSBAKE]: origin /appearance for {0} is {1}", sp.Name, url);
        }

        private string OriginAppearanceUrl(ScenePresence sp)
        {
            if (sp == null)
                return null;
            if (m_originAppearance.TryGetValue(sp.UUID, out string stored) && !string.IsNullOrEmpty(stored))
                return stored;
            return AppearanceUrlFromOrigin(sp);
        }

        private static string AppearanceUrlFromOrigin(ScenePresence sp)
        {
            string server = sp?.OriginServerURI;
            if (string.IsNullOrEmpty(server))
                return null;
            return server.TrimEnd('/') + "/appearance/";
        }

        private void FlushPendingCacheReplies(ScenePresence sp)
        {
            if (sp == null)
                return;
            if (!m_pendingCacheReplies.TryGetValue(sp.UUID, out ConcurrentQueue<PendingCacheReply> q))
                return;

            int sent = 0;
            while (q.TryDequeue(out PendingCacheReply pending))
            {
                if (pending?.Client == null || pending.Request == null)
                    continue;
                SendCachedTextureReply(pending.Client, sp, pending.Serial, pending.Request);
                sent++;
            }
            m_log.InfoFormat("[SSBAKE]: replied to {0} cache-check(s) for {1} with new bake UUIDs",
                sent, sp.Name);
        }

        // True when we have already published server bakes and the viewer's
        // TextureEntry no longer matches them (Ctrl-Alt-R local rebake).
        private bool ViewerBakesDifferFromLast(ScenePresence sp)
        {
            if (!CanBake(sp) || !m_lastBakeIds.TryGetValue(sp.UUID, out UUID[] faces))
                return false;

            foreach (BakeSlot slot in BakeLayerMap.Slots)
            {
                int idx = (int)slot.FaceIndex;
                UUID last = (idx >= 0 && idx < faces.Length) ? faces[idx] : UUID.Zero;
                Primitive.TextureEntryFace face = sp.Appearance.Texture.FaceTextures[idx];
                UUID current = (face == null) ? UUID.Zero : face.TextureID;
                if (BakeLayerMap.IsUnsetTexture(current))
                    current = UUID.Zero;
                if (BakeLayerMap.IsUnsetTexture(last))
                    last = UUID.Zero;
                if (current.NotEqual(last))
                    return true;
            }
            return false;
        }

        private bool RestoreLastBakes(ScenePresence sp)
        {
            if (!CanBake(sp) || !m_lastBakeIds.TryGetValue(sp.UUID, out UUID[] faces))
                return false;

            bool changed = false;
            WearableCacheItem[] wearableCache = sp.Appearance.WearableCacheItems;
            foreach (BakeSlot slot in BakeLayerMap.Slots)
            {
                int idx = (int)slot.FaceIndex;
                if (idx < 0 || idx >= faces.Length || faces[idx].IsZero())
                    continue;

                Primitive.TextureEntryFace face = sp.Appearance.Texture.GetFace((uint)idx);
                if (face == null)
                    continue;
                if (face.TextureID.NotEqual(faces[idx]))
                {
                    face.TextureID = faces[idx];
                    changed = true;
                }
                if (wearableCache != null && idx < wearableCache.Length)
                    wearableCache[idx].TextureID = faces[idx];
            }
            return changed;
        }

        private void RememberBake(UUID agentId, int face, UUID bakeId)
        {
            if (face < 0 || bakeId.IsZero())
                return;
            UUID[] faces = m_lastBakeIds.GetOrAdd(agentId, _ => new UUID[AvatarAppearance.TEXTURE_COUNT]);
            if (face < faces.Length)
                faces[face] = bakeId;
        }

        public bool TryBake(IScenePresence sp)
        {
            return TryBake(sp, false);
        }

        public bool TryBake(IScenePresence sp, bool force)
        {
            ScenePresence presence = sp as ScenePresence;
            if (presence == null || presence.IsNPC || presence.IsChildAgent)
                return false;
            RequestBake(presence, force ? "TryBake-force" : "TryBake", force);
            return true;
        }

        private bool TryBake(IScenePresence sp, CancellationToken token, bool force = false,
            bool preferIncoming = false)
        {
            if (sp == null || sp.IsNPC || sp.IsChildAgent)
                return false;

            if (!m_inProgress.TryAdd(sp.UUID, 1))
            {
                m_log.DebugFormat("[SSBAKE]: bake already in progress for {0}", sp.Name);
                return false;
            }

            try
            {
                return BakeNow(sp, token, force, preferIncoming);
            }
            finally
            {
                m_inProgress.TryRemove(sp.UUID, out _);
            }
        }

        private static bool CanBake(ScenePresence sp)
        {
            return sp != null && !sp.IsDeleted && !sp.IsChildAgent && !sp.IsInTransit
                && sp.Appearance?.Texture != null;
        }

        private bool BakeNow(IScenePresence isp, CancellationToken token, bool force = false,
            bool preferIncoming = false)
        {
            ScenePresence sp = isp as ScenePresence;
            if (!CanBake(sp) || token.IsCancellationRequested)
                return false;

            Scene scene = sp.Scene;
            IAssetCache cache = scene.RequestModuleInterface<IAssetCache>();
            if (cache == null)
            {
                m_log.Warn("[SSBAKE]: no IAssetCache; cannot bake");
                return false;
            }

            string foreign = ForeignAssetUrl(scene, sp.UUID);

            m_log.InfoFormat("[SSBAKE]: baking {0} ({1}) in {2} foreign={3}{4}",
                sp.Name, sp.UUID, scene.RegionInfo.RegionName,
                string.IsNullOrEmpty(foreign) ? "-" : foreign,
                force ? " force-rebake" : "");

            RefreshWearableAssets(sp, scene.InventoryService);
            if (token.IsCancellationRequested)
                return false;

            BakeAssetFetcher fetcher = new BakeAssetFetcher(m_log, scene.AssetService, cache);
            WearableTextureResolver resolver = new WearableTextureResolver(m_log, fetcher, scene.InventoryService, sp.UUID);
            Dictionary<BakeType, List<ResolvedLayer>> resolved = resolver.Resolve(sp.Appearance, foreign);
            if (token.IsCancellationRequested)
                return false;

            WearableCacheItem[] wearableCache = sp.Appearance.WearableCacheItems;
            if (wearableCache == null)
                wearableCache = WearableCacheItem.GetDefaultCacheItem();

            IBakedTextureModule xbakes;
            WearableCacheItem[] xbakesStored = LoadXBakes(scene, sp.UUID, out xbakes);

            int produced = 0;
            int rendered = 0;
            int reused = 0;
            foreach (BakeSlot slot in BakeLayerMap.Slots)
            {
                if (token.IsCancellationRequested)
                {
                    m_log.InfoFormat("[SSBAKE]: aborting bake for {0}, superseded", sp.Name);
                    return false;
                }

                if (!CanBake(sp))
                {
                    m_log.InfoFormat("[SSBAKE]: aborting bake, {0} is no longer in the region",
                        isp.UUID);
                    return false;
                }

                if (!resolved.TryGetValue(slot.BakeType, out List<ResolvedLayer> layers) || layers.Count == 0)
                {
                    m_log.DebugFormat("[SSBAKE]: {0}: no source layers, leaving default", slot.BakeType);
                    continue;
                }

                UUID bakeId = BakeId.FromLayers((int)slot.FaceIndex, layers, FillTintFor(slot.BakeType, resolver));
                if (bakeId.IsZero())
                    continue;

                AssetBase baked = null;
                UUID reuseId = bakeId;
                if (!force)
                {
                    // Arrival only: keep a bake UUID we just pulled from the
                    // previous region. Outfit change must use the wearable hash
                    // or we would republish the old clothes.
                    if (preferIncoming)
                    {
                        Primitive.TextureEntryFace incomingFace =
                            sp.Appearance.Texture.FaceTextures[(int)slot.FaceIndex];
                        UUID incomingId = incomingFace == null ? UUID.Zero : incomingFace.TextureID;
                        if (!BakeLayerMap.IsUnsetTexture(incomingId))
                        {
                            AssetBase seeded = cache.GetCached(incomingId.ToString());
                            if (seeded?.Data != null && seeded.Data.Length > 0)
                            {
                                baked = seeded;
                                reuseId = incomingId;
                            }
                        }
                    }
                    if (baked == null)
                        baked = TryReuseBake(bakeId, slot, cache, xbakesStored, sp.UUID);
                }
                if (baked != null)
                {
                    m_log.InfoFormat("[SSBAKE]: {0} reuse {1} ({2} layer(s))",
                        slot.BakeType, reuseId, layers.Count);
                    ApplyBake(sp, wearableCache, slot, bakeId, reuseId, baked);
                    produced++;
                    reused++;
                    continue;
                }

                BakeLayerImage[] images = new BakeLayerImage[layers.Count];
                int decoded = 0;
                for (int i = 0; i < layers.Count; i++)
                {
                    if (token.IsCancellationRequested)
                    {
                        m_log.InfoFormat("[SSBAKE]: aborting bake for {0}, superseded", sp.Name);
                        return false;
                    }

                    ResolvedLayer layer = layers[i];
                    AssetBase tex = fetcher.Get(layer.TextureID, foreign, out string src);
                    if (tex == null)
                    {
                        m_log.WarnFormat("[SSBAKE]:   layer {0} {1} missing ({2})",
                            layer.TextureIndex, layer.TextureID, src);
                        continue;
                    }

                    if (!TextureBaker.TryDecode(tex.Data, out ManagedImage img))
                    {
                        m_log.WarnFormat("[SSBAKE]:   layer {0} {1} J2K decode failed ({2} bytes from {3})",
                            layer.TextureIndex, layer.TextureID, tex.Data.Length, src);
                        continue;
                    }

                    images[i] = new BakeLayerImage
                    {
                        Image = img,
                        IsAlphaMask = layer.IsAlphaMask,
                        Tint = layer.Tint,
                        ApplyTint = !layer.IsAlphaMask && !BakeLayerMap.IsBodypaint(layer.TextureIndex)
                    };
                    decoded++;
                    m_log.DebugFormat("[SSBAKE]:   layer {0} {1} {2}x{3} from {4}{5}{6}",
                        layer.TextureIndex, layer.TextureID, img.Width, img.Height, src,
                        layer.IsAlphaMask ? " [alpha-mask]" : "",
                        images[i].ApplyTint ? " tint=" + DescribeTint(layer.Tint) : "");
                }

                if (decoded == 0)
                {
                    m_log.WarnFormat("[SSBAKE]: {0}: no layers decoded, skip", slot.BakeType);
                    continue;
                }

                int size = BakeLayerMap.SizeFor(slot, images);
                m_log.InfoFormat("[SSBAKE]: {0} rebake {1} ({2} layer(s) {3}x{4})",
                    slot.BakeType, bakeId, decoded, size, size);

                if (token.IsCancellationRequested)
                {
                    m_log.InfoFormat("[SSBAKE]: aborting bake for {0}, superseded", sp.Name);
                    return false;
                }

                m_bakeGate.Wait(token);
                byte[] j2k;
                try
                {
                    Color4 fill;
                    bool opaqueBody;
                    ManagedImage libColor;
                    ManagedImage libGrain;
                    LibraryBaseFor(slot.BakeType, resolver, out fill, out opaqueBody, out libColor, out libGrain);
                    ManagedImage composite = TextureBaker.Composite(size, size, images,
                        fill, opaqueBody, libColor, libGrain);
                    j2k = TextureBaker.Encode(composite);
                }
                finally
                {
                    m_bakeGate.Release();
                }

                if (j2k == null || j2k.Length == 0)
                {
                    m_log.WarnFormat("[SSBAKE]: {0}: encode failed", slot.BakeType);
                    continue;
                }

                // Hash stays the XBakes identity. On a forced rebake mint a
                // new TextureID so the viewer actually GET /appearance again
                // (same hash UUID is already in its texture cache).
                UUID fetchId = force ? UUID.Random() : bakeId;
                baked = CacheBakeAsset(cache, fetchId, sp.UUID, "SSBake " + slot.BakeType, j2k);
                if (fetchId.NotEqual(bakeId))
                    CacheBakeAsset(cache, bakeId, sp.UUID, "SSBake " + slot.BakeType, j2k);

                m_log.InfoFormat("[SSBAKE]: {0} encoded {1} bytes -> {2}{3}",
                    slot.BakeType, j2k.Length, fetchId,
                    fetchId.NotEqual(bakeId) ? " hash=" + bakeId : "");

                ApplyBake(sp, wearableCache, slot, bakeId, fetchId, baked);
                produced++;
                rendered++;
            }

            if (token.IsCancellationRequested)
            {
                m_log.InfoFormat("[SSBAKE]: aborting bake for {0}, superseded", sp.Name);
                return false;
            }

            sp.Appearance.WearableCacheItems = wearableCache;

            if (produced == 0)
            {
                m_log.WarnFormat("[SSBAKE]: produced no bakes for {0}", sp.Name);
                return false;
            }

            if (xbakes != null)
                StoreXBakes(xbakes, sp, wearableCache);

            bool isHG = !string.IsNullOrEmpty(foreign);
            if (!isHG)
                scene.AvatarFactory?.QueueAppearanceSave(sp.UUID);

            if (!CanBake(sp))
            {
                m_log.InfoFormat("[SSBAKE]: bake finished but {0} already left; not sending appearance",
                    isp.UUID);
                return produced > 0;
            }

            ReapplyBakes(sp, wearableCache);
            PublishAppearance(sp);

            m_log.InfoFormat("[SSBAKE]: done {0}: {1} bake(s) ({2} rendered, {3} reused){4}",
                sp.Name, produced, rendered, reused,
                isHG ? " (HG, not saved to avatar service)" : "");
            return true;
        }

        private WearableCacheItem[] LoadXBakes(Scene scene, UUID agentId, out IBakedTextureModule xbakes)
        {
            xbakes = scene.RequestModuleInterface<IBakedTextureModule>();
            if (xbakes == null)
                return null;

            try
            {
                WearableCacheItem[] stored = xbakes.Get(agentId);
                if (stored != null && stored.Length > 0)
                    m_log.InfoFormat("[SSBAKE]: XBakes has {0} stored bake(s) for {1}", stored.Length, agentId);
                return stored;
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[SSBAKE]: XBakes get failed for {0}: {1}", agentId, e.Message);
                xbakes = null;
                return null;
            }
        }

        private void StoreXBakes(IBakedTextureModule xbakes, ScenePresence sp, WearableCacheItem[] wearableCache)
        {
            int n = 0;
            foreach (BakeSlot slot in BakeLayerMap.Slots)
            {
                int idx = (int)slot.FaceIndex;
                if (idx >= 0 && idx < wearableCache.Length
                    && wearableCache[idx].TextureAsset?.Data != null
                    && wearableCache[idx].TextureAsset.Data.Length > 0)
                    n++;
            }
            if (n == 0)
                return;

            try
            {
                xbakes.Store(sp.UUID, wearableCache);
                m_log.InfoFormat("[SSBAKE]: stored {0} bake(s) to XBakes for {1}", n, sp.Name);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[SSBAKE]: XBakes store failed for {0}: {1}", sp.Name, e.Message);
            }
        }

        private static AssetBase TryReuseBake(UUID bakeId, BakeSlot slot, IAssetCache cache,
            WearableCacheItem[] xbakesStored, UUID agentId)
        {
            AssetBase cached = cache.GetCached(bakeId.ToString());
            if (cached?.Data != null && cached.Data.Length > 0)
                return cached;

            if (xbakesStored == null)
                return null;

            int idx = (int)slot.FaceIndex;
            for (int i = 0; i < xbakesStored.Length; i++)
            {
                WearableCacheItem item = xbakesStored[i];
                if (item == null || (int)item.TextureIndex != idx)
                    continue;
                if (item.TextureID.NotEqual(bakeId) && item.CacheId.NotEqual(bakeId))
                    continue;
                if (item.TextureAsset?.Data == null || item.TextureAsset.Data.Length == 0)
                    continue;
                return CacheBakeAsset(cache, bakeId, agentId, "SSBake " + slot.BakeType, item.TextureAsset.Data);
            }
            return null;
        }

        private static AssetBase CacheBakeAsset(IAssetCache cache, UUID bakeId, UUID agentId,
            string name, byte[] data)
        {
            AssetBase baked = new AssetBase(bakeId, name,
                (sbyte)AssetType.Texture, agentId.ToString());
            baked.Data = data;
            baked.Temporary = true;
            baked.Local = true;
            cache.Cache(baked, true);
            return baked;
        }

        private void RefreshWearableAssets(ScenePresence sp, IInventoryService inventory)
        {
            if (inventory == null || sp?.Appearance?.Wearables == null)
                return;

            for (int i = 0; i < sp.Appearance.Wearables.Length; i++)
            {
                AvatarWearable worn = sp.Appearance.Wearables[i];
                if (worn == null)
                    continue;
                for (int j = 0; j < worn.Count; j++)
                {
                    WearableItem item = worn[j];
                    if (item.ItemID.IsZero())
                        continue;
                    try
                    {
                        InventoryItemBase inv = inventory.GetItem(sp.UUID, item.ItemID);
                        if (inv != null && inv.AssetID.IsNotZero() && inv.AssetID.NotEqual(item.AssetID))
                        {
                            worn.Add(item.ItemID, inv.AssetID);
                            m_log.InfoFormat("[SSBAKE]: wearable {0} item {1} asset {2} -> {3}",
                                (WearableType)i, item.ItemID, item.AssetID, inv.AssetID);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void ReapplyBakes(ScenePresence sp, WearableCacheItem[] wearableCache)
        {
            if (wearableCache == null || !CanBake(sp))
                return;
            foreach (BakeSlot slot in BakeLayerMap.Slots)
            {
                int idx = (int)slot.FaceIndex;
                if (idx < 0 || idx >= wearableCache.Length)
                    continue;
                UUID bakeId = wearableCache[idx].TextureID;
                if (BakeLayerMap.IsUnsetTexture(bakeId))
                    continue;
                Primitive.TextureEntryFace face = sp.Appearance.Texture.GetFace((uint)idx);
                face.TextureID = bakeId;
            }
            sp.Appearance.WearableCacheItems = wearableCache;
        }

        private void PublishAppearance(ScenePresence sp)
        {
            if (!CanBake(sp))
                return;

            if (sp.Appearance.VisualParams == null || sp.Appearance.VisualParams.Length <= 1)
            {
                m_log.WarnFormat("[SSBAKE]: not sending appearance for {0}: visual params missing (viewer would ignore it)",
                    sp.Name);
                return;
            }

            // Self first. SendAppearance writes AppearanceData
            // (AppearanceVersion=1, CofVersion) and AttachmentBlock on the
            // UDP copy only — do not mutate live visual params.
            // Do not SendAvatarDataToAllAgents: a second avatar ObjectUpdate
            // after attachments are out can drop them on SSA viewers.
            ResolvePacketCof(sp.UUID);
            sp.SendAppearanceToAgentNF(sp);
            sp.SendAppearanceToAllOtherAgents();
            AdvertiseAttachments(sp);
            int cof;
            if (!m_packetCof.TryGetValue(sp.UUID, out cof) || cof <= 0)
                cof = 1;
            m_log.InfoFormat("[SSBAKE]: sent appearance cof={0} for {1} (attachments={2})",
                cof, sp.Name, sp.GetAttachmentsCount());
        }

        // AvatarAppearance CofVersion. After IncrementCOFVersion Firestorm
        // slams lastRcv to the returned version, so the next packet must be
        // strictly greater or the viewer drops it. Login/outfit still use
        // the viewer's COF when that is ahead of the last packet.
        private void ResolvePacketCof(UUID agentId)
        {
            int viewer = 1;
            int storedViewer;
            if (m_viewerCof.TryGetValue(agentId, out storedViewer) && storedViewer > 0)
                viewer = storedViewer;
            int packet = 0;
            m_packetCof.TryGetValue(agentId, out packet);
            if (packet >= viewer)
                packet++;
            else
                packet = viewer;
            m_packetCof[agentId] = packet;
        }

        private static Color4 FillTintFor(BakeType bakeType, WearableTextureResolver resolver)
        {
            switch (bakeType)
            {
                case BakeType.Head:
                case BakeType.UpperBody:
                case BakeType.LowerBody:
                    return resolver.SkinTint;
                case BakeType.Hair:
                    return resolver.HairTint;
                case BakeType.Eyes:
                    return resolver.EyesTint;
                default:
                    return Color4.Black;
            }
        }

        private static void LibraryBaseFor(BakeType bakeType, WearableTextureResolver resolver,
            out Color4 fill, out bool opaqueBody, out ManagedImage libraryColor, out ManagedImage libraryGrain)
        {
            fill = FillTintFor(bakeType, resolver);
            opaqueBody = false;
            libraryColor = null;
            libraryGrain = null;
            switch (bakeType)
            {
                case BakeType.Head:
                    opaqueBody = true;
                    libraryColor = TextureBaker.TryLoadResource("head_color.tga");
                    libraryGrain = TextureBaker.TryLoadResource("head_skingrain.tga");
                    break;
                case BakeType.UpperBody:
                    opaqueBody = true;
                    libraryColor = TextureBaker.TryLoadResource("upperbody_color.tga");
                    break;
                case BakeType.LowerBody:
                    opaqueBody = true;
                    libraryColor = TextureBaker.TryLoadResource("lowerbody_color.tga");
                    break;
                case BakeType.Eyes:
                    opaqueBody = true;
                    break;
            }
        }

        private static string DescribeTint(Color4 c)
        {
            return string.Format("{0:0.00},{1:0.00},{2:0.00}", c.R, c.G, c.B);
        }

        private static string ForeignAssetUrl(Scene scene, UUID agentId)
        {
            AgentCircuitData circuit = scene?.AuthenticateHandler?.GetAgentCircuitData(agentId);
            if (circuit?.ServiceURLs != null
                && circuit.ServiceURLs.TryGetValue("AssetServerURI", out object urlObj))
                return urlObj?.ToString();
            return null;
        }

        private bool ApplyBake(ScenePresence sp, WearableCacheItem[] wearableCache, BakeSlot slot,
            UUID cacheId, UUID textureId, AssetBase baked)
        {
            if (!CanBake(sp) || wearableCache == null)
                return false;

            int idx = (int)slot.FaceIndex;
            Primitive.TextureEntryFace face = sp.Appearance.Texture.GetFace((uint)idx);
            if (face == null)
                return false;
            UUID previous = face.TextureID;
            face.TextureID = textureId;
            RememberBake(sp.UUID, idx, textureId);
            bool changed = previous.NotEqual(textureId);
            if (changed)
                m_log.InfoFormat("[SSBAKE]: {0} TextureID {1} -> {2}", slot.BakeType, previous, textureId);

            if (idx >= 0 && idx < wearableCache.Length)
            {
                wearableCache[idx].TextureIndex = (uint)idx;
                wearableCache[idx].TextureID = textureId;
                wearableCache[idx].CacheId = cacheId;
                wearableCache[idx].TextureAsset = baked;
            }

            return changed;
        }

        private void HandleBakeCommand(string module, string[] cmd)
        {
            if (cmd.Length < 4)
            {
                MainConsole.Instance.Output("Usage: ssb bake <first-name> <last-name>");
                return;
            }

            string first = cmd[2];
            string last = cmd[3];
            bool found = false;

            lock (m_scenes)
            {
                foreach (Scene scene in m_scenes)
                {
                    ScenePresence sp = scene.GetScenePresence(first, last);
                    if (sp == null || sp.IsChildAgent)
                        continue;

                    found = true;
                    MainConsole.Instance.Output("Queueing server-side bake for {0} in {1}",
                        sp.Name, scene.RegionInfo.RegionName);
                    TryBake(sp, true);
                }
            }

            if (!found)
                MainConsole.Instance.Output("Avatar {0} {1} not found as a root agent", first, last);
        }
    }
}
