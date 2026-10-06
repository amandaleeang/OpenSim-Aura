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
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Connectors.Friends;
using OpenSim.Services.Interfaces;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Services.HypergridService
{
    /// <summary>
    /// Shared HG friend handling: secret match, then session online.
    /// </summary>
    static class HGFriendNotify
    {
        public static List<string> MatchingLocalFriends(
            IFriendsService friendsService, UUID foreignUserID, List<string> friends, bool requireCanSeeOnline)
        {
            List<string> users = new();
            if (friendsService is null || friends is null || friends.Count == 0)
                return users;

            string foreign = foreignUserID.ToString();
            foreach (string uui in friends)
            {
                if (!Util.ParseUniversalUserIdentifier(uui, out UUID localUserID, out _, out _, out _, out string secret))
                    continue;

                FriendInfo[] infos = friendsService.GetFriends(localUserID);
                if (infos is null)
                    continue;

                foreach (FriendInfo finfo in infos)
                {
                    if (finfo?.Friend is null)
                        continue;
                    if (!finfo.Friend.StartsWith(foreign) || !finfo.Friend.EndsWith(secret))
                        continue;
                    if (requireCanSeeOnline
                            && ((finfo.TheirFlags & (int)FriendRights.CanSeeOnline) == 0 || finfo.TheirFlags == -1))
                        continue;
                    users.Add(localUserID.ToString());
                    break;
                }
            }

            return users;
        }

        public static List<(UUID UserID, UUID RegionID)> WhoIsOnline(
            IPresenceService presence, List<string> userIds, Func<UUID, bool> hasTravelSession)
        {
            List<(UUID UserID, UUID RegionID)> result = new();
            if (userIds is null || userIds.Count == 0)
                return result;

            Dictionary<string, PresenceInfo> byUser = new(StringComparer.OrdinalIgnoreCase);
            if (presence is not null)
            {
                PresenceInfo[] sessions = presence.GetAgents(userIds.ToArray());
                if (sessions is not null)
                {
                    foreach (PresenceInfo pinfo in sessions)
                    {
                        if (pinfo?.UserID is null)
                            continue;
                        byUser[pinfo.UserID] = pinfo;
                    }
                }
            }

            HashSet<string> seen = new();
            foreach (string user in userIds)
            {
                if (!seen.Add(user) || !UUID.TryParse(user, out UUID uid))
                    continue;
                byUser.TryGetValue(user, out PresenceInfo pinfo);
                bool hasPresence = pinfo is not null;
                bool regionZero = !hasPresence || pinfo.RegionID.IsZero();
                bool liveTravel = hasTravelSession is not null && hasTravelSession(uid);
                if (!HGFriendStatus.IsOnline(hasPresence, regionZero, liveTravel))
                    continue;
                result.Add((uid, regionZero ? UUID.Zero : pinfo.RegionID));
            }

            return result;
        }

        public static List<UUID> ApplyStatusNotification(
            List<string> matched,
            IPresenceService presence,
            bool online,
            Func<UUID, bool> hasTravelSession,
            Action<UUID, UUID> forwardHome,
            Action<UUID> forwardTravel = null)
        {
            List<UUID> returning = new();
            foreach ((UUID id, UUID regionID) in WhoIsOnline(presence, matched, hasTravelSession))
            {
                if (regionID.IsNotZero())
                    forwardHome?.Invoke(regionID, id);
                else
                    forwardTravel?.Invoke(id);
                if (online)
                    returning.Add(id);
            }
            return online ? returning : [];
        }

        public static void ForwardTravelingStatus(
            string destUri,
            string homeUri,
            IFriendsSimConnector local,
            FriendsSimConnector remote,
            UUID foreignUserID,
            UUID localId,
            bool online)
        {
            if (string.IsNullOrWhiteSpace(destUri))
                return;

            if (local is not null
                    && !string.IsNullOrWhiteSpace(homeUri)
                    && OSHHTPHost.SameGrid(homeUri, destUri))
            {
                local.StatusNotify(foreignUserID, localId, online);
                return;
            }

            if (remote is null)
                return;

            GridRegion region = new() { ServerURI = destUri };
            remote.StatusNotify(region, foreignUserID, localId.ToString(), online);
        }
    }
}
