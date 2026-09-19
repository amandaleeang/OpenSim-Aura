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
using System.Reflection;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Groups;
using OpenSim.Server.Base;

namespace OpenSim.Addons.UUIDDHT
{
    /// <summary>
    /// Fetches group identity from home: POST GETGROUP to /hg-groups then
    /// /groups, each with HomeHttpTimeoutSec. Does not use GroupsServiceHGConnector
    /// (that MakeRequest has no timeout and stalled after a DHT hit).
    /// </summary>
    public class UuidDhtHomeGroupService : IUuidDhtHomeGroupService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly int m_timeoutSec;
        private readonly int m_positiveSec;
        private readonly int m_negativeSec;
        private readonly ConcurrentDictionary<string, CacheEnt> m_cache = new ConcurrentDictionary<string, CacheEnt>();

        public UuidDhtHomeGroupService(int timeoutSec = 5)
            : this(timeoutSec, UuidDhtConfig.DefaultPositiveCacheSec, UuidDhtConfig.DefaultNegativeCacheSec)
        {
        }

        public UuidDhtHomeGroupService(int timeoutSec, int positiveCacheSec, int negativeCacheSec)
        {
            m_timeoutSec = timeoutSec > 0 ? timeoutSec : UuidDhtConfig.DefaultHomeHttpTimeoutSec;
            m_positiveSec = positiveCacheSec > 0 ? positiveCacheSec : UuidDhtConfig.DefaultPositiveCacheSec;
            m_negativeSec = negativeCacheSec > 0 ? negativeCacheSec : UuidDhtConfig.DefaultNegativeCacheSec;
        }

        public ExtendedGroupRecord GetGroup(UUID groupID, string homeUri)
        {
            if (groupID.IsZero() || string.IsNullOrEmpty(homeUri))
                return null;

            string uri = homeUri.Trim();
            if (!uri.EndsWith("/"))
                uri += "/";

            string key = groupID.ToString();
            if (TryCached(key, out ExtendedGroupRecord cached))
                return cached;

            try
            {
                ExtendedGroupRecord rec = PostGetGroup(uri + "hg-groups", groupID)
                    ?? PostGetGroup(uri + "groups", groupID);
                Remember(key, rec, rec != null ? m_positiveSec : m_negativeSec);
                return rec;
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[UUID-DHT]: GETGROUP failed for {0} at {1}: {2}", groupID, uri, e.Message);
                Remember(key, null, m_negativeSec);
                return null;
            }
        }

        private ExtendedGroupRecord PostGetGroup(string url, UUID groupID)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>
            {
                ["METHOD"] = "GETGROUP",
                ["GroupID"] = groupID.ToString(),
                ["RequestingAgentID"] = UUID.Zero.ToString()
            };

            string reply = SynchronousRestFormsRequester.MakeRequest(
                "POST", url, ServerUtils.BuildQueryString(sendData), m_timeoutSec);
            if (string.IsNullOrEmpty(reply))
                return null;

            Dictionary<string, object> ret = ServerUtils.ParseXmlResponse(reply);
            if (ret == null || !ret.TryGetValue("RESULT", out object oRESULT) || oRESULT == null)
                return null;

            if (oRESULT is string sRESULT && sRESULT.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                return null;

            if (oRESULT is not Dictionary<string, object> dict)
                return null;

            return GroupsDataUtils.GroupRecord(dict);
        }

        private bool TryCached(string key, out ExtendedGroupRecord rec)
        {
            rec = null;
            if (!m_cache.TryGetValue(key, out CacheEnt ent))
                return false;
            if (ent.ExpiresUnix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                m_cache.TryRemove(key, out _);
                return false;
            }
            rec = ent.Record;
            return true;
        }

        private void Remember(string key, ExtendedGroupRecord rec, int ttlSec)
        {
            if (ttlSec <= 0)
                return;
            m_cache[key] = new CacheEnt
            {
                Record = rec,
                ExpiresUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttlSec
            };
        }

        private sealed class CacheEnt
        {
            public ExtendedGroupRecord Record;
            public long ExpiresUnix;
        }
    }
}
