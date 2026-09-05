using System;
using System.Collections.Generic;
using System.Reflection;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Connectors.Hypergrid;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

using OpenMetaverse;

using log4net;

namespace OpenSim.Region.CoreModules.Avatar.Friends
{
    public class HGStatusNotifier
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private HGFriendsModule m_FriendsModule;

        public HGStatusNotifier(HGFriendsModule friendsModule)
        {
            m_FriendsModule = friendsModule;
        }

        public void Notify(UUID userID, Dictionary<string, List<FriendInfo>> friendsPerDomain, bool online)
        {
            if(m_FriendsModule is null)
                return;

            foreach (KeyValuePair<string, List<FriendInfo>> kvp in friendsPerDomain)
            {
                // For the others, call the user agent service
                List<string> ids = new(kvp.Value.Count);
                foreach (FriendInfo f in kvp.Value)
                    ids.Add(f.Friend);

                if (ids.Count == 0)
                    continue; // no one to notify. caller don't do this

                // ASSUMPTION: we assume that all users for one home domain
                // have exactly the same set of service URLs.
                List<UUID> friendsOnline = new();
                if (Util.ParseUniversalUserIdentifier(ids[0], out UUID friendID))
                {
                    string friendsServerURI = m_FriendsModule.UserManagementModule.GetUserServerURL(friendID, "FriendsServerURI");
                    if (string.IsNullOrEmpty(friendsServerURI))
                        friendsServerURI = kvp.Key;
                    if (!string.IsNullOrEmpty(friendsServerURI))
                    {
                        HGFriendsServicesConnector fConn = new(friendsServerURI);
                        List<UUID> reported = fConn.StatusNotification(ids, userID, online);
                        if (reported is not null)
                            friendsOnline = reported;
                    }
                }

                // Stock homes skip travelers (presence RegionID zero). LocateUser is the
                // travel table, and only returns a URL when the friend is on a foreign grid.
                if (online)
                    AddTravelingFriends(kvp.Value, friendsOnline);

                if (friendsOnline.Count == 0)
                    continue;

                IClientAPI client = m_FriendsModule.LocateClientObject(userID);
                if (client is not null)
                {
                    m_FriendsModule.CacheFriendsOnline(userID, friendsOnline, online);
                    if (online)
                        client.SendAgentOnline(friendsOnline.ToArray());
                    else
                        client.SendAgentOffline(friendsOnline.ToArray());
                }
            }
        }

        static void AddTravelingFriends(List<FriendInfo> friends, List<UUID> online)
        {
            HashSet<UUID> have = new(online);
            foreach (FriendInfo f in friends)
            {
                if (f?.Friend is null)
                    continue;
                if (!Util.ParseUniversalUserIdentifier(f.Friend, out UUID fid, out string home, out _, out _, out _))
                    continue;
                if (fid.IsZero() || have.Contains(fid) || string.IsNullOrWhiteSpace(home))
                    continue;
                try
                {
                    UserAgentServiceConnector uas = new(home);
                    if (string.IsNullOrWhiteSpace(uas.LocateUser(fid)))
                        continue;
                    online.Add(fid);
                    have.Add(fid);
                    m_log.DebugFormat("[HG STATUS NOTIFIER]: Friend {0} is online (traveling) via {1}", fid, home);
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[HG STATUS NOTIFIER]: LocateUser {0} at {1} failed: {2}", fid, home, e.Message);
                }
            }
        }
    }
}
