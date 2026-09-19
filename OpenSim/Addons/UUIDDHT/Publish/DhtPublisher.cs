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
using System.Reflection;
using System.Threading;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Addons.UUIDDHT
{
    /// <summary>
    /// Originator STORE of local user and group UUIDs. Backfill lists IDs via
    /// addon-side SQL (IDhtLocalUuidSource), not GetUserAccounts("%") or stock
    /// Data list-all methods.
    /// </summary>
    public sealed class DhtPublisher
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly UuidDhtNode m_Node;
        private readonly object m_lock = new object();
        private readonly HashSet<UUID> m_Published = new HashSet<UUID>();
        private Timer m_backfillTimer;
        private Timer m_republishTimer;
        private bool m_started;

        public DhtPublisher(UuidDhtNode node)
        {
            m_Node = node ?? throw new ArgumentNullException(nameof(node));
        }

        public int PublishedCount
        {
            get { lock (m_lock) return m_Published.Count; }
        }

        public void Start()
        {
            lock (m_lock)
            {
                if (m_started)
                    return;
                m_started = true;
            }

            try
            {
                m_Node.Store.Expire();
                Backfill();
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[UUID-DHT]: backfill failed: {0}", e.Message);
            }

            int backfillSec = m_Node.Config.BackfillIntervalSec > 0 ? m_Node.Config.BackfillIntervalSec : 60;
            int backfillMs = backfillSec * 1000;
            m_backfillTimer = new Timer(_ =>
            {
                try
                {
                    m_Node.Store.Expire();
                    Backfill();
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[UUID-DHT]: backfill: {0}", e.Message);
                }
            }, null, backfillMs, backfillMs);

            int hours = m_Node.Config.RepublishHours > 0 ? m_Node.Config.RepublishHours : 24;
            int republishMs = hours * 3600 * 1000;
            m_republishTimer = new Timer(_ =>
            {
                try
                {
                    Republish();
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[UUID-DHT]: republish: {0}", e.Message);
                }
            }, null, republishMs, republishMs);
        }

        public bool PublishUuid(UUID uuid)
        {
            if (uuid.IsZero())
                return false;
            if (uuid == Constants.servicesGodAgentID || uuid == Constants.m_MrOpenSimID)
                return false;
            if (!m_Node.OwnsLocal(uuid))
                return false;

            int max = m_Node.Config.MaxPublishUuids > 0 ? m_Node.Config.MaxPublishUuids : 100000;
            lock (m_lock)
            {
                if (!m_Published.Contains(uuid) && m_Published.Count >= max)
                {
                    m_log.WarnFormat("[UUID-DHT]: MaxPublishUuids={0} reached, not publishing {1}", max, uuid);
                    return false;
                }
            }

            DhtRecord nodeRec = m_Node.OwnNodeRecord();
            long seq = 1;
            DhtKey key = DhtKey.ForUuid(uuid);
            if (m_Node.Store.TryGet(key, out DhtRecord have) && have != null
                && have.NodeId == m_Node.Identity.NodeId.ToHex())
                seq = have.Seq + 1;

            DhtRecord rec = DhtRecord.CreateUuid(uuid, m_Node.Identity.NodeId, seq, false);
            rec.SignWith(m_Node.Identity);

            using CancellationTokenSource cts = FanoutCts();
            bool ok = m_Node.StoreFanout(rec, nodeRec, "STORE", cts.Token);
            if (ok)
            {
                lock (m_lock)
                    m_Published.Add(uuid);
            }
            else
                m_log.WarnFormat("[UUID-DHT]: publish {0} failed", uuid);
            return ok;
        }

        public bool UnpublishUuid(UUID uuid)
        {
            if (uuid.IsZero())
                return false;

            DhtKey key = DhtKey.ForUuid(uuid);
            long seq = 1;
            if (m_Node.Store.TryGet(key, out DhtRecord have) && have != null)
            {
                if (have.NodeId != m_Node.Identity.NodeId.ToHex())
                    return false;
                seq = have.Seq + 1;
            }
            else if (!m_Node.OwnsLocal(uuid))
                return false;

            DhtRecord nodeRec = m_Node.OwnNodeRecord();
            DhtRecord tomb = DhtRecord.CreateUuid(uuid, m_Node.Identity.NodeId, seq, true);
            tomb.SignWith(m_Node.Identity);
            using CancellationTokenSource cts = FanoutCts();
            bool ok = m_Node.StoreFanout(tomb, nodeRec, "DELETE", cts.Token);
            if (ok)
            {
                lock (m_lock)
                    m_Published.Remove(uuid);
            }
            return ok;
        }

        public int Backfill()
        {
            int n = 0;
            int users = 0;
            int groups = 0;
            int newUsers = 0;
            int newGroups = 0;
            IDhtLocalUuidSource src = m_Node.LocalUuids;
            if (src != null)
            {
                IList<UUID> userIds = src.ListUserIds();
                IList<UUID> groupIds = src.ListGroupIds();
                users = userIds != null ? userIds.Count : 0;
                groups = groupIds != null ? groupIds.Count : 0;
                newUsers = PublishNew(userIds);
                newGroups = PublishNew(groupIds);
                n = newUsers + newGroups;
            }

            m_log.InfoFormat("[UUID-DHT]: backfill {0} users ({1} new), {2} groups ({3} new)",
                users, newUsers, groups, newGroups);
            return n;
        }

        public void Republish()
        {
            m_Node.Store.Expire();
            m_Node.RefreshLocatorRecords();

            UUID[] ids;
            lock (m_lock)
            {
                ids = new UUID[m_Published.Count];
                m_Published.CopyTo(ids);
            }
            int n = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                if (PublishUuid(ids[i]))
                    n++;
            }
            m_log.DebugFormat("[UUID-DHT]: republish {0} uuid records", n);
            m_Node.PersistKnownSeeds();
        }

        private int PublishNew(IList<UUID> ids)
        {
            if (ids == null || ids.Count == 0)
                return 0;
            int n = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                UUID id = ids[i];
                if (id.IsZero())
                    continue;
                lock (m_lock)
                {
                    if (m_Published.Contains(id))
                        continue;
                }
                if (PublishUuid(id))
                    n++;
            }
            return n;
        }

        private CancellationTokenSource FanoutCts()
        {
            int lookupMs = m_Node.Config.LookupTimeoutMs > 0 ? m_Node.Config.LookupTimeoutMs : UuidDhtConfig.DefaultLookupTimeoutMs;
            return new CancellationTokenSource(lookupMs * 3);
        }
    }
}
