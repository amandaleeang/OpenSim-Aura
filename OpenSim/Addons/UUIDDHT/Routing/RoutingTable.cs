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

namespace OpenSim.Addons.UUIDDHT
{
    /// <summary>
    /// In-memory k-buckets. Do not persist this table: NodeID/LastSeen go
    /// stale, and every contact must be re-proven after restart anyway.
    /// Discovered HomeURIs are written to config-include/dht-seeds.
    /// </summary>
    public sealed class RoutingTable
    {
        public const int Bits = 256;

        private readonly object m_rtLock = new object();
        private readonly DhtPeer m_self;
        private readonly int m_k;
        private readonly KBucket[] m_buckets;
        private readonly SiblingList m_siblings;

        public RoutingTable(DhtPeer self, int k, int siblingSize)
        {
            m_self = self ?? throw new ArgumentNullException(nameof(self));
            m_k = k > 0 ? k : 20;
            m_buckets = new KBucket[Bits];
            for (int i = 0; i < Bits; i++)
                m_buckets[i] = new KBucket(m_k);
            m_siblings = new SiblingList(self, siblingSize);
        }

        public DhtPeer Self
        {
            get { return m_self.Clone(); }
        }

        public SiblingList Siblings
        {
            get { return m_siblings; }
        }

        public DhtInsertStatus TryInsert(DhtPeer peer, out DhtPeer pingCandidate)
        {
            pingCandidate = null;
            if (peer == null || peer.NodeId.Equals(m_self.NodeId))
                return DhtInsertStatus.Ignored;

            int idx = m_self.NodeId.BucketIndex(peer.NodeId);
            if (idx < 0)
                return DhtInsertStatus.Ignored;

            lock (m_rtLock)
            {
                KBucket bucket = m_buckets[idx];
                DhtPeer existing = bucket.Find(peer.NodeId);
                if (existing != null)
                {
                    bucket.Touch(existing, peer);
                    RebuildSiblingsUnlocked();
                    return DhtInsertStatus.Refreshed;
                }

                DhtPeer copy = peer.Clone();
                if (copy.LastSeen == default)
                    copy.LastSeen = DateTime.UtcNow;
                if (bucket.TryAdd(copy))
                {
                    RebuildSiblingsUnlocked();
                    return DhtInsertStatus.Added;
                }

                pingCandidate = bucket.Head.Clone();
                return DhtInsertStatus.NeedPing;
            }
        }

        /// <summary>
        /// Call after pinging outside the lock. If pingOk, keep the stale head
        /// and drop the newcomer; otherwise evict stale and insert the new peer.
        /// </summary>
        public bool CompletePingReplace(DhtPeer stale, DhtPeer incoming, bool pingOk)
        {
            if (stale == null || incoming == null)
                return false;

            int idx = m_self.NodeId.BucketIndex(incoming.NodeId);
            if (idx < 0)
                return false;

            lock (m_rtLock)
            {
                KBucket bucket = m_buckets[idx];
                DhtPeer head = bucket.Head;
                if (head == null || !head.NodeId.Equals(stale.NodeId))
                    return false;

                if (pingOk)
                {
                    head.LastSeen = DateTime.UtcNow;
                    head.FailCount = 0;
                    return false;
                }

                bucket.Remove(stale.NodeId);
                DhtPeer copy = incoming.Clone();
                if (copy.LastSeen == default)
                    copy.LastSeen = DateTime.UtcNow;
                bool added = bucket.TryAdd(copy);
                RebuildSiblingsUnlocked();
                return added;
            }
        }

        public List<DhtPeer> Closest(DhtKey target, int n)
        {
            List<DhtPeer> all = SnapshotContacts();
            return SiblingList.Closest(target, all, n);
        }

        public List<DhtPeer> SnapshotContacts()
        {
            List<DhtPeer> all = new List<DhtPeer>();
            lock (m_rtLock)
            {
                for (int i = 0; i < Bits; i++)
                    m_buckets[i].CopyTo(all);
            }
            return all;
        }

        public int FilledBucketCount()
        {
            lock (m_rtLock)
            {
                int n = 0;
                for (int i = 0; i < Bits; i++)
                {
                    if (m_buckets[i].Count > 0)
                        n++;
                }
                return n;
            }
        }

        public IReadOnlyList<DhtPeer> SnapshotSiblings()
        {
            lock (m_rtLock)
                return m_siblings.Snapshot();
        }

        public bool IsResponsible(DhtKey key)
        {
            List<DhtPeer> known = SnapshotContacts();
            lock (m_rtLock)
                return m_siblings.IsResponsible(key, known);
        }

        private void RebuildSiblingsUnlocked()
        {
            List<DhtPeer> all = new List<DhtPeer>();
            for (int i = 0; i < Bits; i++)
                m_buckets[i].CopyTo(all);
            m_siblings.Rebuild(all);
        }
    }
}
