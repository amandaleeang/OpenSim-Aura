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
using System.Reflection;
using System.Threading;
using log4net;
using Nini.Config;
using OpenMetaverse;

namespace OpenSim.Addons.UUIDDHT
{
    /// <summary>
    /// Two-level overlay lookup: FIND uuid then FIND node, returning HomeURI.
    /// Users and groups share one UUID keyspace. Does not join the overlay.
    /// </summary>
    public class UuidDhtClient : IUuidDhtClient
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly UuidDhtNode m_Node;
        private readonly ConcurrentDictionary<string, CacheEnt> m_cache = new ConcurrentDictionary<string, CacheEnt>();

        public UuidDhtClient(IConfigSource config)
            : this(UuidDhtNode.GetOrCreate(config))
        {
        }

        public UuidDhtClient(UuidDhtNode node)
        {
            m_Node = node;
        }

        public UuidDhtHome GetHomeByUuid(UUID userID)
        {
            return LookupHome(userID);
        }

        public UuidDhtHome GetGroupHomeByUuid(UUID groupID)
        {
            return LookupHome(groupID);
        }

        private UuidDhtHome LookupHome(UUID id)
        {
            if (id.IsZero() || m_Node == null || m_Node.Lookup == null)
                return null;

            string key = id.ToString();
            if (TryCached(key, out UuidDhtHome cached))
                return Copy(cached);

            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource();
                cts.CancelAfter(ClientCapMs());
                UuidDhtHome home = FindTwoLevel(id, cts.Token);
                if (home == null)
                {
                    Remember(key, null, NegativeSec());
                    return null;
                }

                Remember(key, home, PositiveSec());
                return Copy(home);
            }
            catch (OperationCanceledException)
            {
                m_log.DebugFormat("[UUID-DHT]: lookup timeout for {0}", id);
                Remember(key, null, NegativeSec());
                return null;
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[UUID-DHT]: lookup failed for {0}: {1}", id, e.Message);
                Remember(key, null, NegativeSec());
                return null;
            }
        }

        private UuidDhtHome FindTwoLevel(UUID id, CancellationToken ct)
        {
            DhtRecord uuidRec = m_Node.Lookup.FindValue(DhtKey.ForUuid(id), ct);
            if (!UsableUuid(uuidRec, id))
                return null;

            if (!DhtKey.TryParseHex(uuidRec.NodeId, out DhtKey nodeId))
                return null;

            DhtRecord nodeRec = m_Node.Lookup.FindValue(nodeId, ct);
            return TryBuildHome(uuidRec, nodeRec, id, m_Node.Config.AllowPrivateHomeURI);
        }

        internal static UuidDhtHome TryBuildHome(DhtRecord uuidRec, DhtRecord nodeRec, UUID id, bool allowPrivate)
        {
            if (!UsableUuid(uuidRec, id) || nodeRec == null || nodeRec.KindEnum != DhtRecordKind.Node || nodeRec.Tombstone)
                return null;
            if (string.IsNullOrEmpty(nodeRec.Pubkey) || nodeRec.NodeId != uuidRec.NodeId)
                return null;

            byte[] pub;
            try
            {
                pub = Convert.FromHexString(nodeRec.Pubkey);
            }
            catch (FormatException)
            {
                return null;
            }

            if (!nodeRec.Verify(pub) || !uuidRec.Verify(pub))
                return null;
            if (!DhtLocator.TryNormalize(nodeRec.HomeURI, allowPrivate, out string home))
                return null;

            return new UuidDhtHome { UserID = id, HomeURI = home };
        }

        private static bool UsableUuid(DhtRecord rec, UUID id)
        {
            if (rec == null || rec.KindEnum != DhtRecordKind.Uuid || rec.Tombstone)
                return false;
            if (!UUID.TryParse(rec.Uuid, out UUID recId) || recId != id)
                return false;
            return true;
        }

        private bool TryCached(string key, out UuidDhtHome home)
        {
            home = null;
            if (!m_cache.TryGetValue(key, out CacheEnt ent))
                return false;
            if (ent.ExpiresUnix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                m_cache.TryRemove(key, out _);
                return false;
            }
            home = ent.Home;
            return true;
        }

        private void Remember(string key, UuidDhtHome home, int ttlSec)
        {
            if (ttlSec <= 0)
                return;
            m_cache[key] = new CacheEnt
            {
                Home = home == null ? null : Copy(home),
                ExpiresUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttlSec
            };
        }

        private int ClientCapMs()
        {
            int n = m_Node.Config.ClientLookupCapMs;
            return n > 0 ? n : UuidDhtConfig.DefaultClientLookupCapMs;
        }

        private int PositiveSec()
        {
            int n = m_Node.Config.PositiveCacheSec;
            return n > 0 ? n : 600;
        }

        private int NegativeSec()
        {
            int n = m_Node.Config.NegativeCacheSec;
            return n > 0 ? n : 60;
        }

        private static UuidDhtHome Copy(UuidDhtHome home)
        {
            if (home == null)
                return null;
            return new UuidDhtHome { UserID = home.UserID, HomeURI = home.HomeURI };
        }

        private sealed class CacheEnt
        {
            public UuidDhtHome Home;
            public long ExpiresUnix;
        }
    }
}
