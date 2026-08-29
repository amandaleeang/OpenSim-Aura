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
        private bool m_HomeCanonicalOffers = false;

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
                m_HomeCanonicalOffers = friendsConfig.GetBoolean("HomeCanonicalOffers", false);
            }
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
            if ((InstantMessageDialog)im.dialog != InstantMessageDialog.FriendshipOffered)
            {
                base.OnInstantMessage(client, im);
                return;
            }

            UUID principalID = new(im.fromAgentID);
            UUID friendID = new(im.toAgentID);

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

            bool aLocal = UserManagementModule.IsLocalGridUser(principalID);
            bool bLocal = UserManagementModule.IsLocalGridUser(friendID);
            if (!m_HomeCanonicalOffers || (aLocal && bLocal))
            {
                base.OnInstantMessage(client, im);
                return;
            }

            FriendInfo[] finfos = GetFriendsFromCache(principalID);
            if (finfos is not null)
            {
                FriendInfo f = GetFriend(finfos, friendID);
                if (f is not null)
                {
                    client.SendAgentAlertMessage("This person is already your friend. Please delete it first if you want to reestablish the friendship.", false);
                    return;
                }
            }

            OfferHomeCanonical(client, principalID, friendID, im);
        }

        protected override void OnApproveFriendRequest(IClientAPI client, UUID friendID, List<UUID> callingCardFolders)
        {
            AddFriendship(client, friendID);
        }

        public override void AddFriendship(IClientAPI client, UUID friendID)
        {
            bool aLocal = UserManagementModule.IsLocalGridUser(client.AgentId);
            bool bLocal = UserManagementModule.IsLocalGridUser(friendID);
            if (!m_HomeCanonicalOffers || (aLocal && bLocal))
            {
                base.AddFriendship(client, friendID);
                return;
            }

            if (!CompleteHomeCanonical(client, friendID))
                return;

            RecacheFriends(client);
            ICallingCardModule ccm = client.Scene.RequestModuleInterface<ICallingCardModule>();
            ccm?.CreateCallingCard(client.AgentId, friendID, UUID.Zero);
            if (LocalFriendshipApproved(client.AgentId, client.Name, friendID))
                client.SendAgentOnline(new UUID[] { friendID });
        }

        protected override void OnDenyFriendRequest(IClientAPI client, UUID friendID, List<UUID> callingCardFolders)
        {
            bool aLocal = UserManagementModule.IsLocalGridUser(client.AgentId);
            bool bLocal = UserManagementModule.IsLocalGridUser(friendID);
            if (!m_HomeCanonicalOffers || (aLocal && bLocal))
            {
                base.OnDenyFriendRequest(client, friendID, callingCardFolders);
                return;
            }

            Scene scene = (Scene)client.Scene;
            AgentCircuitData circuit = HGIdentity.GetCircuit(scene, client.AgentId);
            string homeB = HGIdentity.ResolveFriendsServerURI(scene, UserManagementModule, client.AgentId, circuit);
            string homeA = HGIdentity.ResolveFriendsServerURI(scene, UserManagementModule, friendID, HGIdentity.GetCircuit(scene, friendID));
            // Both homes store Principal=B, Friend=A (pending / reverse). Drop that pair.
            if (!string.IsNullOrEmpty(homeB))
                new HGFriendsServicesConnector(homeB).DropReversePending(friendID, client.AgentId);
            if (!string.IsNullOrEmpty(homeA))
                new HGFriendsServicesConnector(homeA).DropReversePending(friendID, client.AgentId);
            RecacheFriends(client);
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
            bool agentIsLocal = true;
            //bool friendIsLocal = true;

            if (UserManagementModule != null)
            {
                agentIsLocal = UserManagementModule.IsLocalGridUser(agentID);
                //friendIsLocal = UserManagementModule.IsLocalGridUser(friendID);
            }

            // Is the requester a local user?
            if (agentIsLocal)
            {
                // local grid users
                m_log.DebugFormat("[HGFRIENDS MODULE]: Friendship requester is local. Storing backwards.");

                base.StoreBackwards(friendID, agentID);
                return;
            }

            // no provision for this temporary friendship state when user is not local
            //FriendsService.StoreFriend(friendID.ToString(), agentID.ToString(), 0);
        }

        protected override void StoreFriendships(UUID agentID, UUID friendID)
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
                m_log.DebugFormat("[HGFRIENDS MODULE]: Users are both local");
                DeletePreviousHGRelations(agentID, friendID);
                base.StoreFriendships(agentID, friendID);
                return;
            }

            // ok, at least one of them is foreigner, let's get their data
            IClientAPI agentClient = LocateClientObject(agentID);
            IClientAPI friendClient = LocateClientObject(friendID);
            AgentCircuitData agentClientCircuit = null;
            AgentCircuitData friendClientCircuit = null;
            string agentUUI = string.Empty;
            string friendUUI = string.Empty;
            string agentFriendService = string.Empty;
            string friendFriendService = string.Empty;

            Scene scene = m_Scenes.Count > 0 ? m_Scenes[0] : null;
            if (agentClient is not null)
            {
                scene = (Scene)agentClient.Scene;
                agentClientCircuit = scene.AuthenticateHandler.GetAgentCircuitData(agentClient.CircuitCode);
                agentUUI = Util.ProduceUserUniversalIdentifier(agentClientCircuit);
                RecacheFriends(agentClient);
            }
            if (friendClient is not null)
            {
                Scene friendScene = (Scene)friendClient.Scene;
                scene ??= friendScene;
                friendClientCircuit = friendScene.AuthenticateHandler.GetAgentCircuitData(friendClient.CircuitCode);
                friendUUI = Util.ProduceUserUniversalIdentifier(friendClientCircuit);
                RecacheFriends(friendClient);
            }

            IUserManagement um = UserManagementModule;
            if (!HGIdentity.IsFullUui(agentUUI))
            {
                if (HGIdentity.TryResolveUUI(scene, um, friendID, agentID, out string resolvedAgent)
                        && HGIdentity.IsFullUui(resolvedAgent))
                    agentUUI = HGIdentity.WithoutSecret(resolvedAgent);
                else if (um is not null && string.IsNullOrEmpty(agentUUI))
                    agentUUI = um.GetUserUUI(agentID);
            }
            if (!HGIdentity.IsFullUui(friendUUI))
            {
                if (HGIdentity.TryResolveUUI(scene, um, agentID, friendID, out string resolvedFriend)
                        && HGIdentity.IsFullUui(resolvedFriend))
                    friendUUI = HGIdentity.WithoutSecret(resolvedFriend);
                else if (um is not null && string.IsNullOrEmpty(friendUUI))
                    friendUUI = um.GetUserUUI(friendID);
            }

            agentFriendService = HGIdentity.ResolveFriendsServerURI(scene, um, agentID, agentClientCircuit);
            friendFriendService = HGIdentity.ResolveFriendsServerURI(scene, um, friendID, friendClientCircuit);

            m_log.DebugFormat("[HGFRIENDS MODULE] HG Friendship! thisUUI={0}; friendUUI={1}; foreignThisFriendService={2}; foreignFriendFriendService={3}",
                    agentUUI, friendUUI, agentFriendService, friendFriendService);

            // Generate a random 8-character hex number that will sign this friendship
            string secret = UUID.Random().ToString().Substring(0, 8);

            string theFriendUUID = friendUUI + ";" + secret;
            string agentUUID = agentUUI + ";" + secret;

            if (agentIsLocal) // agent is local, 'friend' is foreigner
            {
                // This may happen when the agent returned home, in which case the friend is not there
                // We need to look for its information in the friends list itself
                FriendInfo[] finfos = null;
                bool confirming = false;
                if (friendUUI.Length == 0)
                {
                    finfos = GetFriendsFromCache(agentID);
                    foreach (FriendInfo finfo in finfos)
                    {
                        if (finfo.TheirFlags == -1)
                        {
                            if (finfo.Friend.StartsWith(friendID.ToString()))
                            {
                                friendUUI = finfo.Friend;
                                theFriendUUID = friendUUI;

                                // If it's confirming the friendship, we already have the full UUI with the secret
                                if (Util.ParseFullUniversalUserIdentifier(theFriendUUID, out UUID utmp, out string url,
                                            out string first, out string last))
                                {
                                    agentUUID = agentUUI + ";" + secret;
                                    m_uMan.AddUser(utmp, first, last, url);
                                }
                                confirming = true;
                                break;
                            }
                        }
                    }
                    if (!confirming)
                    {
                        friendUUI = m_uMan.GetUserUUI(friendID);
                        theFriendUUID = friendUUI + ";" + secret;
                    }

                    friendFriendService = m_uMan.GetUserServerURL(friendID, "FriendsServerURI");

                    //m_log.DebugFormat("[HGFRIENDS MODULE] HG Friendship! thisUUI={0}; friendUUI={1}; foreignThisFriendService={2}; foreignFriendFriendService={3}",
                    //    agentUUI, friendUUI, agentFriendService, friendFriendService);

                }

                // Delete any previous friendship relations
                DeletePreviousRelations(agentID, friendID);

                // store in the local friends service a reference to the foreign friend
                FriendsService.StoreFriend(agentID.ToString(), theFriendUUID, 1);
                // and also the converse
                FriendsService.StoreFriend(theFriendUUID, agentID.ToString(), 1);

                //if (!confirming)
                //{
                    // store in the foreign friends service a reference to the local agent
                    if (!string.IsNullOrEmpty(friendFriendService))
                    {
                        HGFriendsServicesConnector friendsConn = null;
                        if (friendClientCircuit != null) // the friend is here, validate session
                            friendsConn = new HGFriendsServicesConnector(friendFriendService, friendClientCircuit.SessionID, friendClientCircuit.ServiceSessionID);
                        else // the friend is not here, he initiated the request in his home world
                            friendsConn = new HGFriendsServicesConnector(friendFriendService);

                        friendsConn.NewFriendship(friendID, agentUUID);
                    }
                //}
            }
            else if (friendIsLocal) // 'friend' is local,  agent is foreigner
            {
                // Delete any previous friendship relations
                DeletePreviousRelations(agentID, friendID);

                // store in the local friends service a reference to the foreign agent
                FriendsService.StoreFriend(friendID.ToString(), agentUUI + ";" + secret, 1);
                // and also the converse
                FriendsService.StoreFriend(agentUUI + ";" + secret, friendID.ToString(), 1);

                if (agentClientCircuit is not null && !string.IsNullOrEmpty(agentFriendService))
                {
                    // store in the foreign friends service a reference to the local agent
                    HGFriendsServicesConnector friendsConn = new HGFriendsServicesConnector(agentFriendService, agentClientCircuit.SessionID, agentClientCircuit.ServiceSessionID);
                    friendsConn.NewFriendship(agentID, friendUUI + ";" + secret);
                }
            }
            else // They're both foreigners!
            {
                HGFriendsServicesConnector friendsConn;
                if (!string.IsNullOrEmpty(agentFriendService) && IsFullUui(friendUUI))
                {
                    if (agentClientCircuit is not null)
                        friendsConn = new HGFriendsServicesConnector(agentFriendService, agentClientCircuit.SessionID, agentClientCircuit.ServiceSessionID);
                    else
                        friendsConn = new HGFriendsServicesConnector(agentFriendService);
                    friendsConn.NewFriendship(agentID, friendUUI + ";" + secret);
                }
                if (!string.IsNullOrEmpty(friendFriendService) && IsFullUui(agentUUI))
                {
                    if (friendClientCircuit is not null)
                        friendsConn = new HGFriendsServicesConnector(friendFriendService, friendClientCircuit.SessionID, friendClientCircuit.ServiceSessionID);
                    else
                        friendsConn = new HGFriendsServicesConnector(friendFriendService);
                    friendsConn.NewFriendship(friendID, agentUUI + ";" + secret);
                }
            }
            // my brain hurts now
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
                friendConn.DeleteFriendship(foreignUser, localUser, secret);
            }
        }

        protected override bool ForwardFriendshipOffer(UUID agentID, UUID friendID, GridInstantMessage im)
        {
            if (base.ForwardFriendshipOffer(agentID, friendID, im))
                return true;

            // OK, that didn't work, so let's try to find this user somewhere.
            // Still the old control flow (local popup first). Identity helper can resolve
            // a friend who is not on this sim via UserManagement / get_uui.
            if (!m_uMan.IsLocalGridUser(friendID))
            {
                Scene scene = m_Scenes.Count > 0 ? m_Scenes[0] : null;
                HGIdentity.TryResolveUUI(scene, m_uMan, agentID, friendID, out string friendUui);
                AgentCircuitData friendCircuit = HGIdentity.GetCircuit(scene, friendID);
                string friendsURL = HGIdentity.ResolveFriendsServerURI(scene, m_uMan, friendID, friendCircuit);
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

                    if (Util.ParseUniversalUserIdentifier(friendUui, out UUID fid, out string fhome, out string ffirst, out string flast, out _))
                        HGIdentity.RememberContact(scene, m_uMan, fid, ffirst, flast, fhome);

                    return true;
                }
            }

            return false;
        }

        void OnIncomingFriendshipIM(GridInstantMessage im)
        {
            if (im is null || im.fromGroup)
                return;
            if ((InstantMessageDialog)im.dialog != InstantMessageDialog.FriendshipOffered)
                return;
            LocalFriendshipOffered(new UUID(im.toAgentID), im);
        }

        bool OfferHomeCanonical(IClientAPI client, UUID agentID, UUID friendID, GridInstantMessage im)
        {
            Scene scene = (Scene)client.Scene;
            IUserManagement um = UserManagementModule;

            if (!HGIdentity.TryResolveUUI(scene, um, agentID, friendID, out string friendUui)
                    && string.IsNullOrEmpty(HGIdentity.ResolveHomeURI(scene, um, friendID)))
            {
                client.SendAgentAlertMessage("Unable to send friendship invitation. User identity could not be resolved.", false);
                return false;
            }

            AgentCircuitData aCircuit = HGIdentity.GetCircuit(scene, agentID);
            if (aCircuit is null || aCircuit.SessionID.IsZero())
            {
                client.SendAgentAlertMessage("Unable to send friendship invitation. Could not reach your home grid.", false);
                return false;
            }

            string homeA = HGIdentity.ResolveHomeURI(scene, um, agentID, aCircuit);
            string friendsA = HGIdentity.ResolveFriendsServerURI(scene, um, agentID, aCircuit);
            string homeB = HGIdentity.ResolveHomeURI(scene, um, friendID);
            string friendsB = HGIdentity.ResolveFriendsServerURI(scene, um, friendID, HGIdentity.GetCircuit(scene, friendID));
            if (string.IsNullOrWhiteSpace(homeB) && string.IsNullOrWhiteSpace(friendsB))
            {
                client.SendAgentAlertMessage("Unable to send friendship invitation. User identity could not be resolved.", false);
                return false;
            }
            if (string.IsNullOrWhiteSpace(friendsB))
                friendsB = homeB;
            if (string.IsNullOrWhiteSpace(friendsA))
                friendsA = homeA;

            bool aLocal = um.IsLocalGridUser(agentID);

            if (!aLocal && string.IsNullOrEmpty(aCircuit.ServiceSessionID))
            {
                client.SendAgentAlertMessage("Unable to send friendship invitation. Could not reach your home grid.", false);
                return false;
            }

            if (aLocal)
            {
                FriendsService.StoreFriend(friendID.ToString(), agentID.ToString(), 0);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(friendsA))
                {
                    client.SendAgentAlertMessage("Unable to send friendship invitation. Could not reach your home grid.", false);
                    return false;
                }
                HGFriendsServicesConnector homeAConn = new(friendsA, aCircuit.SessionID, aCircuit.ServiceSessionID);
                if (!homeAConn.StoreReversePending(agentID, friendID, agentID.ToString(), aCircuit.SessionID, aCircuit.ServiceSessionID))
                {
                    client.SendAgentAlertMessage("Unable to send friendship invitation. Could not reach your home grid.", false);
                    return false;
                }
            }

            string fromHome = homeA;
            string fromName = im.fromAgentName;
            if (!string.IsNullOrWhiteSpace(fromHome))
            {
                try
                {
                    string lastname = "@" + new Uri(fromHome).Authority;
                    fromName = im.fromAgentName.Replace(" ", ".") + lastname;
                }
                catch (UriFormatException) { }
            }
            GridInstantMessage.SplitDisplayName(im.fromAgentName, out string fromFirst, out string fromLast);

            // Persist on HomeB's service (this Robust when B is local; foreign /hgfriends otherwise).
            HGFriendsServicesConnector homeBConn = new(friendsB, aCircuit.SessionID, aCircuit.ServiceSessionID ?? string.Empty);
            bool persistOk = homeBConn.FriendshipOffered(agentID, friendID, im.message, fromName,
                fromHome, fromFirst, fromLast, aCircuit.SessionID, aCircuit.ServiceSessionID ?? string.Empty, out bool delivered);

            if (!persistOk)
            {
                if (aLocal)
                    FriendsService.Delete(friendID, agentID.ToString());
                else if (!string.IsNullOrWhiteSpace(friendsA))
                    new HGFriendsServicesConnector(friendsA).DropReversePending(agentID, friendID);
                client.SendAgentAlertMessage("Unable to send friendship invitation. Could not reach the destination home grid.", false);
                return false;
            }

            if (!delivered)
                LocalFriendshipOffered(friendID, im);

            if (Util.ParseUniversalUserIdentifier(friendUui, out UUID fid, out string fhome, out string ffirst, out string flast, out _))
                HGIdentity.RememberContact(scene, um, fid, ffirst, flast, fhome);
            else if (!string.IsNullOrWhiteSpace(homeB))
                HGIdentity.RememberContact(scene, um, friendID, string.Empty, string.Empty, homeB);
            HGIdentity.RememberContact(scene, um, agentID, fromFirst, fromLast, fromHome);
            return true;
        }

        bool CompleteHomeCanonical(IClientAPI client, UUID friendID)
        {
            Scene scene = (Scene)client.Scene;
            AgentCircuitData circuit = HGIdentity.GetCircuit(scene, client.AgentId);
            string homeB = HGIdentity.ResolveFriendsServerURI(scene, UserManagementModule, client.AgentId, circuit);
            if (string.IsNullOrWhiteSpace(homeB))
            {
                client.SendAgentAlertMessage("Unable to complete friendship. Could not reach your home grid.", false);
                return false;
            }

            HGFriendsServicesConnector conn;
            if (circuit is not null)
                conn = new HGFriendsServicesConnector(homeB, circuit.SessionID, circuit.ServiceSessionID ?? string.Empty);
            else
                conn = new HGFriendsServicesConnector(homeB);

            if (!conn.NewFriendship(client.AgentId, friendID.ToString(), out string reason))
            {
                client.SendAgentAlertMessage("Unable to complete friendship.", false);
                return false;
            }
            if (reason.Equals("no_pending", StringComparison.OrdinalIgnoreCase)
                    || reason.Equals("homea_failed", StringComparison.OrdinalIgnoreCase))
            {
                client.SendAgentAlertMessage("Unable to complete friendship.", false);
                return false;
            }
            return true;
        }

        public override bool LocalFriendshipOffered(UUID toID, GridInstantMessage im)
        {
            if (base.LocalFriendshipOffered(toID, im))
            {
                string home = GridInstantMessage.ResolveSenderHomeURI(im.fromAgentHomeURI, null, im.fromAgentName);
                GridInstantMessage.SplitDisplayName(im.fromAgentName, out string first, out string last);
                if (string.IsNullOrWhiteSpace(home) && im.fromAgentName.Contains('@'))
                {
                    string[] parts = im.fromAgentName.Split(new char[] { '@' });
                    if (parts.Length == 2)
                    {
                        OSHHTPHost host = new(parts[1].Trim());
                        if (host.IsValidHost)
                            home = host.URI;
                    }
                }
                if (!string.IsNullOrWhiteSpace(home))
                {
                    Scene scene = m_Scenes.Count > 0 ? m_Scenes[0] : null;
                    HGIdentity.RememberContact(scene, m_uMan, new UUID(im.fromAgentID), first, last, home);
                }
                return true;
            }
            return false;
        }
    }
}
