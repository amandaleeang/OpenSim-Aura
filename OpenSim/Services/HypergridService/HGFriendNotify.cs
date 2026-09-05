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
using OpenSim.Services.Interfaces;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

namespace OpenSim.Services.HypergridService
{
    /// <summary>
    /// Shared pieces of HG friend online/offline handling used by UserAgentService
    /// and HGFriendsService (secret match, home presence, traveling fallback).
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

        public static List<(UUID UserID, UUID RegionID)> HomeOnline(IPresenceService presence, List<string> userIds)
        {
            List<(UUID UserID, UUID RegionID)> result = new();
            if (presence is null || userIds is null || userIds.Count == 0)
                return result;

            PresenceInfo[] sessions = presence.GetAgents(userIds.ToArray());
            if (sessions is null)
                return result;

            HashSet<string> seen = new();
            foreach (PresenceInfo pinfo in sessions)
            {
                if (pinfo is null || pinfo.RegionID.IsZero())
                    continue;
                if (!seen.Add(pinfo.UserID) || !UUID.TryParse(pinfo.UserID, out UUID uid))
                    continue;
                result.Add((uid, pinfo.RegionID));
            }

            return result;
        }

        public static void AddTraveling(
            List<string> users, HashSet<string> alreadyHomeOnline, List<UUID> into, Func<UUID, bool> isTraveling)
        {
            if (users is null || into is null || isTraveling is null)
                return;

            foreach (string user in users)
            {
                if ((alreadyHomeOnline is not null && alreadyHomeOnline.Contains(user))
                        || !UUID.TryParse(user, out UUID uid))
                    continue;
                if (!isTraveling(uid))
                    continue;
                into.Add(uid);
                alreadyHomeOnline?.Add(user);
            }
        }
    }
}
