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
using System.Threading.Tasks;
using log4net;

namespace OpenSim.Addons.UUIDDHT
{
    public sealed class DhtLookup
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly UuidDhtNode m_node;

        public DhtLookup(UuidDhtNode node)
        {
            m_node = node ?? throw new ArgumentNullException(nameof(node));
        }

        public DhtRecord FindValue(DhtKey key, CancellationToken ct)
        {
            if (m_node.Store.TryGet(key, out DhtRecord local) && local != null && !local.Tombstone)
                return local;
            if (m_node.TryCachedNode(key, out DhtRecord cached))
                return cached;

            List<DhtRecord> found = new List<DhtRecord>();
            Iterate(key, true, ct, found);
            DhtRecord merged = Merge(found);
            if (merged != null && merged.KindEnum == DhtRecordKind.Node)
                m_node.CacheNodeRecord(merged);
            return merged;
        }

        public List<DhtPeer> FindNode(DhtKey key, CancellationToken ct)
        {
            Iterate(key, false, ct, new List<DhtRecord>());
            int n = m_node.Config.SiblingSize > 0 ? m_node.Config.SiblingSize : 20;
            return m_node.Table.Closest(key, n);
        }

        public static DhtRecord Merge(IList<DhtRecord> values)
        {
            if (values == null || values.Count == 0)
                return null;

            List<DhtRecord> live = new List<DhtRecord>();
            for (int i = 0; i < values.Count; i++)
            {
                DhtRecord r = values[i];
                if (r == null || r.Tombstone)
                    continue;
                live.Add(r);
            }
            if (live.Count == 0)
                return null;

            long bestSeq = -1;
            for (int i = 0; i < live.Count; i++)
            {
                if (live[i].Seq > bestSeq)
                    bestSeq = live[i].Seq;
            }

            List<DhtRecord> top = new List<DhtRecord>();
            for (int i = 0; i < live.Count; i++)
            {
                if (live[i].Seq == bestSeq)
                    top.Add(live[i]);
            }

            string owner = top[0].NodeId;
            for (int i = 1; i < top.Count; i++)
            {
                if (top[i].NodeId != owner)
                {
                    m_log.WarnFormat("[UUID-DHT]: lookup merge split nodeId at seq={0}", bestSeq);
                    return null;
                }
                if (top[i].Sig != top[0].Sig || top[i].Key != top[0].Key)
                {
                    m_log.Warn("[UUID-DHT]: lookup merge same seq/nodeId but not identical");
                    return null;
                }
            }
            return top[0];
        }

        private void Iterate(DhtKey key, bool findValue, CancellationToken outer, List<DhtRecord> found)
        {
            int timeout = m_node.Config.LookupTimeoutMs > 0 ? m_node.Config.LookupTimeoutMs : UuidDhtConfig.DefaultLookupTimeoutMs;
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            cts.CancelAfter(timeout);
            CancellationToken ct = cts.Token;

            List<DhtPeer> seeds = m_node.Table.SnapshotContacts();
            int d = Math.Max(1, m_node.Config.DisjointPaths);
            PathState[] paths = new PathState[d];
            for (int i = 0; i < d; i++)
                paths[i] = new PathState();

            List<DhtPeer> sorted = SiblingList.Closest(key, seeds, Math.Max(seeds.Count, 1));
            for (int i = 0; i < sorted.Count; i++)
                paths[i % d].Verified.Add(sorted[i].Clone());

            object gate = new object();
            HashSet<string> contacted = new HashSet<string>(StringComparer.Ordinal);

            Task[] tasks = new Task[d];
            for (int i = 0; i < d; i++)
            {
                PathState path = paths[i];
                tasks[i] = Task.Run(() => RunPath(path, key, findValue, ct, gate, contacted), ct);
            }
            try
            {
                Task.WaitAll(tasks, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException)
            {
            }

            lock (gate)
            {
                for (int i = 0; i < d; i++)
                    found.AddRange(paths[i].Values);
            }
        }

        private void RunPath(PathState path, DhtKey key, bool findValue, CancellationToken ct,
            object gate, HashSet<string> contacted)
        {
            int alpha = Alpha();
            while (!ct.IsCancellationRequested)
            {
                List<DhtPeer> snapshotVerified;
                lock (gate)
                    snapshotVerified = CloneList(path.Verified);

                bool admitted = Admit(path, key, snapshotVerified, alpha, ct, gate, contacted);
                bool queried = Query(path, snapshotVerified, key, findValue, alpha, ct, gate, contacted);
                if (!admitted && !queried)
                    break;
            }
        }

        private bool Admit(PathState path, DhtKey key, List<DhtPeer> snapshotVerified, int alpha,
            CancellationToken ct, object gate, HashSet<string> contacted)
        {
            List<DhtPeer> cands;
            lock (gate)
                cands = CloneList(path.Candidates);
            cands.Sort((a, b) => a.NodeId.Xor(key).CompareTo(b.NodeId.Xor(key)));

            bool haveBest = snapshotVerified.Count > 0;
            DhtKey bestXor = haveBest ? BestXor(snapshotVerified, key) : default;
            int n = 0;
            for (int i = 0; i < cands.Count && n < alpha && !ct.IsCancellationRequested; i++)
            {
                DhtPeer cand = cands[i];
                string idHex = cand.NodeId.ToHex();
                if (cand.NodeId.Equals(m_node.Identity.NodeId))
                    continue;
                lock (gate)
                {
                    if (contacted.Contains(idHex))
                        continue;
                }
                if (haveBest && cand.NodeId.Xor(key).CompareTo(bestXor) >= 0)
                    continue;

                try
                {
                    DhtNodeDocument doc = m_node.Transport.GetNode(cand.HomeURI, ct);
                    if (doc == null || doc.NodeId != idHex)
                        continue;
                    DhtPeer verified = new DhtPeer
                    {
                        NodeId = cand.NodeId,
                        HomeURI = string.IsNullOrEmpty(doc.HomeURI) ? cand.HomeURI : doc.HomeURI,
                        LastSeen = DateTime.UtcNow
                    };
                    DhtPeer stale;
                    if (m_node.Table.TryInsert(verified, out stale) == DhtInsertStatus.Added)
                        m_node.PersistKnownSeeds();
                    lock (gate)
                    {
                        contacted.Add(idHex);
                        path.Verified.Add(verified);
                        RemovePeer(path.Candidates, cand.NodeId);
                    }
                    n++;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    lock (gate)
                        RemovePeer(path.Candidates, cand.NodeId);
                }
            }
            return n > 0;
        }

        private bool Query(PathState path, List<DhtPeer> snapshotVerified, DhtKey key, bool findValue,
            int alpha, CancellationToken ct, object gate, HashSet<string> contacted)
        {
            snapshotVerified.Sort((a, b) => a.NodeId.Xor(key).CompareTo(b.NodeId.Xor(key)));
            List<DhtPeer> toQuery = new List<DhtPeer>();
            lock (gate)
            {
                for (int i = 0; i < snapshotVerified.Count && toQuery.Count < alpha; i++)
                {
                    DhtPeer p = snapshotVerified[i];
                    string idHex = p.NodeId.ToHex();
                    if (p.NodeId.Equals(m_node.Identity.NodeId))
                        continue;
                    if (path.Queried.Contains(idHex))
                        continue;
                    path.Queried.Add(idHex);
                    contacted.Add(idHex);
                    toQuery.Add(p);
                }
            }

            string op = findValue ? "FIND_VALUE" : "FIND_NODE";
            int ok = 0;
            for (int i = 0; i < toQuery.Count && !ct.IsCancellationRequested; i++)
            {
                DhtPeer p = toQuery[i];
                try
                {
                    DhtRpcRequest req = DhtRpcRequest.Create(m_node.Identity, m_node.Config.HomeURI, op,
                        new DhtRpcBody { Key = key.ToHex() });
                    DhtRpcResponse resp = m_node.Transport.Rpc(p.HomeURI, req, ct);
                    if (resp == null || !resp.Ok || resp.Sender != p.NodeId.ToHex())
                        continue;
                    ok++;
                    if (findValue && resp.Value != null)
                    {
                        lock (gate)
                            path.Values.Add(resp.Value);
                        if (resp.Value.KindEnum == DhtRecordKind.Node)
                            m_node.CacheNodeRecord(resp.Value);
                    }
                    if (resp.Peers == null)
                        continue;
                    for (int j = 0; j < resp.Peers.Count; j++)
                    {
                        DhtPeerWire w = resp.Peers[j];
                        if (w == null || !DhtKey.TryParseHex(w.NodeId, out DhtKey pid))
                            continue;
                        if (pid.Equals(m_node.Identity.NodeId))
                            continue;
                        lock (gate)
                        {
                            if (contacted.Contains(w.NodeId) || path.Queried.Contains(w.NodeId))
                                continue;
                            if (HasPeer(path.Verified, pid) || HasPeer(path.Candidates, pid))
                                continue;
                            path.Candidates.Add(new DhtPeer { NodeId = pid, HomeURI = w.HomeURI });
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                }
            }
            return ok > 0;
        }

        private int Alpha()
        {
            int a = m_node.Config.Alpha;
            return a > 0 ? a : 3;
        }

        private static DhtKey BestXor(List<DhtPeer> verified, DhtKey key)
        {
            if (verified == null || verified.Count == 0)
                return DhtKey.FromBytes(new byte[32]);
            DhtKey best = verified[0].NodeId.Xor(key);
            for (int i = 1; i < verified.Count; i++)
            {
                DhtKey x = verified[i].NodeId.Xor(key);
                if (x.CompareTo(best) < 0)
                    best = x;
            }
            return best;
        }

        private static List<DhtPeer> CloneList(List<DhtPeer> src)
        {
            List<DhtPeer> copy = new List<DhtPeer>(src.Count);
            for (int i = 0; i < src.Count; i++)
                copy.Add(src[i].Clone());
            return copy;
        }

        private static bool HasPeer(List<DhtPeer> list, DhtKey id)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].NodeId.Equals(id))
                    return true;
            }
            return false;
        }

        private static void RemovePeer(List<DhtPeer> list, DhtKey id)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].NodeId.Equals(id))
                    list.RemoveAt(i);
            }
        }

        private sealed class PathState
        {
            public List<DhtPeer> Verified = new List<DhtPeer>();
            public List<DhtPeer> Candidates = new List<DhtPeer>();
            public HashSet<string> Queried = new HashSet<string>(StringComparer.Ordinal);
            public List<DhtRecord> Values = new List<DhtRecord>();
        }
    }
}
