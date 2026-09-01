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
            if (friend is null)
                return false;
            if (!Util.ParseUniversalUserIdentifier(friend.Friend, out UUID friendID, out string url, out string first, out string last, out string secret))
                return false;

            m_log.DebugFormat("[HGFRIENDS SERVICE]: New friendship {0} {1} ({2})", friend.PrincipalID, friend.Friend, verified);

            FriendInfo[] mine = m_FriendsService.GetFriends(friend.PrincipalID);
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
                return KeepOrReplaceAcceptedSecret(friend, existing, verified, secret);

            FriendInfo[] theirs = m_FriendsService.GetFriends(friendID);
            FriendInfo reverse = null;
            foreach (FriendInfo fi in theirs)
            {
                if (fi.Friend != null && fi.Friend.StartsWith(friend.PrincipalID.ToString()) && fi.TheirFlags == -1)
                {
                    reverse = fi;
                    break;
                }
            }

            bool myPending = existing != null && existing.TheirFlags == -1;
            if (!myPending && reverse is null && !verified)
                return false;

            // Verified accept carries the secret the other home will match on status notify.
            string uui = null;
            if (verified && friend.Friend.Length > 36 && !string.IsNullOrEmpty(secret))
                uui = friend.Friend;
            else if (existing != null && existing.Friend.Length > 36)
                uui = existing.Friend;
            else if (friend.Friend.Length > 36)
                uui = friend.Friend;
            else
                uui = reverse?.Friend;
            if (string.IsNullOrEmpty(uui) || uui.Length <= 36)
            {
                m_log.WarnFormat("[HGFRIENDS SERVICE]: NewFriendship missing UUI for {0} {1}",
                    friend.PrincipalID, friend.Friend);
                return false;
            }

            // Phase 3: acceptor accepted abroad; the other avatar may still be visiting this grid.
            // Notify their home with their live session before we store, so C can wait on this result.
            if (verified && !NotifyOtherVisitorOnThisGrid(friend.PrincipalID, friendID, secret))
                return false;

            if (existing != null)
            {
                m_FriendsService.Delete(friend.PrincipalID, existing.Friend);
                m_FriendsService.Delete(existing.Friend, friend.PrincipalID.ToString());
            }
            if (reverse != null)
            {
                m_FriendsService.Delete(friendID, reverse.Friend);
                m_FriendsService.Delete(reverse.Friend, friendID.ToString());
            }

            m_FriendsService.StoreFriend(friend.PrincipalID.ToString(), uui, 1);
            m_FriendsService.StoreFriend(uui, friend.PrincipalID.ToString(), 1);

            m_log.InfoFormat("[HGFRIENDS SERVICE]: New friendship {0} {1} stored accepted verified={2}",
                friend.PrincipalID, friend.Friend, verified);

            ForwardToSim("ApproveFriendshipRequest", friendID, Util.UniversalName(first, last, url), "", friend.PrincipalID, "");
            return true;
        }

        /// <summary>
        /// If the other party is an HG visitor on this grid, that region POSTs NewFriendship
        /// to their home with their session. Not on this grid: skip (their sim already told their home).
        /// </summary>
        bool NotifyOtherVisitorOnThisGrid(UUID localId, UUID otherId, string secret)
        {
            UserAccount localAccount = m_UserAccountService.GetUserAccount(UUID.Zero, otherId);
            if (localAccount != null)
                return true;

            PresenceInfo[] sessions = m_PresenceService?.GetAgents(new string[] { otherId.ToString() });
            if (sessions is null || sessions.Length == 0 || sessions[0] is null || sessions[0].RegionID.IsZero())
                return true;

            GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero, sessions[0].RegionID);
            if (region is null)
            {
                m_log.WarnFormat("[HGFRIENDS SERVICE]: Visitor {0} present but region {1} unknown",
                    otherId, sessions[0].RegionID);
                return false;
            }

            UserAccount me = m_UserAccountService.GetUserAccount(UUID.Zero, localId);
            if (me is null || string.IsNullOrEmpty(m_HomeURI))
            {
                m_log.WarnFormat("[HGFRIENDS SERVICE]: Cannot build local UUI for {0}", localId);
                return false;
            }

            string myUui = Util.UniversalIdentifier(localId, me.FirstName, me.LastName, m_HomeURI);
            if (!string.IsNullOrEmpty(secret))
                myUui += ";" + secret;

            m_log.InfoFormat("[HGFRIENDS SERVICE]: Asking region {0} to notify home of visitor {1}",
                region.RegionName, otherId);
            bool ok = m_FriendsSimConnector.CompleteVisitorFriendship(region, otherId, myUui);
            if (!ok)
                m_log.WarnFormat("[HGFRIENDS SERVICE]: HomeB notify failed for visitor {0} via {1}",
                    otherId, region.RegionName);
            return ok;
        }

        public bool DeleteFriendship(FriendInfo friend, string secret)
        {
            if (friend is null || string.IsNullOrEmpty(friend.Friend))
                return false;

            string uuidPrefix = friend.Friend.Length >= 36 ? friend.Friend.Substring(0, 36) : friend.Friend;
            FriendInfo secretHit = null;
            FriendInfo uuidHit = null;
            int uuidHits = 0;

            FriendInfo[] finfos = m_FriendsService.GetFriends(friend.PrincipalID);
            foreach (FriendInfo finfo in finfos)
            {
                if (finfo.Friend is null || !finfo.Friend.StartsWith(uuidPrefix))
                    continue;
                uuidHits++;
                uuidHit = finfo;
                if (!string.IsNullOrEmpty(secret) && finfo.Friend.EndsWith(secret))
                    secretHit = finfo;
            }

            FriendInfo victim = secretHit ?? (uuidHits == 1 ? uuidHit : null);
            if (victim is null)
                return false;

            if (secretHit is null)
                m_log.WarnFormat("[HGFRIENDS SERVICE]: Delete friendship {0} {1} secret mismatch, removing by UUID",
                    friend.PrincipalID, victim.Friend);
            else
                m_log.DebugFormat("[HGFRIENDS SERVICE]: Delete friendship {0} {1}", friend.PrincipalID, victim.Friend);

            m_FriendsService.Delete(friend.PrincipalID, victim.Friend);
            m_FriendsService.Delete(victim.Friend, friend.PrincipalID.ToString());
            return true;
        }

        public bool FriendshipOffered(UUID fromID, string fromName, UUID toID, string message)
        {
            UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, toID);
            if (account == null)
                return false;

            // OK, we have that user here.
            // So let's send back the call, but start a thread to continue
            // with the verification and the actual action.

            Util.FireAndForget(
                o => ProcessFriendshipOffered(fromID, fromName, toID, message), null, "HGFriendsService.ProcessFriendshipOffered");

            return true;
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

            HashSet<string> reported = new();
            PresenceInfo[] friendSessions = m_PresenceService.GetAgents(usersToBeNotified.ToArray());
            if (friendSessions != null)
            {
                foreach (PresenceInfo pinfo in friendSessions)
                {
                    if (pinfo is null || pinfo.RegionID.IsZero())
                        continue;
                    if (!reported.Add(pinfo.UserID))
                        continue;
                    ForwardStatusNotificationToSim(pinfo.RegionID, foreignUserID, pinfo.UserID, online);
                    if (UUID.TryParse(pinfo.UserID, out UUID id))
                        localFriendsOnline.Add(id);
                }
            }

            // Traveling locals are still online (hg_traveling_data). Presence RegionID is zero after HG TP.
            if (online)
            {
                foreach (string user in usersToBeNotified)
                {
                    if (reported.Contains(user) || !UUID.TryParse(user, out UUID uid))
                        continue;
                    if (!IsTraveling(uid))
                        continue;
                    localFriendsOnline.Add(uid);
                    m_log.DebugFormat("[HGFRIENDS SERVICE]: Local friend {0} is online (traveling)", uid);
                }
                return localFriendsOnline;
            }

            return new List<UUID>();
        }

        bool IsTraveling(UUID userId)
        {
            if (m_UserAgentService is null)
                return false;
            try
            {
                return !string.IsNullOrWhiteSpace(m_UserAgentService.LocateUser(userId));
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[HGFRIENDS SERVICE]: LocateUser failed for {0}: {1}", userId, e.Message);
                return false;
            }
        }

        #endregion IHGFriendsService

        #region Aux

        /// <summary>
        /// Already-accepted friendship. A verified repeat with a different secret replaces
        /// the stored UUI; status notify matches on EndsWith(secret).
        /// </summary>
        bool KeepOrReplaceAcceptedSecret(FriendInfo friend, FriendInfo existing, bool verified, string secret)
        {
            Util.ParseUniversalUserIdentifier(existing.Friend, out _, out _, out _, out _, out string oldSecret);
            if (!verified || string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(friend.Friend)
                    || friend.Friend.Length <= 36
                    || string.Equals(oldSecret, secret, StringComparison.Ordinal))
            {
                m_log.InfoFormat("[HGFRIENDS SERVICE]: New friendship {0} {1} already accepted",
                    friend.PrincipalID, friend.Friend);
                return true;
            }

            string principal = friend.PrincipalID.ToString();
            m_FriendsService.Delete(principal, existing.Friend);
            m_FriendsService.Delete(existing.Friend, principal);

            int myFlags = existing.MyFlags != 0 ? existing.MyFlags : 1;
            int theirFlags = existing.TheirFlags > 0 ? existing.TheirFlags : 1;
            m_FriendsService.StoreFriend(principal, friend.Friend, myFlags);
            m_FriendsService.StoreFriend(friend.Friend, principal, theirFlags);

            m_log.InfoFormat("[HGFRIENDS SERVICE]: Replaced friendship secret for {0} {1} ({2} -> {3})",
                friend.PrincipalID, friend.Friend, oldSecret, secret);
            return true;
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

        private void ProcessFriendshipOffered(UUID fromID, String fromName, UUID toID, String message)
        {
            // Great, it's a genuine request. Let's proceed.
            // But now we need to confirm that the requester is who he says he is
            // before we act on the friendship request.

            if (!TryResolveOffererHomeURI(fromName, out string uriStr))
            {
                m_log.DebugFormat("[HGFRIENDS SERVICE]: Malformed offerer name/home {0}", fromName);
                return;
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
                return;
            }

            string fromUUI = Util.UniversalIdentifier(fromID, parts[0], "@" + parts[1], uriStr);
            // OK, we're good!
            ForwardToSim("FriendshipOffered", fromID, fromName, fromUUI, toID, message);
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
                    // Pending is the source of truth for an unanswered ask. Store first.
                    string secret = UUID.Random().ToString().Substring(0, 8);
                    m_FriendsService.StoreFriend(toID.ToString(), fromUUI + ";" + secret, 0);
                    if (m_FriendsLocalSimConnector != null)
                    {
                        GridInstantMessage im = MakeOfferIM(fromID, name, toID, message);
                        if (m_FriendsLocalSimConnector.LocalFriendshipOffered(toID, im))
                            return true;
                    }
                    else if (region != null)
                    {
                        if (m_FriendsSimConnector.FriendshipOffered(region, fromID, toID, message, name))
                            return true;
                    }
                    return DeliverOfferToTraveler(fromID, name, toID, message);
                case "ApproveFriendshipRequest":
                    if (m_FriendsLocalSimConnector != null) // standalone
                        return m_FriendsLocalSimConnector.LocalFriendshipApproved(fromID, name, toID);
                    else if (region != null) //grid
                        return m_FriendsSimConnector.FriendshipApproved(region, fromID, name, toID);
                    break;
            }

            return false;
        }

        static GridInstantMessage MakeOfferIM(UUID fromID, string name, UUID toID, string message)
        {
            GridInstantMessage im = new(null, fromID, name, toID,
                (byte)InstantMessageDialog.FriendshipOffered, message ?? string.Empty, false, Vector3.Zero);
            im.imSessionID = im.fromAgentID;
            return im;
        }

        bool DeliverOfferToTraveler(UUID fromID, string fromName, UUID toID, string message)
        {
            if (m_UserAgentService is null)
                return false;

            string locate;
            try
            {
                locate = m_UserAgentService.LocateUser(toID);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[HGFRIENDS SERVICE]: LocateUser failed for {0}: {1}", toID, e.Message);
                return false;
            }
            if (string.IsNullOrWhiteSpace(locate))
                return false;

            bool ok = InstantMessageServiceConnector.SendInstantMessage(
                locate, MakeOfferIM(fromID, fromName, toID, message), m_MessageKey);
            m_log.InfoFormat("[HGFRIENDS SERVICE]: Traveler offer to {0} via {1} delivered={2}",
                toID, locate, ok);
            return ok;
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
