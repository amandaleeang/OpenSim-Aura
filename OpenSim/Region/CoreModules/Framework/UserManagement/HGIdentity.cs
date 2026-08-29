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
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Connectors.Hypergrid;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.Framework.UserManagement
{
    /// <summary>
    /// Shared HG identity resolution for friends (and IM/profile twins).
    /// HomeURI from circuit / UserManagement / local GateKeeperURL.
    /// FriendsServerURI advertised or HomeURI. UUI from cache, this-sim circuit, or requester-home get_uui.
    /// </summary>
    public static class HGIdentity
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(HGIdentity));

        public static AgentCircuitData GetCircuit(Scene scene, UUID userId)
        {
            if (scene?.AuthenticateHandler is null || userId.IsZero())
                return null;
            try
            {
                return scene.AuthenticateHandler.GetAgentCircuitData(userId);
            }
            catch (Exception e)
            {
                m_log.Debug($"[HG IDENTITY]: GetCircuit({userId}) failed: {e.Message}");
                return null;
            }
        }

        public static string NormalizeUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return string.Empty;
            OSHHTPHost host = new(uri);
            return host.IsValidHost ? host.URI : string.Empty;
        }

        /// <summary>
        /// Circuit HomeURI → UserManagement.GetUserHomeURL → SceneGridInfo.GateKeeperURL for locals.
        /// </summary>
        public static string ResolveHomeURI(Scene scene, IUserManagement um, UUID userId,
            AgentCircuitData circuit = null)
        {
            circuit ??= GetCircuit(scene, userId);
            string fromCircuit = HomeUriFromCircuit(circuit);
            if (fromCircuit.Length > 0)
                return fromCircuit;

            if (um is not null)
            {
                string home = NormalizeUri(um.GetUserHomeURL(userId));
                if (home.Length > 0)
                    return home;

                if (um.IsLocalGridUser(userId))
                {
                    string gk = NormalizeUri(scene?.SceneGridInfo?.GateKeeperURL);
                    if (gk.Length > 0)
                        return gk;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// TryGetValue("FriendsServerURI") then UserManagement FriendsServerURI (HomeURI fallback) then ResolveHomeURI.
        /// Never uses ServiceURLs indexer.
        /// </summary>
        public static string ResolveFriendsServerURI(Scene scene, IUserManagement um, UUID userId,
            AgentCircuitData circuit = null)
        {
            circuit ??= GetCircuit(scene, userId);
            if (circuit?.ServiceURLs is not null
                    && circuit.ServiceURLs.TryGetValue("FriendsServerURI", out object fsu) && fsu != null)
            {
                string advertised = NormalizeUri(fsu.ToString());
                if (advertised.Length > 0)
                    return advertised;
            }

            if (um is not null)
            {
                string fromUm = um.GetUserServerURL(userId, "FriendsServerURI");
                if (!string.IsNullOrWhiteSpace(fromUm))
                    return fromUm;
            }

            return ResolveHomeURI(scene, um, userId, circuit);
        }

        /// <summary>
        /// UserManagement full UUI → target circuit ProduceUserUniversalIdentifier → requester-home get_uui.
        /// Fail if unknown. <paramref name="getUui"/> is for tests; production uses UserAgentServiceConnector.
        /// </summary>
        public static bool TryResolveUUI(Scene scene, IUserManagement um, UUID requesterId, UUID targetId,
            out string uui, Func<string, UUID, UUID, string> getUui = null)
        {
            uui = string.Empty;
            if (targetId.IsZero())
                return false;

            if (um is not null && um.GetUserUUI(targetId, out string cached) && IsFullUui(cached))
            {
                uui = WithoutSecret(cached);
                return true;
            }

            AgentCircuitData targetCircuit = GetCircuit(scene, targetId);
            if (targetCircuit is not null)
            {
                string produced = Util.ProduceUserUniversalIdentifier(targetCircuit);
                if (IsFullUui(produced))
                {
                    uui = produced;
                    return true;
                }
            }

            string home = ResolveHomeURI(scene, um, requesterId);
            if (string.IsNullOrWhiteSpace(home))
                return false;

            string remote;
            try
            {
                remote = getUui is not null
                    ? getUui(home, requesterId, targetId)
                    : QueryHomeUui(home, requesterId, targetId);
            }
            catch (Exception e)
            {
                m_log.Debug($"[HG IDENTITY]: get_uui({requesterId},{targetId}) at {home} failed: {e.Message}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(remote)
                    || !Util.ParseUniversalUserIdentifier(remote, out UUID uid, out string rhome, out string first, out string last, out _))
                return false;
            if (uid.IsZero() || string.IsNullOrWhiteSpace(rhome))
                return false;

            RememberContact(scene, um, uid, first, last, rhome);
            uui = WithoutSecret(remote);
            return IsFullUui(uui);
        }

        /// <summary>
        /// Seed UserManagement and GridUser from a UUI. Same store get_uui reads
        /// for friends, IM contacts, and visitors.
        /// </summary>
        public static void RememberContact(Scene scene, IUserManagement um,
            UUID userId, string first, string last, string home)
        {
            if (userId.IsZero() || string.IsNullOrWhiteSpace(home))
                return;

            string normalized = NormalizeUri(home);
            if (normalized.Length == 0)
                return;

            if (string.IsNullOrWhiteSpace(first))
                first = "Unknown";
            if (string.IsNullOrWhiteSpace(last) || last.StartsWith('@'))
                last = "User";

            um?.AddUser(userId, first, last, normalized);

            if (scene?.GridUserService is null)
                return;
            if (um is not null && um.IsLocalGridUser(userId))
                return;

            try
            {
                IGridUserService gridUser = scene.GridUserService;
                GridUserInfo existing = gridUser.GetGridUserInfo(userId.ToString());
                if (existing is not null && !string.IsNullOrEmpty(existing.UserID) && existing.UserID.Length > 36)
                    return;

                string uui = GridInstantMessage.BuildUUI(userId, first + " " + last, normalized);
                if (string.IsNullOrEmpty(uui))
                    return;
                gridUser.SetLastPosition(uui, UUID.Zero, UUID.Zero, Vector3.Zero, Vector3.Zero);
                m_log.DebugFormat("[HG IDENTITY]: Remembered contact {0} {1} @ {2}", first, last, normalized);
            }
            catch (Exception e)
            {
                m_log.Debug($"[HG IDENTITY]: Failed to persist contact {userId}: {e.Message}");
            }
        }

        public static bool IsFullUui(string uui)
        {
            return !string.IsNullOrEmpty(uui) && Util.ParseFullUniversalUserIdentifier(uui, out UUID _);
        }

        public static string WithoutSecret(string uui)
        {
            if (!Util.ParseUniversalUserIdentifier(uui, out UUID id, out string url, out string first, out string last, out _))
                return uui ?? string.Empty;
            if (string.IsNullOrEmpty(url))
                return id.ToString();
            return GridInstantMessage.BuildUUI(id, first + " " + last, url);
        }

        static string HomeUriFromCircuit(AgentCircuitData circuit)
        {
            if (circuit?.ServiceURLs is null)
                return string.Empty;
            if (!circuit.ServiceURLs.TryGetValue("HomeURI", out object hu) || hu is null)
                return string.Empty;
            return NormalizeUri(hu.ToString());
        }

        static string QueryHomeUui(string homeUri, UUID requesterId, UUID targetId)
        {
            UserAgentServiceConnector uasConn = new(homeUri);
            return uasConn.GetUUI(requesterId, targetId) ?? string.Empty;
        }
    }
}
