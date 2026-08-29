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
using System.Collections.Generic;
using System.Net;
using System.Reflection;

using OpenSim.Framework;
using OpenSim.Services.Connectors.Friends;
using OpenSim.Services.Connectors.Hypergrid;
using OpenSim.Services.Connectors.InstantMessage;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenSim.Server.Base;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

using OpenMetaverse;
using log4net;
using Nini.Config;

namespace OpenSim.Services.HypergridService
{
    /// <summary>
    /// W2W social networking
    /// </summary>
    public class HGFriendsService : IHGFriendsService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        static bool m_Initialized = false;

        protected static IGridUserService m_GridUserService;
        protected static IGridService m_GridService;
        protected static IGatekeeperService m_GatekeeperService;
        protected static IFriendsService m_FriendsService;
        protected static IPresenceService m_PresenceService;
        protected static IUserAccountService m_UserAccountService;
        protected static IFriendsSimConnector m_FriendsLocalSimConnector; // standalone, points to HGFriendsModule
        protected static FriendsSimConnector m_FriendsSimConnector; // grid
        protected static IUserAgentService m_UserAgentService;
        protected static string m_MessageKey = string.Empty;
        protected static string m_HomeURI = string.Empty;

        private static string m_ConfigName = "HGFriendsService";

        public HGFriendsService(IConfigSource config, String configName, IFriendsSimConnector localSimConn)
        {
            if (m_FriendsLocalSimConnector == null)
                m_FriendsLocalSimConnector = localSimConn;

            if (!m_Initialized)
            {
                m_Initialized = true;

                if (configName != String.Empty)
                    m_ConfigName = configName;

                Object[] args = [ config ];

                IConfig serverConfig = config.Configs[m_ConfigName];
                if (serverConfig == null)
                    throw new Exception(String.Format("No section {0} in config file", m_ConfigName));

                string theService = serverConfig.GetString("FriendsService", string.Empty);
                if (theService.Length == 0)
                    throw new Exception("No FriendsService in config file " + m_ConfigName);
                m_FriendsService = ServerUtils.LoadPlugin<IFriendsService>(theService, args);

                theService = serverConfig.GetString("UserAccountService", string.Empty);
                if (theService.Length == 0)
                    throw new Exception("No UserAccountService in " + m_ConfigName);
                m_UserAccountService = ServerUtils.LoadPlugin<IUserAccountService>(theService, args);

                theService = serverConfig.GetString("GridService", string.Empty);
                if (theService.Length == 0)
                    throw new Exception("No GridService in " + m_ConfigName);
                m_GridService = ServerUtils.LoadPlugin<IGridService>(theService, args);

                theService = serverConfig.GetString("PresenceService", string.Empty);
                if (theService.Length == 0)
                    throw new Exception("No PresenceService in " + m_ConfigName);
                m_PresenceService = ServerUtils.LoadPlugin<IPresenceService>(theService, args);

                m_FriendsSimConnector = new FriendsSimConnector();

                string uas = serverConfig.GetString("UserAgentService", string.Empty);
                if (uas.Length > 0)
                {
                    try
                    {
                        m_UserAgentService = ServerUtils.LoadPlugin<IUserAgentService>(uas, args);
                    }
                    catch (Exception e)
                    {
                        m_log.WarnFormat("[HGFRIENDS SERVICE]: UserAgentService failed to load: {0}", e.Message);
                    }
                }

                IConfig messaging = config.Configs["Messaging"];
                if (messaging is not null)
                    m_MessageKey = messaging.GetString("MessageKey", string.Empty);

                m_HomeURI = Util.GetConfigVarFromSections<string>(config, "GatekeeperURI",
                    new string[] { "Startup", "Hypergrid", "UserAgentService", "HGFriendsService" }, string.Empty);
                if (string.IsNullOrEmpty(m_HomeURI))
                    m_HomeURI = serverConfig.GetString("HomeURI", string.Empty);
                if (!string.IsNullOrEmpty(m_HomeURI) && !m_HomeURI.EndsWith("/"))
                    m_HomeURI += "/";

                m_log.DebugFormat("[HGFRIENDS SERVICE]: Starting... (UserAgentService {0}, HomeURI {1})",
                    m_UserAgentService is null ? "off" : "on", m_HomeURI);

            }
        }

        #region IHGFriendsService

        public int GetFriendPerms(UUID userID, UUID friendID)
        {
            FriendInfo[] friendsInfo = m_FriendsService.GetFriends(userID);
            foreach (FriendInfo finfo in friendsInfo)
            {
                if (finfo.Friend.StartsWith(friendID.ToString()))
                    return finfo.TheirFlags;
            }
            return -1;
        }

        public bool NewFriendship(FriendInfo friend, bool verified)
        {
            return NewFriendship(friend, verified, UUID.Zero, out _);
        }

        public bool NewFriendship(FriendInfo friend, bool verified, out string reason)
        {
            return NewFriendship(friend, verified, UUID.Zero, out reason);
        }

        public bool NewFriendship(FriendInfo friend, bool verified, UUID sessionId, out string reason)
        {
            if (!verified && sessionId.IsNotZero() && m_PresenceService != null)
            {
                // B accepting at home has SessionID but no HG ServiceSessionID.
                PresenceInfo presence = m_PresenceService.GetAgent(sessionId);
                if (presence != null
                        && presence.UserID == friend.PrincipalID.ToString()
                        && !presence.RegionID.IsZero())
                    verified = true;
            }

            FriendshipCompleteReason r = TryCompleteLocal(m_FriendsService, friend, out string pendingExact);
            reason = ReasonString(r);
            m_log.DebugFormat("[HGFRIENDS SERVICE]: New friendship {0} {1} verified={2} reason={3}",
                friend.PrincipalID, friend.Friend, verified, reason);

            if (r == FriendshipCompleteReason.NoPending)
                return false;

            // Unverified: this is HomeA completing from HomeB. Do not call back (loop).
            if (!verified)
            {
                if (Util.ParseUniversalUserIdentifier(pendingExact, out UUID otherId, out string url, out string first, out string last, out _))
                    ForwardToSim("ApproveFriendshipRequest", otherId,
                        Util.UniversalName(first, last, url), "", friend.PrincipalID, "");
                return true;
            }

            if (r == FriendshipCompleteReason.Already)
            {
                NotifyOtherHome(friend, pendingExact, retry: false);
                return true;
            }

            // Upgraded + verified (HomeB orchestrator): notify HomeA; roll back only this upgrade if that fails.
            if (!NotifyOtherHome(friend, pendingExact, retry: true))
            {
                m_FriendsService.Delete(friend.PrincipalID, pendingExact);
                m_FriendsService.Delete(pendingExact, friend.PrincipalID.ToString());
                m_FriendsService.StoreFriend(friend.PrincipalID.ToString(), pendingExact, 0);
                reason = ReasonString(FriendshipCompleteReason.HomeAFailed);
                return false;
            }
            return true;
        }

        public static string ReasonString(FriendshipCompleteReason r)
        {
            return r switch
            {
                FriendshipCompleteReason.Upgraded => "upgraded",
                FriendshipCompleteReason.Already => "already",
                FriendshipCompleteReason.HomeAFailed => "homea_failed",
                _ => "no_pending"
            };
        }

        /// <summary>
        /// Complete a pending HG friendship on this home. Does not create pending (flags=0).
        /// </summary>
        public static FriendshipCompleteReason TryCompleteLocal(IFriendsService friends, FriendInfo friend,
            out string pendingFriendExact)
        {
            pendingFriendExact = null;
            if (friends is null || friend is null)
                return FriendshipCompleteReason.NoPending;
            if (!Util.ParseUniversalUserIdentifier(friend.Friend, out UUID friendID,
                    out _, out _, out _, out _))
                return FriendshipCompleteReason.NoPending;

            FriendInfo[] mine = friends.GetFriends(friend.PrincipalID.ToString());
            FriendInfo existing = null;
            foreach (FriendInfo fi in mine)
            {
                if (fi.Friend != null && fi.Friend.StartsWith(friendID.ToString()))
                {
                    existing = fi;
                    break;
                }
            }

            if (existing != null && existing.MyFlags != 0 && existing.TheirFlags != -1)
            {
                pendingFriendExact = existing.Friend;
                return FriendshipCompleteReason.Already;
            }

            FriendInfo[] theirs = friends.GetFriends(friendID.ToString());
            FriendInfo reverse = null;
            foreach (FriendInfo fi in theirs)
            {
                if (fi.Friend != null && fi.Friend.StartsWith(friend.PrincipalID.ToString())
                        && fi.TheirFlags == -1)
                {
                    reverse = fi;
                    break;
                }
            }

            bool myPending = existing != null && existing.TheirFlags == -1;
            if (!myPending && reverse == null)
                return FriendshipCompleteReason.NoPending;

            string uui = existing != null && existing.Friend.Length > 36
                ? existing.Friend
                : (friend.Friend.Length > 36 ? friend.Friend : existing?.Friend);
            if (string.IsNullOrEmpty(uui) && reverse != null)
                uui = reverse.Friend;
            if (string.IsNullOrEmpty(uui))
                return FriendshipCompleteReason.NoPending;
            pendingFriendExact = uui;

            if (existing != null)
            {
                friends.Delete(friend.PrincipalID, existing.Friend);
                friends.Delete(existing.Friend, friend.PrincipalID.ToString());
            }
            if (reverse != null)
            {
                friends.Delete(friendID, reverse.Friend);
                friends.Delete(reverse.Friend, friendID.ToString());
            }

            friends.StoreFriend(friend.PrincipalID.ToString(), uui, 1);
            friends.StoreFriend(uui, friend.PrincipalID.ToString(), 1);

            return FriendshipCompleteReason.Upgraded;
        }

        public bool DeleteFriendship(FriendInfo friend, string secret)
        {
            FriendInfo[] finfos = m_FriendsService.GetFriends(friend.PrincipalID);
            foreach (FriendInfo finfo in finfos)
            {
                // We check the secret here. Or if the friendship request was initiated here, and was declined
                if (finfo.Friend.StartsWith(friend.Friend) && finfo.Friend.EndsWith(secret))
                {
                    m_log.DebugFormat("[HGFRIENDS SERVICE]: Delete friendship {0} {1}", friend.PrincipalID, friend.Friend);
                    m_FriendsService.Delete(friend.PrincipalID, finfo.Friend);
                    m_FriendsService.Delete(finfo.Friend, friend.PrincipalID.ToString());

                    return true;
                }
            }

            return false;
        }

        public bool FriendshipOffered(UUID fromID, string fromName, UUID toID, string message)
        {
            HGFriendshipOffer offer = new()
            {
                FromID = fromID,
                ToID = toID,
                FromName = fromName,
                Message = message
            };
            return FriendshipOffered(offer, out _);
        }

        public bool FriendshipOffered(HGFriendshipOffer offer, out bool delivered)
        {
            delivered = false;
            if (offer is null)
                return false;

            UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, offer.ToID);
            if (account == null)
                return false;

            if (offer.HasSessionProof)
                return ProcessFriendshipOfferedNew(offer, out delivered);

            return ProcessFriendshipOffered(offer.FromID, offer.FromName, offer.ToID, offer.Message, out delivered);
        }

        public bool StoreReversePending(UUID fromId, UUID toId, string fromUui)
        {
            if (fromId.IsZero() || toId.IsZero())
                return false;
            string friend = string.IsNullOrWhiteSpace(fromUui) ? fromId.ToString() : fromUui;
            FriendInfo[] existing = m_FriendsService.GetFriends(toId.ToString());
            foreach (FriendInfo fi in existing)
            {
                if (fi.Friend != null && fi.Friend.StartsWith(fromId.ToString()))
                    return true;
            }
            return m_FriendsService.StoreFriend(toId.ToString(), friend, 0);
        }

        public bool DropReversePending(UUID fromId, UUID toId)
        {
            FriendInfo[] existing = m_FriendsService.GetFriends(toId.ToString());
            foreach (FriendInfo fi in existing)
            {
                if (fi.Friend != null && fi.Friend.StartsWith(fromId.ToString()) && fi.TheirFlags == -1)
                {
                    m_FriendsService.Delete(toId, fi.Friend);
                    m_FriendsService.Delete(fi.Friend, toId.ToString());
                    return true;
                }
            }
            return false;
        }

        public bool ValidateFriendshipOffered(UUID fromID, UUID toID)
        {
            FriendInfo[] finfos = m_FriendsService.GetFriends(toID.ToString());
            foreach (FriendInfo fi in finfos)
            {
                if (fi.Friend.StartsWith(fromID.ToString()) && fi.TheirFlags == -1)
                    return true;
            }
            return false;
        }

        public List<UUID> StatusNotification(List<string> friends, UUID foreignUserID, bool online)
        {
            if (m_FriendsService == null || m_PresenceService == null)
            {
                m_log.WarnFormat("[HGFRIENDS SERVICE]: Unable to perform status notifications because friends or presence services are missing");
                return new List<UUID>();
            }

            // Let's unblock the caller right now, and take it from here async

            List<UUID> localFriendsOnline = new List<UUID>();

            m_log.DebugFormat("[HGFRIENDS SERVICE]: Status notification: foreign user {0} wants to notify {1} local friends of {2} status",
                foreignUserID, friends.Count, (online ? "online" : "offline"));

            // First, let's double check that the reported friends are, indeed, friends of that user
            // And let's check that the secret matches
            List<string> usersToBeNotified = new List<string>();
            string foreignUserIDToString = foreignUserID.ToString();
            foreach (string uui in friends)
            {
                if (Util.ParseUniversalUserIdentifier(uui, out UUID localUserID, out _, out _, out _, out string secret))
                {
                    FriendInfo[] friendInfos = m_FriendsService.GetFriends(localUserID);
                    foreach (FriendInfo finfo in friendInfos)
                    {
                        if (finfo.Friend.StartsWith(foreignUserIDToString) && finfo.Friend.EndsWith(secret))
                        {
                            // great!
                            usersToBeNotified.Add(localUserID.ToString());
                        }
                    }
                }
            }

            // Now, let's send the notifications
            //m_log.DebugFormat("[HGFRIENDS SERVICE]: Status notification: user has {0} local friends", usersToBeNotified.Count);

            // First, let's send notifications to local users who are online in the home grid
            PresenceInfo[] friendSessions = m_PresenceService.GetAgents(usersToBeNotified.ToArray());
            if (friendSessions != null && friendSessions.Length > 0)
            {
                PresenceInfo friendSession = null;
                foreach (PresenceInfo pinfo in friendSessions)
                    if (!pinfo.RegionID.IsZero()) // let's guard against traveling agents
                    {
                        friendSession = pinfo;
                        break;
                    }

                if (friendSession != null)
                {
                    ForwardStatusNotificationToSim(friendSession.RegionID, foreignUserID, friendSession.UserID, online);
                    usersToBeNotified.Remove(friendSession.UserID.ToString());
                    UUID id;
                    if (UUID.TryParse(friendSession.UserID, out id))
                        localFriendsOnline.Add(id);

                }
            }

//            // Lastly, let's notify the rest who may be online somewhere else
//            foreach (string user in usersToBeNotified)
//            {
//                UUID id = new UUID(user);
//                //m_UserAgentService.LocateUser(id);
//                //etc...
//                //if (m_TravelingAgents.ContainsKey(id) && m_TravelingAgents[id].GridExternalName != m_GridName)
//                //{
//                //    string url = m_TravelingAgents[id].GridExternalName;
//                //    // forward
//                //}
//                //m_log.WarnFormat("[HGFRIENDS SERVICE]: User {0} is visiting another grid. HG Status notifications still not implemented.", user);
//            }

            // and finally, let's send the online friends
            if (online)
            {
                return localFriendsOnline;
            }
            else
                return new List<UUID>();
        }

        #endregion IHGFriendsService

        #region Aux

        public static bool HomeHostsMatch(string fromHomeUri, string fromName)
        {
            if (string.IsNullOrWhiteSpace(fromName))
                return true;
            string nameHome = GridInstantMessage.ResolveSenderHomeURI(null, null, fromName);
            OSHHTPHost a = new(fromHomeUri);
            OSHHTPHost b = new(nameHome);
            if (!a.IsValidHost || !b.IsValidHost)
                return false;
            if (!string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase))
                return false;
            bool aExplicit = HasExplicitNonDefaultPort(fromHomeUri);
            bool bExplicit = HasExplicitNonDefaultPort(nameHome);
            return !(aExplicit && bExplicit && a.Port != b.Port);
        }

        public static bool HasExplicitNonDefaultPort(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return false;
            OSHHTPHost h = new(uri);
            if (!h.IsValidHost)
                return false;
            int schemeSep = uri.IndexOf("://", StringComparison.Ordinal);
            int hostStart = schemeSep >= 0 ? schemeSep + 3 : 0;
            int slash = uri.IndexOf('/', hostStart);
            string hostport = slash >= 0 ? uri[hostStart..slash] : uri[hostStart..];
            int colon = hostport.LastIndexOf(':');
            if (colon <= 0)
                return false;
            if (!int.TryParse(hostport[(colon + 1)..], out int port))
                return false;
            return port != 80 && port != 443;
        }

        bool ProcessFriendshipOfferedNew(HGFriendshipOffer offer, out bool delivered)
        {
            delivered = false;
            if (!HomeHostsMatch(offer.FromHomeURI, offer.FromName))
            {
                m_log.WarnFormat("[HGFRIENDS SERVICE]: host mismatch FromName={0} FromHomeURI={1}",
                    offer.FromName, offer.FromHomeURI);
                return false;
            }

            try
            {
                UserAgentServiceConnector uas = new(offer.FromHomeURI);
                if (!uas.VerifyAgent(offer.SessionID, offer.ServiceKey))
                {
                    m_log.WarnFormat("[HGFRIENDS SERVICE]: VerifyAgent failed for {0} at {1}",
                        offer.FromID, offer.FromHomeURI);
                    return false;
                }
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[HGFRIENDS SERVICE]: VerifyAgent exception: {0}", e.Message);
                return false;
            }

            string home = offer.FromHomeURI;
            Dictionary<string, object> servers = TryGetServerURLs(offer.FromID, ref home);
            string friendsUri = null;
            if (servers != null && servers.TryGetValue("FriendsServerURI", out object fsu) && fsu != null)
                friendsUri = fsu.ToString();
            if (string.IsNullOrWhiteSpace(friendsUri))
                friendsUri = home;

            HGFriendsServicesConnector friendsConn = new(friendsUri);
            if (!friendsConn.ValidateFriendshipOffered(offer.FromID, offer.ToID))
            {
                m_log.WarnFormat("[HGFRIENDS SERVICE]: Friendship request from {0} to {1} is invalid. Impersonations?",
                    offer.FromID, offer.ToID);
                return false;
            }

            string first = offer.FromFirst;
            string last = offer.FromLast;
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last))
            {
                GridInstantMessage.SplitDisplayName(
                    string.IsNullOrWhiteSpace(offer.FromName) ? "Unknown User" : offer.FromName,
                    out first, out last);
            }
            string fromUUI = Util.UniversalIdentifier(offer.FromID, first, last, offer.FromHomeURI);
            if (!PersistPendingOffer(offer.ToID, offer.FromID, fromUUI, offer.FromName, offer.Message, offer.FromHomeURI, out delivered))
                return false;
            return true;
        }

        bool PersistPendingOffer(UUID toID, UUID fromID, string fromUUI, string fromName, string message, string fromHomeURI, out bool delivered)
        {
            delivered = false;
            string stored = null;
            FriendInfo[] existing = m_FriendsService.GetFriends(toID.ToString());
            foreach (FriendInfo fi in existing)
            {
                if (fi.Friend != null && fi.Friend.StartsWith(fromID.ToString()) && fi.TheirFlags == -1)
                {
                    stored = fi.Friend;
                    break;
                }
            }
            if (stored is null)
            {
                string secret = UUID.Random().ToString().Substring(0, 8);
                stored = fromUUI + ";" + secret;
                if (!m_FriendsService.StoreFriend(toID.ToString(), stored, 0))
                    return false;
            }

            delivered = DeliverOfferPopup(fromID, fromName, toID, message, fromHomeURI);
            return true;
        }

        bool DeliverOfferPopup(UUID fromID, string fromName, UUID toID, string message, string fromHomeURI)
        {
            GridInstantMessage im = new(null, fromID, fromName, toID,
                (byte)InstantMessageDialog.FriendshipOffered, message ?? string.Empty, false, Vector3.Zero)
            {
                imSessionID = fromID.Guid,
                fromAgentHomeURI = fromHomeURI ?? string.Empty
            };

            PresenceInfo[] sessions = m_PresenceService?.GetAgents(new string[] { toID.ToString() });
            if (sessions != null && sessions.Length > 0 && !sessions[0].RegionID.IsZero())
            {
                GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero, sessions[0].RegionID);
                if (m_FriendsLocalSimConnector != null)
                    return m_FriendsLocalSimConnector.LocalFriendshipOffered(toID, im);
                if (region != null)
                    return m_FriendsSimConnector.FriendshipOffered(region, fromID, toID, message ?? string.Empty, fromName, fromHomeURI);
            }

            if (m_UserAgentService is null)
                return false;
            string locate;
            try
            {
                locate = m_UserAgentService.LocateUser(toID);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[HGFRIENDS SERVICE]: LocateUser failed: {0}", e.Message);
                return false;
            }
            if (string.IsNullOrWhiteSpace(locate))
                return false;
            return InstantMessageServiceConnector.SendInstantMessage(locate, im, m_MessageKey, 2000);
        }

        static bool IsThisHome(string url)
        {
            if (string.IsNullOrEmpty(m_HomeURI) || string.IsNullOrEmpty(url))
                return false;
            OSHHTPHost a = new(url);
            OSHHTPHost b = new(m_HomeURI);
            return a.IsValidHost && b.IsValidHost
                && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
        }

        bool NotifyOtherHome(FriendInfo friend, string pendingExact, bool retry)
        {
            if (!Util.ParseUniversalUserIdentifier(pendingExact, out UUID otherId, out string url, out string first, out string last, out string secret))
                return true;
            if (string.IsNullOrWhiteSpace(url) || IsThisHome(url))
            {
                ForwardToSim("ApproveFriendshipRequest", otherId,
                    Util.UniversalName(first, last, url), "", friend.PrincipalID, "");
                return true;
            }

            UserAccount me = m_UserAccountService.GetUserAccount(UUID.Zero, friend.PrincipalID);
            string myFirst = me?.FirstName ?? "Unknown";
            string myLast = me?.LastName ?? "User";
            string myHome = string.IsNullOrEmpty(m_HomeURI) ? url : m_HomeURI;
            string myUui = Util.UniversalIdentifier(friend.PrincipalID, myFirst, myLast, myHome);
            if (!string.IsNullOrEmpty(secret))
                myUui += ";" + secret;

            HGFriendsServicesConnector conn = new(url);
            bool ok = conn.NewFriendship(otherId, myUui);
            if (!ok && retry)
                ok = conn.NewFriendship(otherId, myUui);
            return ok;
        }

        /// <summary>
        /// Home URI of an offerer from First.Last@host[:port]. Does not rewrite https to http.
        /// Host without a scheme is parsed as given (OSHHTPHost defaults to http).
        /// </summary>
        public static bool TryResolveOffererHomeURI(string fromName, out string homeUri)
        {
            homeUri = string.Empty;
            if (string.IsNullOrWhiteSpace(fromName) || !fromName.Contains('@'))
                return false;

            string[] parts = fromName.Split(new char[] { '@' });
            if (parts.Length != 2)
                return false;

            string hostPart = parts[1].Trim();
            if (hostPart.Length == 0)
                return false;

            OSHHTPHost parsed = new(hostPart);
            if (!parsed.IsValidHost && !hostPart.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                parsed = new OSHHTPHost("https://" + hostPart);
            if (!parsed.IsValidHost)
                return false;

            homeUri = parsed.URIwEndSlash;
            return true;
        }

        private bool ProcessFriendshipOffered(UUID fromID, String fromName, UUID toID, String message, out bool delivered)
        {
            delivered = false;
            if (!TryResolveOffererHomeURI(fromName, out string uriStr))
            {
                m_log.DebugFormat("[HGFRIENDS SERVICE]: Malformed offerer name/home {0}", fromName);
                return false;
            }

            string[] parts = fromName.Split(new char[] {'@'});

            Dictionary<string, object> servers = TryGetServerURLs(fromID, ref uriStr);
            string friendsServerURI = null;
            if (servers != null && servers.TryGetValue("FriendsServerURI", out object fsu) && fsu != null)
                friendsServerURI = fsu.ToString();
            if (string.IsNullOrWhiteSpace(friendsServerURI))
                friendsServerURI = uriStr;

            HGFriendsServicesConnector friendsConn = new(friendsServerURI);
            if (!friendsConn.ValidateFriendshipOffered(fromID, toID))
            {
                m_log.WarnFormat("[HGFRIENDS SERVICE]: Friendship request from {0} to {1} is invalid. Impersonations?", fromID, toID);
                return false;
            }

            string fromUUI = Util.UniversalIdentifier(fromID, parts[0], "@" + parts[1], uriStr);
            return PersistPendingOffer(toID, fromID, fromUUI, fromName, message, uriStr, out delivered);
        }

        static Dictionary<string, object> TryGetServerURLs(UUID fromID, ref string uriStr)
        {
            List<string> candidates = new() { uriStr };
            if (uriStr.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                candidates.Add("https://" + uriStr.Substring("http://".Length));
            else if (uriStr.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                candidates.Add("http://" + uriStr.Substring("https://".Length));

            foreach (string candidate in candidates)
            {
                try
                {
                    UserAgentServiceConnector uasConn = new(candidate);
                    Dictionary<string, object> servers = uasConn.GetServerURLs(fromID);
                    if (servers != null && servers.Count > 0)
                    {
                        uriStr = candidate;
                        return servers;
                    }
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[HGFRIENDS SERVICE]: GetServerURLs at {0} failed: {1}", candidate, e.Message);
                }
            }
            return null;
        }

        private bool ForwardToSim(string op, UUID fromID, string name, String fromUUI, UUID toID, string message)
        {
            PresenceInfo session = null;
            GridRegion region = null;
            PresenceInfo[] sessions = m_PresenceService.GetAgents(new string[] { toID.ToString() });
            if (sessions != null && sessions.Length > 0)
                session = sessions[0];
            if (session != null)
                region = m_GridService.GetRegionByUUID(UUID.Zero, session.RegionID);

            switch (op)
            {
                case "FriendshipOffered":
                    return PersistPendingOffer(toID, fromID, fromUUI, name, message, string.Empty, out _);
                case "ApproveFriendshipRequest":
                    if (m_FriendsLocalSimConnector != null) // standalone
                        return m_FriendsLocalSimConnector.LocalFriendshipApproved(fromID, name, toID);
                    else if (region != null) //grid
                        return m_FriendsSimConnector.FriendshipApproved(region, fromID, name, toID);
                    break;
            }

            return false;
        }

        protected void ForwardStatusNotificationToSim(UUID regionID, UUID foreignUserID, string user, bool online)
        {
            UUID userID;
            if (UUID.TryParse(user, out userID))
            {
                if (m_FriendsLocalSimConnector != null)
                {
                    m_log.DebugFormat("[HGFRIENDS SERVICE]: Local Notify, user {0} is {1}", foreignUserID, (online ? "online" : "offline"));
                    m_FriendsLocalSimConnector.StatusNotify(foreignUserID, userID, online);
                }
                else
                {
                    GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero /* !!! */, regionID);
                    if (region != null)
                    {
                        m_log.DebugFormat("[HGFRIENDS SERVICE]: Remote Notify to region {0}, user {1} is {2}", region.RegionName, foreignUserID, (online ? "online" : "offline"));
                        m_FriendsSimConnector.StatusNotify(region, foreignUserID, userID.ToString(), online);
                    }
                }
            }
        }

        #endregion Aux
    }
}
