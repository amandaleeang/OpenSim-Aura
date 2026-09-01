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
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using log4net;
using Nini.Config;
using Nwc.XmlRpc;
using Mono.Addins;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Connectors.Friends;
using OpenSim.Services.Connectors.Hypergrid;
using OpenSim.Services.Connectors.InstantMessage;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;
using PresenceInfo = OpenSim.Services.Interfaces.PresenceInfo;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.CoreModules.Avatar.Friends
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "HGFriendsModule")]
    public class HGFriendsModule : FriendsModule, ISharedRegionModule, IFriendsModule, IFriendsSimConnector
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private int m_levelHGFriends = 0;
        private string m_MessageKey = string.Empty;

        IUserManagement m_uMan;
        public IUserManagement UserManagementModule
        {
            get
            {
                m_uMan ??= m_Scenes[0].RequestModuleInterface<IUserManagement>();
                return m_uMan;
            }
        }

        protected HGFriendsServicesConnector m_HGFriendsConnector = new();
        protected HGStatusNotifier m_StatusNotifier;

        #region ISharedRegionModule
        public override string Name
        {
            get { return "HGFriendsModule"; }
        }

        public override void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            base.AddRegion(scene);
            scene.RegisterModuleInterface<IFriendsSimConnector>(this);
            scene.EventManager.OnIncomingInstantMessage += OnIncomingFriendshipIM;
        }

        public override void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;
            if (m_StatusNotifier == null)
                m_StatusNotifier = new HGStatusNotifier(this);
        }

        public override void RemoveRegion(Scene scene)
        {
            if (m_Enabled)
                scene.EventManager.OnIncomingInstantMessage -= OnIncomingFriendshipIM;
            base.RemoveRegion(scene);
        }

        protected override void InitModule(IConfigSource config)
        {
            base.InitModule(config);

            // Additionally to the base method
            IConfig friendsConfig = config.Configs["HGFriendsModule"];
            if (friendsConfig != null)
            {
                m_levelHGFriends = friendsConfig.GetInt("LevelHGFriends", 0);

                // TODO: read in all config variables pertaining to
                // HG friendship permissions
            }
            IConfig messaging = config.Configs["Messaging"];
            if (messaging is not null)
                m_MessageKey = messaging.GetString("MessageKey", string.Empty);
        }

        #endregion

        #region IFriendsSimConnector

        /// <summary>
        /// Notify the user that the friend's status changed
        /// </summary>
        /// <param name="userID">user to be notified</param>
        /// <param name="friendID">friend whose status changed</param>
        /// <param name="online">status</param>
        /// <returns></returns>
        public bool StatusNotify(UUID friendID, UUID userID, bool online)
        {
            return LocalStatusNotification(friendID, userID, online);
        }

        #endregion

        protected override void OnInstantMessage(IClientAPI client, GridInstantMessage im)
        {
            if ((InstantMessageDialog)im.dialog == InstantMessageDialog.FriendshipOffered)
            {
                // we got a friendship offer
                UUID principalID = new(im.fromAgentID);
                UUID friendID = new(im.toAgentID);

                // Check if friendID is foreigner and if principalID has the permission
                // to request friendships with foreigners. If not, return immediately.
                if (!UserManagementModule.IsLocalGridUser(friendID))
                {
                    ((Scene)client.Scene).TryGetScenePresence(principalID, out ScenePresence avatar);
                    if (avatar is null)
                        return;

                    if (avatar.GodController.UserLevel < m_levelHGFriends)
                    {
                        client.SendAgentAlertMessage("Unable to send friendship invitation to foreigner. Insufficient permissions.", false);
                        return;
                    }
                }
            }

            base.OnInstantMessage(client, im);
        }

        protected override void OnApproveFriendRequest(IClientAPI client, UUID friendID, List<UUID> callingCardFolders)
        {
            // Update the local cache. Yes, we need to do it right here
            // because the HGFriendsService placed something on the DB
            // from under the sim
            base.OnApproveFriendRequest(client, friendID, callingCardFolders);
        }

        protected override bool CacheFriends(IClientAPI client)
        {
            //m_log.DebugFormat("[HGFRIENDS MODULE]: Entered CacheFriends for {0}", client.Name);

            if (base.CacheFriends(client))
            {
                // we do this only for the root agent
                UserFriendData FriendData = m_Friends[client.AgentId];
                if (FriendData.Refcount == 1)
                {
                    IUserManagement uMan = m_Scenes[0].RequestModuleInterface<IUserManagement>();
                    if(uMan == null)
                        return true;
                    // We need to preload the user management cache with the names
                    // of foreign friends, just like we do with SOPs' creators
                    foreach (FriendInfo finfo in FriendData.Friends)
                    {
                        if (finfo?.Friend is null)
                            continue;
                        // Seed HomeURL for HG friends so profile lookup can call their grid
                        // without asking the visitor's home again.
                        if (Util.ParseFullUniversalUserIdentifier(finfo.Friend, out UUID id, out string url, out string first, out string last))
                            uMan.AddUser(id, first, last, url);
                    }

                    //m_log.DebugFormat("[HGFRIENDS MODULE]: Exiting CacheFriends for {0} since detected root agent", client.Name);
                    return true;
                }
            }

            //m_log.DebugFormat("[HGFRIENDS MODULE]: Exiting CacheFriends for {0} since detected not root agent", client.Name);
            return false;
        }

        public override bool SendFriendsOnlineIfNeeded(IClientAPI client)
        {
            //m_log.DebugFormat("[HGFRIENDS MODULE]: Entering SendFriendsOnlineIfNeeded for {0}", client.Name);

            if (base.SendFriendsOnlineIfNeeded(client))
            {
                AgentCircuitData aCircuit = ((Scene)client.Scene).AuthenticateHandler.GetAgentCircuitData(client.AgentId);
                if (aCircuit is not null && (aCircuit.teleportFlags & (uint)Constants.TeleportFlags.ViaHGLogin) != 0)
                {
                    UserAccount account = m_Scenes[0].UserAccountService.GetUserAccount(client.Scene.RegionInfo.ScopeID, client.AgentId);
                    if (account is null) // foreign
                    {
                        FriendInfo[] friends = GetFriendsFromCache(client.AgentId);
                        foreach (FriendInfo f in friends)
                        {
                            int rights = f.TheirFlags;
                            if(rights != -1 )
                                client.SendChangeUserRights(new UUID(f.Friend), client.AgentId, rights);
                        }
                    }
                }
            }

            //m_log.DebugFormat("[HGFRIENDS MODULE]: Exiting SendFriendsOnlineIfNeeded for {0}", client.Name);
            return false;
        }

        protected override void GetOnlineFriends(UUID userID, List<string> friendList, /*collector*/ List<UUID> online)
        {
            //m_log.DebugFormat("[HGFRIENDS MODULE]: Entering GetOnlineFriends for {0}", userID);

            List<string> fList = new();
            foreach (string s in friendList)
            {
                if (s.Length < 36)
                    m_log.WarnFormat(
                        "[HGFRIENDS MODULE]: Ignoring friend {0} ({1} chars) for {2} since identifier too short",
                        s, s.Length, userID);
                else
                    fList.Add(s.Substring(0, 36));
            }

            // FIXME: also query the presence status of friends in other grids (like in HGStatusNotifier.Notify())

            PresenceInfo[] presence = PresenceService.GetAgents(fList.ToArray());
            if (presence.Length == 0)
                return;

            if (!m_OnlineFriendsCache.TryGetValue(userID, out HashSet<UUID> friends))
            {
                friends = new HashSet<UUID>();
                m_OnlineFriendsCache[userID] = friends;
            }

            foreach (PresenceInfo pi in presence)
            {
                if (UUID.TryParse(pi.UserID, out UUID presenceID))
                {
                    online.Add(presenceID);
                    friends.Add(presenceID);
                }
            }

            //m_log.DebugFormat("[HGFRIENDS MODULE]: Exiting GetOnlineFriends for {0}", userID);
        }

        protected override void StatusNotify(List<FriendInfo> friendList, UUID userID, bool online)
        {
            //m_log.DebugFormat("[HGFRIENDS MODULE]: Entering StatusNotify for {0}", userID);

            // First, let's divide the friends on a per-domain basis
            List<FriendInfo> locallst = new(friendList.Count);

            Dictionary<string, List<FriendInfo>> friendsPerDomain = new Dictionary<string, List<FriendInfo>>();
            foreach (FriendInfo friend in friendList)
            {
                if (UUID.TryParse(friend.Friend, out UUID friendID))
                {
                    if (LocalStatusNotification(userID, friendID, online))
                        continue;
                    locallst.Add(friend);
                }
                else
                {
                    // it's a foreign friend
                    if (Util.ParseUniversalUserIdentifier(friend.Friend, out friendID, out string url))
                    {
                        // Let's try our luck in the local sim. Who knows, maybe it's here
                        if (LocalStatusNotification(userID, friendID, online))
                            continue;

                        if (!friendsPerDomain.TryGetValue(url, out List<FriendInfo> lst))
                        {
                            lst = new List<FriendInfo>();
                            friendsPerDomain[url] = lst;
                        }
                        lst.Add(friend);
                    }
                }
            }

            // For the local friends, just call the base method
            // Let's do this first of all
            if (locallst.Count > 0)
                base.StatusNotify(locallst, userID, online);

            if(friendsPerDomain.Count > 0)
                m_StatusNotifier.Notify(userID, friendsPerDomain, online);

            //m_log.DebugFormat("[HGFRIENDS MODULE]: Exiting StatusNotify for {0}", userID);
        }

        protected override bool GetAgentInfo(UUID scopeID, string fid, out UUID agentID, out string first, out string last)
        {
            first = "Unknown"; last = "UserHGGAI";
            if (base.GetAgentInfo(scopeID, fid, out agentID, out first, out last))
                return true;

            // fid is not a UUID...
            if (Util.ParseFullUniversalUserIdentifier(fid, out agentID, out string url, out string f, out string l))
            {
                if (agentID.IsNotZero())
                {
                    m_uMan.AddUser(agentID, f, l, url);

                    string name = m_uMan.GetUserName(agentID);
                    string[] parts = name.Trim().Split();
                    if (parts.Length == 2)
                    {
                        first = parts[0];
                        last = parts[1];
                    }
                    else
                    {
                        first = f;
                        last = l;
                    }
                    return true;
                }
            }
            return false;
        }

        protected override string GetFriendshipRequesterName(UUID agentID)
        {
            return m_uMan.GetUserName(agentID);
        }

        protected override string FriendshipMessage(string friendID)
        {
            if (UUID.TryParse(friendID, out UUID _))
                return base.FriendshipMessage(friendID);

            return "Please confirm this friendship you made while you were on another HG grid";
        }

        protected override FriendInfo GetFriend(FriendInfo[] friends, UUID friendID)
        {
            if(friends.Length > 0)
            {
                string friendIDstr = friendID.ToString();
                foreach (FriendInfo fi in friends)
                {
                    if (fi.Friend.StartsWith(friendIDstr))
                        return fi;
                }
            }
            return null;
        }

        public override FriendInfo[] GetFriendsFromService(IClientAPI client)
        {
            //m_log.DebugFormat("[HGFRIENDS MODULE]: Entering GetFriendsFromService for {0}", client.Name);
            bool agentIsLocal = true;
            if (UserManagementModule is not null)
                agentIsLocal = UserManagementModule.IsLocalGridUser(client.AgentId);

            if (agentIsLocal)
                return base.GetFriendsFromService(client);

            // Foreigner — local DB only has friendships made while visiting this grid.
            // Pull the full list from home FriendsServerURI so CacheFriends can seed
            // UserManagement (HomeURL) and profile lookup for same-home friends works.
            AgentCircuitData agentClientCircuit = ((Scene)(client.Scene)).AuthenticateHandler.GetAgentCircuitData(client.CircuitCode);
            if (agentClientCircuit is null)
                return Array.Empty<FriendInfo>();

            List<FriendInfo> all = new();

            FriendInfo[] localFriends = FriendsService.GetFriends(client.AgentId.ToString());
            if (localFriends is not null && localFriends.Length > 0)
                all.AddRange(localFriends);

            m_log.DebugFormat(
                "[HGFRIENDS MODULE]: Fetched {0} local friends for visitor {1}",
                localFriends?.Length ?? 0, client.AgentId);

            try
            {
                string friendsUri = HGIdentity.ResolveFriendsServerURI(
                    (Scene)client.Scene, UserManagementModule, client.AgentId, agentClientCircuit);

                if (!string.IsNullOrWhiteSpace(friendsUri))
                {
                    var homeFriends = new FriendsServicesConnector(friendsUri);
                    FriendInfo[] remote = homeFriends.GetFriends(client.AgentId);
                    if (remote is not null && remote.Length > 0)
                    {
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (FriendInfo f in all)
                        {
                            if (f?.Friend is not null)
                                seen.Add(f.Friend);
                        }
                        int added = 0;
                        foreach (FriendInfo f in remote)
                        {
                            if (f?.Friend is null || seen.Contains(f.Friend))
                                continue;
                            all.Add(f);
                            seen.Add(f.Friend);
                            added++;
                        }
                        m_log.DebugFormat(
                            "[HGFRIENDS MODULE]: Fetched {0} home friends for visitor {1} from {2} ({3} new)",
                            remote.Length, client.Name, friendsUri, added);
                    }
                }
                else
                {
                    m_log.DebugFormat(
                        "[HGFRIENDS MODULE]: No FriendsServerURI/HomeURI for visitor {0}; only local friends available",
                        client.Name);
                }
            }
            catch (Exception e)
            {
                m_log.DebugFormat(
                    "[HGFRIENDS MODULE]: Failed to fetch home friends for visitor {0}: {1}",
                    client.Name, e.Message);
            }

            return all.ToArray();
        }

        protected override bool StoreRights(UUID agentID, UUID friendID, int rights)
        {
            bool agentIsLocal = true;
            bool friendIsLocal = true;
            if (UserManagementModule != null)
            {
                agentIsLocal = UserManagementModule.IsLocalGridUser(agentID);
                friendIsLocal = UserManagementModule.IsLocalGridUser(friendID);
            }

            // Are they both local users?
            if (agentIsLocal && friendIsLocal)
            {
                // local grid users
                return base.StoreRights(agentID, friendID, rights);
            }

            if (agentIsLocal) // agent is local, friend is foreigner
            {
                FriendInfo[] finfos = GetFriendsFromCache(agentID);
                FriendInfo finfo = GetFriend(finfos, friendID);
                if (finfo != null)
                {
                    FriendsService.StoreFriend(agentID.ToString(), finfo.Friend, rights);
                    return true;
                }
            }

            if (friendIsLocal) // agent is foreigner, friend is local
            {
                string agentUUI = GetUUI(friendID, agentID);
                if (agentUUI != string.Empty)
                {
                    FriendsService.StoreFriend(agentUUI, friendID.ToString(), rights);
                    return true;
                }
            }

            return false;
        }

        protected override void StoreBackwards(UUID friendID, UUID agentID)
        {
            bool askerLocal = UserManagementModule is null || UserManagementModule.IsLocalGridUser(agentID);
            bool recipientLocal = UserManagementModule is null || UserManagementModule.IsLocalGridUser(friendID);

            if (askerLocal)
            {
                m_log.DebugFormat("[HGFRIENDS MODULE]: Friendship requester is local. Storing backwards.");
                base.StoreBackwards(friendID, agentID);
                return;
            }

            if (recipientLocal)
            {
                // Visitor asked our local user. We are the recipient home — park pending here.
                string from = agentID.ToString();
                AgentCircuitData circuit = FindCircuit(agentID);
                if (circuit is not null)
                {
                    string uui = Util.ProduceUserUniversalIdentifier(circuit);
                    if (IsFullUui(uui))
                        from = uui;
                }
                FriendsService.StoreFriend(friendID.ToString(), from, 0);
                m_log.DebugFormat("[HGFRIENDS MODULE]: Stored pending for local {0} from visitor {1}", friendID, from);
            }
        }

        protected override bool StoreFriendships(UUID agentID, UUID friendID)
        {
            bool agentIsLocal = true;
            bool friendIsLocal = true;
            if (UserManagementModule != null)
            {
                agentIsLocal = UserManagementModule.IsLocalGridUser(agentID);
                friendIsLocal = UserManagementModule.IsLocalGridUser(friendID);
            }

            if (agentIsLocal && friendIsLocal)
            {
                m_log.DebugFormat("[HGFRIENDS MODULE]: Users are both local");
                DeletePreviousHGRelations(agentID, friendID);
                return base.StoreFriendships(agentID, friendID);
            }

            IClientAPI agentClient = LocateClientObject(agentID);
            IClientAPI friendClient = LocateClientObject(friendID);
            AgentCircuitData agentClientCircuit = FindCircuit(agentID);
            AgentCircuitData friendClientCircuit = FindCircuit(friendID);

            Scene scene = m_Scenes.Count > 0 ? m_Scenes[0] : null;
            if (agentClient is not null)
            {
                scene = (Scene)agentClient.Scene;
                RecacheFriends(agentClient);
            }
            if (friendClient is not null)
            {
                scene ??= (Scene)friendClient.Scene;
                RecacheFriends(friendClient);
            }

            IUserManagement um = UserManagementModule;
            string agentUUI = CircuitUui(agentClientCircuit);
            string friendUUI = CircuitUui(friendClientCircuit);
            string agentFriendService = HGIdentity.ResolveFriendsServerURI(scene, um, agentID, agentClientCircuit);
            string friendFriendService = HGIdentity.ResolveFriendsServerURI(scene, um, friendID, friendClientCircuit);

            m_log.DebugFormat("[HGFRIENDS MODULE] HG Friendship! thisUUI={0}; friendUUI={1}; foreignThisFriendService={2}; foreignFriendFriendService={3}",
                    agentUUI, friendUUI, agentFriendService, friendFriendService);

            string secret = UUID.Random().ToString().Substring(0, 8);

            if (agentIsLocal)
            {
                agentUUI = EnsureUui(agentUUI, agentID, true, UUID.Zero);
                friendUUI = EnsureUui(friendUUI, friendID, false, agentID);
                if (!IsFullUui(agentUUI) || !IsFullUui(friendUUI))
                {
                    m_log.WarnFormat("[HGFRIENDS MODULE]: Accept failed: missing UUI agent={0} friend={1}", agentUUI, friendUUI);
                    return false;
                }
                // Session if B is on this grid; else phase 4 unverified to HomeB.
                if (!InformHome(friendID, friendClientCircuit, friendFriendService, agentUUI + ";" + secret, true))
                    return false;
                return StoreAcceptedLocal(agentID, friendID, friendUUI + ";" + secret);
            }

            if (friendIsLocal)
            {
                // Visitor acceptor. They must have a session on this grid.
                friendUUI = EnsureUui(friendUUI, friendID, true, UUID.Zero);
                agentUUI = EnsureUui(agentUUI, agentID, false, friendID);
                if (!IsFullUui(agentUUI) || !IsFullUui(friendUUI))
                {
                    m_log.WarnFormat("[HGFRIENDS MODULE]: Accept failed: missing UUI agent={0} friend={1}", agentUUI, friendUUI);
                    return false;
                }
                if (!InformHome(agentID, agentClientCircuit, agentFriendService, friendUUI + ";" + secret, false))
                    return false;
                return StoreAcceptedLocal(friendID, agentID, agentUUI + ";" + secret);
            }

            // Both foreigners. Acceptor must be here with a session.
            // C → A's home. If B is also here, tell HomeB; if not, A's home fans out (phase 3).
            if (!HasLiveVisitorSession(agentClientCircuit))
            {
                m_log.WarnFormat("[HGFRIENDS MODULE]: Accept failed: acceptor {0} has no circuit/session", agentID);
                return false;
            }
            agentUUI = EnsureUui(agentUUI, agentID, false, UUID.Zero);
            friendUUI = EnsureUui(friendUUI, friendID, false, UUID.Zero);
            if (!IsFullUui(agentUUI) || !IsFullUui(friendUUI))
            {
                m_log.WarnFormat("[HGFRIENDS MODULE]: Accept failed: missing UUI agent={0} friend={1}", agentUUI, friendUUI);
                return false;
            }
            if (!NotifyVisitorHome(agentFriendService, agentClientCircuit, agentID, friendUUI + ";" + secret))
                return false;
            if (HasLiveVisitorSession(friendClientCircuit)
                    && !NotifyVisitorHome(friendFriendService, friendClientCircuit, friendID, agentUUI + ";" + secret))
                return false;
            return true;
        }

        AgentCircuitData FindCircuit(UUID userId)
        {
            foreach (Scene s in m_Scenes)
            {
                AgentCircuitData circuit = HGIdentity.GetCircuit(s, userId);
                if (circuit is not null)
                    return circuit;
            }
            return null;
        }

        static string CircuitUui(AgentCircuitData circuit)
        {
            if (circuit is null)
                return string.Empty;
            string uui = Util.ProduceUserUniversalIdentifier(circuit);
            return IsFullUui(uui) ? uui : string.Empty;
        }

        string EnsureUui(string uui, UUID userId, bool local, UUID pendingPeer)
        {
            uui = HGIdentity.WithoutSecret(uui);
            if (IsFullUui(uui))
                return uui;
            if (local)
                return LocalUserUui(userId);
            if (pendingPeer.IsNotZero())
            {
                string pending = HGIdentity.WithoutSecret(GetUUI(pendingPeer, userId));
                if (IsFullUui(pending))
                    return pending;
            }
            if (UserManagementModule is not null)
            {
                string fromUm = HGIdentity.WithoutSecret(UserManagementModule.GetUserUUI(userId));
                if (IsFullUui(fromUm))
                    return fromUm;
            }
            return uui ?? string.Empty;
        }

        string LocalUserUui(UUID userId)
        {
            UserAccount account = UserAccountService.GetUserAccount(UUID.Zero, userId);
            Scene scene = m_Scenes.Count > 0 ? m_Scenes[0] : null;
            string home = HGIdentity.ResolveHomeURI(scene, UserManagementModule, userId);
            if (account is null || string.IsNullOrEmpty(home))
                return string.Empty;
            return GridInstantMessage.BuildUUI(userId, account.FirstName + " " + account.LastName, home);
        }

        bool StoreAcceptedLocal(UUID localId, UUID otherId, string otherUuiWithSecret)
        {
            DeletePreviousRelations(localId, otherId);
            FriendsService.StoreFriend(localId.ToString(), otherUuiWithSecret, 1);
            FriendsService.StoreFriend(otherUuiWithSecret, localId.ToString(), 1);
            return true;
        }

        static bool HasLiveVisitorSession(AgentCircuitData circuit)
        {
            return circuit is not null
                && circuit.SessionID.IsNotZero()
                && !string.IsNullOrEmpty(circuit.ServiceSessionID);
        }

        /// <summary>
        /// Session here → verified NewFriendship. On this grid without a local circuit →
        /// that region POSTs with their session. Not on this grid → unverified only if allowed (phase 4).
        /// </summary>
        bool InformHome(UUID userId, AgentCircuitData circuit, string friendsUri, string otherUuiWithSecret, bool allowUnverified)
        {
            if (HasLiveVisitorSession(circuit))
                return NotifyVisitorHome(friendsUri, circuit, userId, otherUuiWithSecret);

            if (VisitorPresentOnThisGrid(userId))
                return NotifyVisitorHomeViaPresence(userId, otherUuiWithSecret);

            if (allowUnverified)
            {
                if (string.IsNullOrEmpty(friendsUri) && m_Scenes.Count > 0)
                    friendsUri = HGIdentity.ResolveFriendsServerURI(m_Scenes[0], UserManagementModule, userId, null);
                return NotifyHomeUnverified(friendsUri, userId, otherUuiWithSecret);
            }

            m_log.WarnFormat("[HGFRIENDS MODULE]: Accept failed: HG visitor {0} has no circuit/session on this grid", userId);
            return false;
        }

        bool NotifyVisitorHome(string friendsUri, AgentCircuitData circuit, UUID visitorId, string otherUuiWithSecret)
        {
            if (string.IsNullOrEmpty(friendsUri) || !HasLiveVisitorSession(circuit))
            {
                m_log.WarnFormat("[HGFRIENDS MODULE]: Accept failed: HG visitor {0} has no FriendsServerURI or session", visitorId);
                return false;
            }

            HGFriendsServicesConnector conn = new(friendsUri, circuit.SessionID, circuit.ServiceSessionID);
            bool ok = conn.NewFriendship(visitorId, otherUuiWithSecret);
            if (!ok)
                m_log.WarnFormat("[HGFRIENDS MODULE]: Home NewFriendship failed for visitor {0} at {1}", visitorId, friendsUri);
            else
                m_log.InfoFormat("[HGFRIENDS MODULE]: Informed home of visitor {0} at {1} session={2}",
                    visitorId, friendsUri, circuit.SessionID);
            return ok;
        }

        bool VisitorPresentOnThisGrid(UUID userId)
        {
            if (FindCircuit(userId) is not null)
                return true;
            PresenceInfo[] sessions = PresenceService?.GetAgents(new string[] { userId.ToString() });
            return sessions is not null && sessions.Length > 0 && sessions[0] is not null
                && !sessions[0].RegionID.IsZero();
        }

        bool NotifyHomeUnverified(string friendsUri, UUID homeUserId, string otherUuiWithSecret)
        {
            if (string.IsNullOrEmpty(friendsUri) || !IsFullUui(HGIdentity.WithoutSecret(otherUuiWithSecret)))
            {
                m_log.WarnFormat("[HGFRIENDS MODULE]: Phase 4 accept failed: no FriendsServerURI or UUI for {0}", homeUserId);
                return false;
            }

            HGFriendsServicesConnector conn = new(friendsUri);
            bool ok = conn.NewFriendship(homeUserId, otherUuiWithSecret);
            if (!ok)
                m_log.WarnFormat("[HGFRIENDS MODULE]: Unverified NewFriendship failed for {0} at {1}", homeUserId, friendsUri);
            else
                m_log.InfoFormat("[HGFRIENDS MODULE]: Informed home {0} of {1} (no session, reverse pending)",
                    friendsUri, homeUserId);
            return ok;
        }

        bool NotifyVisitorHomeViaPresence(UUID visitorId, string otherUuiWithSecret)
        {
            PresenceInfo[] sessions = PresenceService?.GetAgents(new string[] { visitorId.ToString() });
            if (sessions is null || sessions.Length == 0 || sessions[0] is null || sessions[0].RegionID.IsZero())
                return false;

            GridRegion region = GridService.GetRegionByUUID(m_Scenes[0].RegionInfo.ScopeID, sessions[0].RegionID);
            if (region is null)
                return false;

            m_log.InfoFormat("[HGFRIENDS MODULE]: Asking region {0} to complete visitor {1} home friendship",
                region.RegionName, visitorId);
            return m_FriendsSimConnector.CompleteVisitorFriendship(region, visitorId, otherUuiWithSecret);
        }

        public override bool CompleteVisitorHomeFriendship(UUID visitorId, string otherUuiWithSecret)
        {
            AgentCircuitData circuit = FindCircuit(visitorId);
            if (!HasLiveVisitorSession(circuit))
            {
                m_log.WarnFormat("[HGFRIENDS MODULE]: complete_visitor_friendship: no circuit/session for {0}", visitorId);
                return false;
            }

            Scene scene = m_Scenes.Count > 0 ? m_Scenes[0] : null;
            string friendsUri = HGIdentity.ResolveFriendsServerURI(scene, UserManagementModule, visitorId, circuit);
            return NotifyVisitorHome(friendsUri, circuit, visitorId, otherUuiWithSecret);
        }

        static bool IsFullUui(string uui)
        {
            return HGIdentity.IsFullUui(uui);
        }

        private void DeletePreviousRelations(UUID a1, UUID a2)
        {
            // Delete any previous friendship relations
            FriendInfo f;
            FriendInfo[] finfos = GetFriendsFromCache(a1);
            if (finfos is not null)
            {
                f = GetFriend(finfos, a2);
                if (f is not null)
                {
                    FriendsService.Delete(a1, f.Friend);
                    // and also the converse
                    FriendsService.Delete(f.Friend, a1.ToString());
                }
            }

            finfos = GetFriendsFromCache(a2);
            if (finfos is not null)
            {
                f = GetFriend(finfos, a1);
                if (f is not null)
                {
                    FriendsService.Delete(a2, f.Friend);
                    // and also the converse
                    FriendsService.Delete(f.Friend, a2.ToString());
                }
            }
        }

        protected void DeletePreviousHGRelations(UUID a1, UUID a2)
        {
            // Delete any previous friendship relations
            FriendInfo[] finfos = GetFriendsFromCache(a1);
            if (finfos is not null)
            {
                string a1str = a1.ToString();
                string a2str = a2.ToString();
                foreach (FriendInfo f in finfos)
                {
                    if (f.TheirFlags == -1)
                    {
                        if (f.Friend.StartsWith(a2str))
                        {
                            FriendsService.Delete(a1, f.Friend);
                            // and also the converse
                            FriendsService.Delete(f.Friend, a1str);
                        }
                    }
                }
            }

            finfos = GetFriendsFromCache(a2);
            if (finfos is not null)
            {
                string a1str2 = a1.ToString();
                string a2str2 = a2.ToString();
                foreach (FriendInfo f in finfos)
                {
                    if (f.TheirFlags == -1)
                    {
                        if (f.Friend.StartsWith(a1str2))
                        {
                            FriendsService.Delete(a2, f.Friend);
                            // and also the converse
                            FriendsService.Delete(f.Friend, a2str2);
                        }
                    }
                }
            }
        }

        protected override bool DeleteFriendship(UUID agentID, UUID exfriendID)
        {
            Boolean agentIsLocal = true;
            Boolean friendIsLocal = true;
            if (UserManagementModule != null)
            {
                agentIsLocal = UserManagementModule.IsLocalGridUser(agentID);
                friendIsLocal = UserManagementModule.IsLocalGridUser(exfriendID);
            }

            // Are they both local users?
            if (agentIsLocal && friendIsLocal)
            {
                // local grid users
                return base.DeleteFriendship(agentID, exfriendID);
            }

            // ok, at least one of them is foreigner, let's get their data
            string agentUUI = string.Empty;
            string friendUUI = string.Empty;

            if (agentIsLocal) // agent is local, 'friend' is foreigner
            {
                // We need to look for its information in the friends list itself
                FriendInfo[] finfos = GetFriendsFromCache(agentID);
                FriendInfo finfo = GetFriend(finfos, exfriendID);
                if (finfo != null)
                {
                    friendUUI = finfo.Friend;

                    // delete in the local friends service the reference to the foreign friend
                    FriendsService.Delete(agentID, friendUUI);
                    // and also the converse
                    FriendsService.Delete(friendUUI, agentID.ToString());

                    // notify the exfriend's service
                    Util.FireAndForget(
                        delegate { Delete(exfriendID, agentID, friendUUI); }, null, "HGFriendsModule.DeleteFriendshipForeignFriend");

                    m_log.DebugFormat("[HGFRIENDS MODULE]: {0} terminated {1}", agentID, friendUUI);
                    return true;
                }
            }
            else if (friendIsLocal) // agent is foreigner, 'friend' is local
            {
                agentUUI = GetUUI(exfriendID, agentID);

                if (agentUUI != string.Empty)
                {
                    // delete in the local friends service the reference to the foreign agent
                    FriendsService.Delete(exfriendID, agentUUI);
                    // and also the converse
                    FriendsService.Delete(agentUUI, exfriendID.ToString());

                    // notify the agent's service?
                    Util.FireAndForget(
                        delegate { Delete(agentID, exfriendID, agentUUI); }, null, "HGFriendsModule.DeleteFriendshipLocalFriend");

                    m_log.DebugFormat("[HGFRIENDS MODULE]: {0} terminated {1}", agentUUI, exfriendID);
                    return true;
                }
            }
            //else They're both foreigners! Can't handle this

            return false;
        }

        private string GetUUI(UUID localUser, UUID foreignUser)
        {
            // Let's see if the user is here by any chance
            FriendInfo[] finfos = GetFriendsFromCache(localUser);
            if (finfos != EMPTY_FRIENDS) // friend is here, cool
            {
                FriendInfo finfo = GetFriend(finfos, foreignUser);
                if (finfo != null)
                {
                    return finfo.Friend;
                }
            }
            else // user is not currently on this sim, need to get from the service
            {
                finfos = FriendsService.GetFriends(localUser);
                foreach (FriendInfo finfo in finfos)
                {
                    if (finfo.Friend.StartsWith(foreignUser.ToString())) // found it!
                    {
                        return finfo.Friend;
                    }
                }
            }
            return string.Empty;
        }

        private void Delete(UUID foreignUser, UUID localUser, string uui)
        {
            if (Util.ParseFullUniversalUserIdentifier(uui, out UUID _, out string url, out string _, out string _, out string secret))
            {
                m_log.DebugFormat("[HGFRIENDS MODULE]: Deleting friendship from {0}", url);
                HGFriendsServicesConnector friendConn = new HGFriendsServicesConnector(url);
                if (!friendConn.DeleteFriendship(foreignUser, localUser, secret))
                    m_log.WarnFormat("[HGFRIENDS MODULE]: Remote delete failed for {0} at {1} (secret mismatch?)",
                        foreignUser, url);
            }
        }

        protected override bool ForwardFriendshipOffer(UUID agentID, UUID friendID, GridInstantMessage im)
        {
            if (base.ForwardFriendshipOffer(agentID, friendID, im))
                return true;

            // Local recipient not on this sim: pending is already stored. If they are traveling, IM them.
            if (m_uMan is not null && m_uMan.IsLocalGridUser(friendID))
                return DeliverOfferToLocalTraveler(agentID, friendID, im);

            // Foreign recipient not here: friendship_offered to their home.
            if (m_uMan is not null && !m_uMan.IsLocalGridUser(friendID))
            {
                Scene scene = m_Scenes.Count > 0 ? m_Scenes[0] : null;
                string friendsURL = HGIdentity.ResolveFriendsServerURI(scene, m_uMan, friendID, null);
                if (!string.IsNullOrEmpty(friendsURL))
                {
                    m_log.DebugFormat("[HGFRIENDS MODULE]: Forwading friendship from {0} to {1} @ {2}", agentID, friendID, friendsURL);
                    GridRegion region = new GridRegion();
                    region.ServerURI = friendsURL;

                    string name = im.fromAgentName;
                    if (m_uMan.IsLocalGridUser(agentID))
                    {
                        string agentHomeService = HGIdentity.ResolveHomeURI(scene, m_uMan, agentID);
                        if (string.IsNullOrWhiteSpace(agentHomeService))
                        {
                            m_log.DebugFormat("[HGFRIENDS MODULE]: No HomeURI for local user {0}", agentID);
                            return false;
                        }
                        try
                        {
                            string lastname = "@" + new Uri(agentHomeService).Authority;
                            string firstname = im.fromAgentName.Replace(" ", ".");
                            name = firstname + lastname;
                        }
                        catch (UriFormatException)
                        {
                            m_log.DebugFormat("[HGFRIENDS MODULE]: Malformed HomeUri {0} for local user {1}", agentHomeService, agentID);
                            return false;
                        }
                    }

                    m_HGFriendsConnector.FriendshipOffered(region, agentID, friendID, im.message, name);
                    return true;
                }
            }

            return false;
        }

        bool DeliverOfferToLocalTraveler(UUID fromID, UUID toID, GridInstantMessage im)
        {
            IUserAgentService uas = m_Scenes.Count > 0
                ? m_Scenes[0].RequestModuleInterface<IUserAgentService>()
                : null;
            if (uas is null)
            {
                m_log.DebugFormat("[HGFRIENDS MODULE]: No UserAgentService to locate local traveler {0}", toID);
                return false;
            }

            string locate;
            try
            {
                locate = uas.LocateUser(toID);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[HGFRIENDS MODULE]: LocateUser failed for {0}: {1}", toID, e.Message);
                return false;
            }
            if (string.IsNullOrWhiteSpace(locate))
                return false;

            AgentCircuitData fromCircuit = FindCircuit(fromID);
            Scene scene = m_Scenes.Count > 0 ? m_Scenes[0] : null;
            string home = HGIdentity.ResolveHomeURI(scene, m_uMan, fromID, fromCircuit);
            if (!string.IsNullOrWhiteSpace(home))
                im.fromAgentHomeURI = home;

            bool ok = InstantMessageServiceConnector.SendInstantMessage(locate, im, m_MessageKey);
            m_log.InfoFormat("[HGFRIENDS MODULE]: Traveler offer to local {0} via {1} delivered={2}", toID, locate, ok);
            return ok;
        }

        void OnIncomingFriendshipIM(GridInstantMessage im)
        {
            if (im is null || im.fromGroup)
                return;
            if ((InstantMessageDialog)im.dialog != InstantMessageDialog.FriendshipOffered)
                return;
            LocalFriendshipOffered(new UUID(im.toAgentID), im);
        }

        public override bool LocalFriendshipOffered(UUID toID, GridInstantMessage im)
        {
            if (!base.LocalFriendshipOffered(toID, im))
                return false;

            string home = GridInstantMessage.ResolveSenderHomeURI(im.fromAgentHomeURI, null, im.fromAgentName);
            if (!string.IsNullOrWhiteSpace(home))
            {
                GridInstantMessage.SplitDisplayName(im.fromAgentName, out string first, out string last);
                Scene scene = m_Scenes.Count > 0 ? m_Scenes[0] : null;
                HGIdentity.RememberContact(scene, m_uMan, new UUID(im.fromAgentID), first, last, home);
            }
            return true;
        }
    }
}
