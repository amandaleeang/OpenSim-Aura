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
using System.Threading.Tasks;
using log4net;
using OpenMetaverse;
using OpenSim.Services.Connectors.Hypergrid;

namespace OpenSim.Addons.UUIDDHT
{
    /// <summary>
    /// Calls home UserAgent GetUserInfo for First/Last and display name.
    /// IGridUserService is synchronous, so the first foreign fetch still waits
    /// up to HomeHttpTimeoutSec. Repeats hit this cache. A timed-out fetch is
    /// left running and stored when it finishes so the next call does not HTTP
    /// again. Callers must not HTTP to their own HomeURI (OSHttpServer deadlock).
    /// </summary>
    public class UuidDhtHomeNameService : IUuidDhtHomeNameService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly int m_timeoutMs;
        private readonly int m_positiveSec;
        private readonly int m_negativeSec;
        private readonly ConcurrentDictionary<string, CacheEnt> m_cache = new ConcurrentDictionary<string, CacheEnt>();

        public UuidDhtHomeNameService(int timeoutSec = 5)
            : this(timeoutSec, UuidDhtConfig.DefaultPositiveCacheSec, UuidDhtConfig.DefaultNegativeCacheSec)
        {
        }

        public UuidDhtHomeNameService(int timeoutSec, int positiveCacheSec, int negativeCacheSec)
        {
            m_timeoutMs = (timeoutSec > 0 ? timeoutSec : UuidDhtConfig.DefaultHomeHttpTimeoutSec) * 1000;
            m_positiveSec = positiveCacheSec > 0 ? positiveCacheSec : UuidDhtConfig.DefaultPositiveCacheSec;
            m_negativeSec = negativeCacheSec > 0 ? negativeCacheSec : UuidDhtConfig.DefaultNegativeCacheSec;
        }

        public UuidDhtHomeName GetName(UUID userID, string homeUri)
        {
            if (userID.IsZero() || string.IsNullOrEmpty(homeUri))
                return null;

            string key = userID.ToString();
            if (TryCached(key, out UuidDhtHomeName cached))
                return cached;

            try
            {
                Task<UuidDhtHomeName> task = Task.Run(() => GetNameUncapped(userID, homeUri));
                if (!task.Wait(m_timeoutMs))
                {
                    m_log.WarnFormat("[UUID-DHT]: GetUserInfo timed out for {0} at {1}", userID, homeUri);
                    Remember(key, null, m_negativeSec);
                    task.ContinueWith(t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion)
                            Remember(key, t.Result, t.Result != null ? m_positiveSec : m_negativeSec);
                    }, TaskContinuationOptions.ExecuteSynchronously);
                    return null;
                }
                UuidDhtHomeName name = task.Result;
                Remember(key, name, name != null ? m_positiveSec : m_negativeSec);
                return name;
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[UUID-DHT]: GetUserInfo failed for {0} at {1}: {2}", userID, homeUri, e.Message);
                Remember(key, null, m_negativeSec);
                return null;
            }
        }

        private bool TryCached(string key, out UuidDhtHomeName name)
        {
            name = null;
            if (!m_cache.TryGetValue(key, out CacheEnt ent))
                return false;
            if (ent.ExpiresUnix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                m_cache.TryRemove(key, out _);
                return false;
            }
            name = ent.Name;
            return true;
        }

        private void Remember(string key, UuidDhtHomeName name, int ttlSec)
        {
            if (ttlSec <= 0)
                return;
            m_cache[key] = new CacheEnt
            {
                Name = name,
                ExpiresUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttlSec
            };
        }

        private sealed class CacheEnt
        {
            public UuidDhtHomeName Name;
            public long ExpiresUnix;
        }

        private static UuidDhtHomeName GetNameUncapped(UUID userID, string homeUri)
        {
            UserAgentServiceConnector home = new UserAgentServiceConnector(homeUri);
            Dictionary<string, object> info = home.GetUserInfo(userID);
            if (info == null || info.Count == 0)
                return null;

            if (info.TryGetValue("result", out object result) && result != null
                && !string.Equals(result.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                return null;

            string first = GetField(info, "user_firstname");
            string last = GetField(info, "user_lastname");
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(last))
                return null;

            return new UuidDhtHomeName
            {
                FirstName = first,
                LastName = last,
                DisplayName = GetField(info, "user_display_name")
            };
        }

        private static string GetField(Dictionary<string, object> info, string key)
        {
            if (!info.TryGetValue(key, out object value) || value == null)
                return string.Empty;
            return value.ToString();
        }
    }
}
