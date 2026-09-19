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
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Groups;
using OpenSim.Services.Interfaces;

namespace OpenSim.Addons.UUIDDHT
{
    public sealed class UuidDhtNode
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly object s_initLock = new object();
        private static UuidDhtNode s_instance;

        private readonly object m_startLock = new object();
        private readonly object m_joinLock = new object();
        private readonly ConcurrentDictionary<string, long> m_replay = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, TokenBucket> m_rate = new ConcurrentDictionary<string, TokenBucket>();
        private readonly ConcurrentDictionary<string, DhtRecord> m_nodeCache = new ConcurrentDictionary<string, DhtRecord>();
        private readonly object m_seedLock = new object();
        private string m_lastSeedsWritten = string.Empty;
        private bool m_started;
        private IUserAccountService m_users;
        private IGroupsService m_groups;

        private UuidDhtNode(UuidDhtConfig config, DhtIdentity identity, IDhtStore store,
            RoutingTable table, IDhtTransport transport, IDhtStoreValidator validator)
        {
            Config = config;
            Identity = identity;
            Store = store;
            Table = table;
            Transport = transport;
            Validator = validator is LocalAwareValidator
                ? validator
                : new LocalAwareValidator(this, validator);
            Lookup = new DhtLookup(this);
            Publisher = new DhtPublisher(this);
        }

        public UuidDhtConfig Config { get; }
        public DhtIdentity Identity { get; }
        public IDhtStore Store { get; }
        public RoutingTable Table { get; }
        public IDhtTransport Transport { get; }
        public IDhtStoreValidator Validator { get; }
        public DhtLookup Lookup { get; }
        public DhtPublisher Publisher { get; }
        public DateTime LastJoinUtc { get; private set; }

        internal IUserAccountService Users
        {
            get { return m_users; }
        }

        internal IGroupsService Groups
        {
            get { return m_groups; }
        }

        internal IDhtLocalUuidSource LocalUuids { get; set; }

        public static UuidDhtNode GetOrCreate(IConfigSource config)
        {
            lock (s_initLock)
            {
                if (s_instance != null)
                    return s_instance;

                UuidDhtConfig cfg = UuidDhtConfig.From(config);
                if (!cfg.Enabled)
                    return null;

                SqliteDhtStore store = new SqliteDhtStore(cfg.StorePath, cfg.AllowPrivateHomeURI,
                    cfg.MaxUuidsStoredPerOwner, cfg.ExpireHours, cfg.TombstoneHours);
                DhtIdentity identity;
                try
                {
                    identity = DhtIdentity.LoadOrCreate(cfg.IdentityPath, store);
                }
                catch
                {
                    store.Dispose();
                    throw;
                }

                DhtPeer self = new DhtPeer { NodeId = identity.NodeId, HomeURI = cfg.HomeURI };
                RoutingTable table = new RoutingTable(self, cfg.K, cfg.SiblingSize);
                DhtHttpClient http = new DhtHttpClient(cfg.AllowPrivateHomeURI, cfg.RpcTimeoutMs);
                s_instance = new UuidDhtNode(cfg, identity, store, table, http, http);
                s_instance.LocalUuids = new DhtLocalUuidSql(config);
                return s_instance;
            }
        }

        public static UuidDhtNode CreateForTests(UuidDhtConfig config, DhtIdentity identity, IDhtStore store,
            IDhtTransport transport, IDhtStoreValidator validator)
        {
            DhtPeer self = new DhtPeer { NodeId = identity.NodeId, HomeURI = config.HomeURI };
            RoutingTable table = new RoutingTable(self, config.K, config.SiblingSize);
            return new UuidDhtNode(config, identity, store, table, transport, validator);
        }

        public void SetUserAccountService(IUserAccountService users)
        {
            m_users = users;
        }

        public void SetGroupsService(IGroupsService groups)
        {
            m_groups = groups;
        }

        public void SetLocalUuidSource(IDhtLocalUuidSource source)
        {
            LocalUuids = source;
        }

        public void CacheNodeRecord(DhtRecord record)
        {
            if (record == null || record.KindEnum != DhtRecordKind.Node || record.Tombstone)
                return;
            if (!string.IsNullOrEmpty(record.NodeId))
                m_nodeCache[record.NodeId] = record;
        }

        public bool TryCachedNode(DhtKey nodeId, out DhtRecord record)
        {
            return m_nodeCache.TryGetValue(nodeId.ToHex(), out record);
        }

        public void Start()
        {
            lock (m_startLock)
            {
                if (m_started)
                    return;
                m_started = true;
            }
            RegisterConsole();
            Thread joinThread = new Thread(JoinThenPublish)
            {
                IsBackground = true,
                Name = "UUID-DHT-Join"
            };
            joinThread.Start();
        }

        private void JoinThenPublish()
        {
            const int attempts = 5;
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    if (Join())
                    {
                        Publisher.Start();
                        RetryBootstrapIfIsolated();
                        if (Table.SnapshotContacts().Count > 0)
                            Publisher.Republish();
                        return;
                    }
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[UUID-DHT]: join failed: {0}", e.Message);
                }
                if (i + 1 < attempts)
                    Thread.Sleep(500 * (i + 1));
            }
            m_log.Warn("[UUID-DHT]: join did not succeed; handlers still serve /dht/node");
        }

        private void RetryBootstrapIfIsolated()
        {
            int others = 0;
            string[] seeds = Config.Bootstrap ?? Array.Empty<string>();
            for (int i = 0; i < seeds.Length; i++)
            {
                if (!IsOwnHome(seeds[i]))
                    others++;
            }
            if (others == 0)
                return;

            for (int i = 0; i < 8 && Table.SnapshotContacts().Count == 0; i++)
            {
                Thread.Sleep(500 * (i + 1));
                PingSeeds();
            }
        }

        public bool Join()
        {
            lock (m_joinLock)
                return JoinUnlocked();
        }

        public DhtRpcResponse HandleRpc(DhtRpcRequest req)
        {
            if (req == null || req.V != 1 || string.IsNullOrEmpty(req.Op))
                return Fail(req, "malformed");

            string op = req.Op;
            if (op != "PING" && op != "FIND_NODE" && op != "FIND_VALUE" && op != "STORE" && op != "DELETE")
                return Fail(req, "malformed");

            if (!VerifyRequest(req, out string verifyErr))
                return Fail(req, verifyErr);

            DhtRpcBody body = req.Body ?? new DhtRpcBody();
            List<DhtPeerWire> peers;
            DhtRecord value = null;
            bool ok = true;
            string error = null;

            switch (op)
            {
                case "PING":
                    DhtKey pingTarget = DhtKey.TryParseHex(req.Sender, out DhtKey sid) ? sid : Identity.NodeId;
                    peers = PeersNear(pingTarget);
                    QueueAdmit(req);
                    break;
                case "FIND_NODE":
                    if (!DhtKey.TryParseHex(body.Key, out DhtKey nk))
                        return Fail(req, "malformed");
                    peers = PeersNear(nk);
                    QueueAdmit(req);
                    break;
                case "FIND_VALUE":
                    if (!DhtKey.TryParseHex(body.Key, out DhtKey vk))
                        return Fail(req, "malformed");
                    Store.TryGet(vk, out value);
                    peers = PeersNear(vk);
                    QueueAdmit(req);
                    break;
                case "STORE":
                case "DELETE":
                    DhtStorePutResult put = Store.Put(body.Record, body.Node, Validator);
                    ok = put.Ok;
                    error = put.Error;
                    DhtKey sk = Identity.NodeId;
                    if (body.Record != null && body.Record.TryParseDhtKey(out DhtKey rk))
                        sk = rk;
                    peers = PeersNear(sk);
                    break;
                default:
                    return Fail(req, "malformed");
            }

            return DhtRpcResponse.Create(Identity, Config.HomeURI, op, req.Nonce, ok, error, peers, value);
        }

        public DhtNodeDocument CreateNodeDocument()
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string nodeId = Identity.NodeId.ToHex();
            string pub = DhtSigner.ToHex(Identity.PublicKey);
            byte[] sig = Identity.Sign(DhtCanonical.Utf8(DhtCanonical.Proof(nodeId, Config.HomeURI, pub, ts)));
            return new DhtNodeDocument
            {
                V = 1,
                NodeId = nodeId,
                HomeURI = Config.HomeURI,
                Pubkey = pub,
                Ts = ts,
                Seq = Identity.NodeSeq,
                Sig = DhtSigner.ToHex(sig)
            };
        }

        public DhtOwnsDocument CreateOwnsDocument(UUID uuid)
        {
            bool has = OwnsLocal(uuid);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string nodeId = Identity.NodeId.ToHex();
            string canon = DhtCanonical.Owns(uuid.ToString(), nodeId, Config.HomeURI, has, ts);
            return new DhtOwnsDocument
            {
                V = 1,
                Uuid = uuid.ToString(),
                NodeId = nodeId,
                HomeURI = Config.HomeURI,
                Has = has,
                Ts = ts,
                Sig = DhtSigner.ToHex(Identity.Sign(DhtCanonical.Utf8(canon)))
            };
        }

        public bool OwnsLocal(UUID uuid)
        {
            if (m_users != null && m_users.GetUserAccount(UUID.Zero, uuid) != null)
                return true;
            if (m_groups is UuidDhtGroupsService dhtGroups)
            {
                ExtendedGroupRecord g = dhtGroups.GetLocalGroupRecord(UUID.Zero.ToString(), uuid);
                if (g != null && string.IsNullOrEmpty(g.ServiceLocation))
                    return true;
            }
            return false;
        }

        public bool TryRateLimit(IPAddress ip)
        {
            if (ip == null)
                return true;
            TokenBucket b = m_rate.GetOrAdd(ip.ToString(), _ => new TokenBucket());
            return b.TryTake();
        }

        internal static void ResetForTests()
        {
            lock (s_initLock)
                s_instance = null;
        }

        private bool JoinUnlocked()
        {
            if (!DhtLocator.TryNormalize(Config.HomeURI, Config.AllowPrivateHomeURI, out string home))
            {
                m_log.Error("[UUID-DHT]: HomeURI missing or unusable; join disabled");
                return false;
            }

            int cb = Config.CallbackTimeoutMs > 0 ? Config.CallbackTimeoutMs : UuidDhtConfig.DefaultCallbackTimeoutMs;
            using CancellationTokenSource proofCts = new CancellationTokenSource(cb);
            if (!ProveHome(home, proofCts.Token))
            {
                m_log.WarnFormat("[UUID-DHT]: could not prove HomeURI {0}; join disabled", home);
                return false;
            }

            PingSeeds();
            int lookupMs = Config.LookupTimeoutMs > 0 ? Config.LookupTimeoutMs : UuidDhtConfig.DefaultLookupTimeoutMs;
            using (CancellationTokenSource findCts = new CancellationTokenSource(lookupMs))
                Lookup.FindNode(Identity.NodeId, findCts.Token);

            DhtKey locKey = DhtKey.ForLoc(home);
            DhtRecord locFound;
            using (CancellationTokenSource locCts = new CancellationTokenSource(lookupMs))
                locFound = Lookup.FindValue(locKey, locCts.Token);
            if (locFound != null && !locFound.Tombstone && locFound.NodeId != Identity.NodeId.ToHex())
            {
                if (OccupantStillProves(locFound, home))
                {
                    m_log.WarnFormat("[UUID-DHT]: join refused, home taken by {0}", locFound.NodeId);
                    return false;
                }
            }

            string oldHome = null;
            if (Store.TryGet(Identity.NodeId, out DhtRecord haveNode) && haveNode != null && !haveNode.Tombstone)
                oldHome = haveNode.HomeURI;

            if (LocIsOurs(locKey, home) && oldHome == home)
            {
                LastJoinUtc = DateTime.UtcNow;
                PersistKnownSeeds();
                m_log.InfoFormat("[UUID-DHT]: joined node {0} at {1}", Identity.NodeId.ToHex(), home);
                return true;
            }

            long locSeq = NextLocSeq(locKey);
            DhtRecord locRec = DhtRecord.CreateLoc(home, Identity.NodeId, locSeq, false);
            locRec.SignWith(Identity);

            bool moving = !string.IsNullOrEmpty(oldHome) && oldHome != home;
            long nodeSeq = Identity.NodeSeq;
            if (moving)
                nodeSeq = Identity.BumpNodeSeq();
            DhtRecord nodeRec = DhtRecord.CreateNode(Identity.NodeId, home, Identity.PublicKey, nodeSeq);
            nodeRec.SignWith(Identity);

            using CancellationTokenSource storeCts = new CancellationTokenSource(lookupMs * 3);
            StoreFanout(locRec, nodeRec, "STORE", storeCts.Token);
            StoreFanout(nodeRec, null, "STORE", storeCts.Token);

            if (moving)
            {
                long oldSeq = 1;
                DhtKey oldLocKey = DhtKey.ForLoc(oldHome);
                if (Store.TryGet(oldLocKey, out DhtRecord oldLoc) && oldLoc != null)
                    oldSeq = oldLoc.Seq + 1;
                DhtRecord tomb = DhtRecord.CreateLoc(oldHome, Identity.NodeId, oldSeq, true);
                tomb.SignWith(Identity);
                StoreFanout(tomb, nodeRec, "DELETE", storeCts.Token);
            }

            LastJoinUtc = DateTime.UtcNow;
            PersistKnownSeeds();
            m_log.InfoFormat("[UUID-DHT]: joined node {0} at {1}", Identity.NodeId.ToHex(), home);
            return LocIsOurs(locKey, home);
        }

        internal DhtRecord OwnNodeRecord()
        {
            if (Store.TryGet(Identity.NodeId, out DhtRecord rec) && rec != null && !rec.Tombstone)
                return rec;
            string home = Config.HomeURI ?? string.Empty;
            DhtLocator.TryNormalize(home, Config.AllowPrivateHomeURI, out home);
            DhtRecord created = DhtRecord.CreateNode(Identity.NodeId, home ?? Config.HomeURI, Identity.PublicKey, Identity.NodeSeq);
            created.SignWith(Identity);
            return created;
        }

        internal void RefreshLocatorRecords()
        {
            if (!DhtLocator.TryNormalize(Config.HomeURI, Config.AllowPrivateHomeURI, out string home))
                return;
            long nodeSeq = Identity.BumpNodeSeq();
            DhtRecord nodeRec = DhtRecord.CreateNode(Identity.NodeId, home, Identity.PublicKey, nodeSeq);
            nodeRec.SignWith(Identity);
            DhtKey locKey = DhtKey.ForLoc(home);
            DhtRecord locRec = DhtRecord.CreateLoc(home, Identity.NodeId, NextLocSeq(locKey), false);
            locRec.SignWith(Identity);
            int lookupMs = Config.LookupTimeoutMs > 0 ? Config.LookupTimeoutMs : UuidDhtConfig.DefaultLookupTimeoutMs;
            using CancellationTokenSource cts = new CancellationTokenSource(lookupMs * 3);
            StoreFanout(locRec, nodeRec, "STORE", cts.Token);
            StoreFanout(nodeRec, null, "STORE", cts.Token);
        }

        internal bool StoreFanout(DhtRecord record, DhtRecord attachedNode, string op, CancellationToken ct)
        {
            if (record == null || !record.TryParseDhtKey(out DhtKey key))
                return false;

            DhtStorePutResult local = Store.Put(record, attachedNode, Validator);
            HashSet<string> okIds = new HashSet<string>(StringComparer.Ordinal);
            if (local.Ok)
                okIds.Add(Identity.NodeId.ToHex());
            else
                m_log.WarnFormat("[UUID-DHT]: local STORE {0} failed: {1}", record.Key, local.Error);

            List<DhtPeer> closest = Lookup.FindNode(key, ct);
            List<DhtPeer> targets = new List<DhtPeer>();
            for (int i = 0; i < closest.Count; i++)
            {
                if (!closest[i].NodeId.Equals(Identity.NodeId))
                    targets.Add(closest[i]);
            }

            DhtPeer self = new DhtPeer { NodeId = Identity.NodeId, HomeURI = Config.HomeURI };
            List<DhtPeer> withSelf = new List<DhtPeer>(targets) { self };
            int s = Config.SiblingSize > 0 ? Config.SiblingSize : 20;
            List<DhtPeer> among = SiblingList.Closest(key, withSelf, s);

            List<DhtPeer> remote = new List<DhtPeer>();
            for (int i = 0; i < among.Count; i++)
            {
                if (!among[i].NodeId.Equals(Identity.NodeId))
                    remote.Add(among[i]);
            }

            RpcStore(remote, record, attachedNode, op, ct, okIds);
            int need = Math.Max(1, Math.Min(s / 2, Math.Max(1, among.Count)));
            if (okIds.Count >= need)
                return true;

            List<DhtPeer> retry = new List<DhtPeer>();
            for (int i = 0; i < remote.Count; i++)
            {
                if (!okIds.Contains(remote[i].NodeId.ToHex()))
                    retry.Add(remote[i]);
            }
            RpcStore(retry, record, attachedNode, op, ct, okIds);
            if (okIds.Count < need)
                m_log.WarnFormat("[UUID-DHT]: STORE fan-out {0} got {1}/{2}", record.Key, okIds.Count, need);
            return okIds.Count >= need;
        }

        private void RpcStore(List<DhtPeer> peers, DhtRecord record, DhtRecord attachedNode, string op,
            CancellationToken ct, HashSet<string> okIds)
        {
            int alpha = Config.Alpha > 0 ? Config.Alpha : 3;
            for (int i = 0; i < peers.Count && !ct.IsCancellationRequested; i += alpha)
            {
                int batch = Math.Min(alpha, peers.Count - i);
                Task<bool>[] tasks = new Task<bool>[batch];
                DhtPeer[] batchPeers = new DhtPeer[batch];
                for (int j = 0; j < batch; j++)
                {
                    DhtPeer p = peers[i + j];
                    batchPeers[j] = p;
                    tasks[j] = Task.Run(() => RpcStoreOne(p, record, attachedNode, op, ct), ct);
                }
                try
                {
                    Task.WaitAll(tasks, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (AggregateException)
                {
                }
                for (int j = 0; j < tasks.Length; j++)
                {
                    try
                    {
                        if (tasks[j].Status == TaskStatus.RanToCompletion && tasks[j].Result)
                            okIds.Add(batchPeers[j].NodeId.ToHex());
                    }
                    catch
                    {
                    }
                }
            }
        }

        private bool RpcStoreOne(DhtPeer peer, DhtRecord record, DhtRecord attachedNode, string op, CancellationToken ct)
        {
            try
            {
                DhtRpcRequest req = DhtRpcRequest.Create(Identity, Config.HomeURI, op,
                    new DhtRpcBody { Record = record, Node = attachedNode });
                DhtRpcResponse resp = Transport.Rpc(peer.HomeURI, req, ct);
                return resp != null && resp.Ok && resp.Sender == peer.NodeId.ToHex();
            }
            catch
            {
                return false;
            }
        }

        private bool ProveHome(string home, CancellationToken ct)
        {
            // Real HTTP self-GET of HomeURI hits this process's listener. At boot
            // that sync-over-async call occupies a worker the server needs, so
            // GetNode times out (~RpcTimeoutMs) even though curl works later.
            // The document we would serve is CreateNodeDocument(); verify that.
            if (Transport is DhtHttpClient)
            {
                DhtNodeDocument local = CreateNodeDocument();
                if (DocumentProvesHome(local, home))
                    return true;
                m_log.WarnFormat("[UUID-DHT]: in-process HomeURI document did not match {0}", home);
            }

            try
            {
                DhtNodeDocument doc = Transport.GetNode(home, ct);
                return DocumentProvesHome(doc, home);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[UUID-DHT]: prove HomeURI {0} failed: {1}", home, e.Message);
                return false;
            }
        }

        private bool DocumentProvesHome(DhtNodeDocument doc, string home)
        {
            if (doc == null || doc.NodeId != Identity.NodeId.ToHex())
                return false;
            string docHome = doc.HomeURI ?? string.Empty;
            if (DhtLocator.TryNormalize(docHome, Config.AllowPrivateHomeURI, out string normalized))
                docHome = normalized;
            return string.Equals(docHome, home, StringComparison.Ordinal);
        }

        private bool OccupantStillProves(DhtRecord loc, string ourHome)
        {
            try
            {
                string occHome = string.IsNullOrEmpty(loc.HomeURI) ? ourHome : loc.HomeURI;
                DhtNodeDocument doc = Transport.GetNode(occHome, CancellationToken.None);
                if (doc == null || doc.NodeId != loc.NodeId)
                    return false;
                return doc.HomeURI == ourHome || doc.HomeURI == occHome;
            }
            catch
            {
                return false;
            }
        }

        private bool LocIsOurs(DhtKey locKey, string home)
        {
            if (!Store.TryGet(locKey, out DhtRecord loc) || loc == null || loc.Tombstone)
                return false;
            return loc.NodeId == Identity.NodeId.ToHex() && loc.HomeURI == home;
        }

        private long NextLocSeq(DhtKey locKey)
        {
            if (Store.TryGet(locKey, out DhtRecord loc) && loc != null && loc.NodeId == Identity.NodeId.ToHex())
                return loc.Seq + 1;
            return 1;
        }

        private void PingSeeds()
        {
            string[] seeds = Config.Bootstrap ?? Array.Empty<string>();
            if (seeds.Length == 0)
            {
                m_log.Info("[UUID-DHT]: bootstrap empty/failed; isolated until a peer PINGs us");
                return;
            }

            int ok = 0;
            int skippedSelf = 0;
            using CancellationTokenSource cts = new CancellationTokenSource(Config.RpcTimeoutMs * Math.Max(1, seeds.Length));
            for (int i = 0; i < seeds.Length; i++)
            {
                if (IsOwnHome(seeds[i]))
                {
                    skippedSelf++;
                    continue;
                }
                try
                {
                    DhtNodeDocument doc = Transport.GetNode(seeds[i], cts.Token);
                    if (DhtKey.TryParseHex(doc.NodeId, out DhtKey id))
                    {
                        DhtPeer stale;
                        if (Table.TryInsert(new DhtPeer { NodeId = id, HomeURI = doc.HomeURI, LastSeen = DateTime.UtcNow }, out stale)
                            == DhtInsertStatus.Added)
                        {
                            OnPeerAdded();
                        }
                    }
                    DhtRpcRequest ping = DhtRpcRequest.Create(Identity, Config.HomeURI, "PING", new DhtRpcBody());
                    Transport.Rpc(seeds[i], ping, cts.Token);
                    ok++;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[UUID-DHT]: bootstrap seed {0} failed: {1}", seeds[i], e.Message);
                }
            }
            if (ok == 0 && skippedSelf == seeds.Length)
                m_log.Info("[UUID-DHT]: bootstrap is this server only; isolated until a peer PINGs us");
            else if (ok == 0)
                m_log.Info("[UUID-DHT]: bootstrap empty/failed; isolated until a peer PINGs us");
            else
                m_log.InfoFormat("[UUID-DHT]: bootstrap contacted {0} seed(s)", ok);
        }

        private void OnPeerAdded()
        {
            PersistKnownSeeds();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Publisher.Republish();
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[UUID-DHT]: republish after peer: {0}", e.Message);
                }
            });
        }

        internal void PersistKnownSeeds()
        {
            string path = Config.SeedsPath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            List<string> extra = new List<string>();
            if (Config.Bootstrap != null)
                extra.AddRange(Config.Bootstrap);
            string[] existing = DhtSeedFile.Read(path);
            extra.AddRange(existing);

            List<string> discovered = new List<string>();
            List<DhtPeer> peers = Table.SnapshotContacts();
            for (int i = 0; i < peers.Count; i++)
            {
                if (!string.IsNullOrEmpty(peers[i].HomeURI))
                    discovered.Add(peers[i].HomeURI);
            }

            int max = Config.MaxBootstrapSeeds > 0 ? Config.MaxBootstrapSeeds : DhtSeedFile.DefaultMaxSeeds;
            List<string> seeds = DhtSeedFile.Collect(Config.HomeURI, extra, discovered,
                Config.AllowPrivateHomeURI, max);
            string joined = DhtSeedFile.Format(seeds);
            lock (m_seedLock)
            {
                if (string.Equals(joined, m_lastSeedsWritten, StringComparison.Ordinal))
                    return;
                try
                {
                    DhtSeedFile.Write(path, seeds);
                    m_lastSeedsWritten = joined;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[UUID-DHT]: could not write seeds to {0}: {1}", path, e.Message);
                }
            }
        }

        internal bool IsOwnHome(string home)
        {
            return DhtLocator.SameHome(home, Config.HomeURI, Config.AllowPrivateHomeURI);
        }

        private bool VerifyRequest(DhtRpcRequest req, out string error)
        {
            error = "malformed";
            if (string.IsNullOrEmpty(req.Sender) || string.IsNullOrEmpty(req.Pubkey) || string.IsNullOrEmpty(req.Nonce) || string.IsNullOrEmpty(req.Sig))
                return false;
            if (req.Nonce.Length != 32)
                return false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - req.Ts) > 300)
                return false;
            if (!DhtLocator.TryNormalize(req.SenderHome, Config.AllowPrivateHomeURI, out _))
            {
                error = "bad_locator";
                return false;
            }

            byte[] pub;
            byte[] sig;
            try
            {
                pub = Convert.FromHexString(req.Pubkey);
                sig = Convert.FromHexString(req.Sig);
            }
            catch (FormatException)
            {
                return false;
            }
            if (DhtSigner.NodeIdFromPubkey(pub).ToHex() != req.Sender)
            {
                error = "bad_sig";
                return false;
            }

            DhtRpcBody body = req.Body ?? new DhtRpcBody();
            string hash = DhtJson.BodyHash(body);
            string canon = DhtCanonical.RpcRequest(req.Op, req.Sender, req.SenderHome, req.Nonce, req.Ts, hash);
            if (!DhtIdentity.Verify(pub, DhtCanonical.Utf8(canon), sig))
            {
                error = "bad_sig";
                return false;
            }

            string replayKey = req.Sender + ":" + req.Nonce;
            if (!m_replay.TryAdd(replayKey, req.Ts))
            {
                error = "malformed";
                return false;
            }
            if (m_replay.Count > 10000)
                SweepReplay(now);

            return true;
        }

        private void SweepReplay(long now)
        {
            foreach (KeyValuePair<string, long> kv in m_replay)
            {
                if (now - kv.Value > 600)
                    m_replay.TryRemove(kv.Key, out _);
            }
        }

        private List<DhtPeerWire> PeersNear(DhtKey key)
        {
            List<DhtPeer> closest = Table.Closest(key, Config.K);
            List<DhtPeerWire> wire = new List<DhtPeerWire>(closest.Count);
            for (int i = 0; i < closest.Count; i++)
            {
                wire.Add(new DhtPeerWire { NodeId = closest[i].NodeId.ToHex(), HomeURI = closest[i].HomeURI });
            }
            return wire;
        }

        private void QueueAdmit(DhtRpcRequest req)
        {
            if (!DhtKey.TryParseHex(req.Sender, out DhtKey id) || string.IsNullOrEmpty(req.SenderHome))
                return;
            string claimed = req.SenderHome;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    DhtNodeDocument doc = Transport.GetNode(claimed, CancellationToken.None);
                    if (doc == null || doc.NodeId != id.ToHex())
                        return;
                    string home = string.IsNullOrEmpty(doc.HomeURI) ? claimed : doc.HomeURI;
                    DhtPeer peer = new DhtPeer { NodeId = id, HomeURI = home, LastSeen = DateTime.UtcNow };
                    DhtPeer stale;
                    if (Table.TryInsert(peer, out stale) == DhtInsertStatus.Added)
                        OnPeerAdded();
                }
                catch
                {
                }
            });
        }

        private DhtRpcResponse Fail(DhtRpcRequest req, string error)
        {
            return DhtRpcResponse.Create(Identity, Config.HomeURI, req != null ? req.Op : string.Empty,
                req != null ? req.Nonce : string.Empty, false, error, new List<DhtPeerWire>(), null);
        }

        private void RegisterConsole()
        {
            if (MainConsole.Instance == null)
                return;
            MainConsole.Instance.Commands.AddCommand("UUID-DHT", false, "dht status",
                "dht status", "Show DHT node id, home, peers, and store counts", HandleDht);
            MainConsole.Instance.Commands.AddCommand("UUID-DHT", false, "dht peers",
                "dht peers", "List DHT contacts", HandleDht);
            MainConsole.Instance.Commands.AddCommand("UUID-DHT", false, "dht lookup",
                "dht lookup <uuid>", "Two-level FIND for a UUID", HandleDht);
            MainConsole.Instance.Commands.AddCommand("UUID-DHT", false, "dht publish",
                "dht publish <uuid>", "STORE a UUID we own", HandleDht);
            MainConsole.Instance.Commands.AddCommand("UUID-DHT", false, "dht unpublish",
                "dht unpublish <uuid>", "Tombstone a published UUID", HandleDht);
            MainConsole.Instance.Commands.AddCommand("UUID-DHT", false, "dht backfill",
                "dht backfill", "Re-list local users/groups and STORE any not yet published", HandleDht);
        }

        private void HandleDht(string module, string[] cmd)
        {
            if (MainConsole.Instance == null)
                return;
            if (cmd == null || cmd.Length < 2)
            {
                MainConsole.Instance.Output("dht status|peers|lookup <uuid>|publish <uuid>|unpublish <uuid>|backfill");
                return;
            }

            string sub = cmd[1];
            if (sub == "backfill")
            {
                int n = Publisher != null ? Publisher.Backfill() : 0;
                MainConsole.Instance.Output("[UUID-DHT] backfill published {0} new", n);
                return;
            }
            if (sub == "status")
            {
                MainConsole.Instance.Output(
                    "[UUID-DHT] node={0} home={1} contacts={2} buckets={3} liveUuids={4} published={5} lastJoin={6}",
                    Identity.NodeId.ToHex(), Config.HomeURI, Table.SnapshotContacts().Count,
                    Table.FilledBucketCount(), Store.CountLiveUuidByOwner(Identity.NodeId),
                    Publisher != null ? Publisher.PublishedCount : 0,
                    LastJoinUtc == default ? "never" : LastJoinUtc.ToString("o"));
                return;
            }
            if (sub == "peers")
            {
                List<DhtPeer> peers = Table.SnapshotContacts();
                MainConsole.Instance.Output("[UUID-DHT] {0} contacts", peers.Count);
                for (int i = 0; i < peers.Count; i++)
                    MainConsole.Instance.Output("  {0} {1}", peers[i].NodeId.ToHex(), peers[i].HomeURI);
                return;
            }

            if (cmd.Length < 3 || !UUID.TryParse(cmd[2], out UUID uuid) || uuid.IsZero())
            {
                MainConsole.Instance.Output("usage: dht {0} <uuid>", sub);
                return;
            }

            if (sub == "lookup")
            {
                UuidDhtHome home = new UuidDhtClient(this).GetHomeByUuid(uuid);
                if (home == null)
                    MainConsole.Instance.Output("[UUID-DHT] miss {0}", uuid);
                else
                    MainConsole.Instance.Output("[UUID-DHT] {0} -> {1}", home.UserID, home.HomeURI);
                return;
            }
            if (sub == "publish")
            {
                bool ok = Publisher != null && Publisher.PublishUuid(uuid);
                MainConsole.Instance.Output("[UUID-DHT] publish {0} {1}", uuid, ok ? "ok" : "failed");
                return;
            }
            if (sub == "unpublish")
            {
                bool ok = Publisher != null && Publisher.UnpublishUuid(uuid);
                MainConsole.Instance.Output("[UUID-DHT] unpublish {0} {1}", uuid, ok ? "ok" : "failed");
                return;
            }

            MainConsole.Instance.Output("dht status|peers|lookup <uuid>|publish <uuid>|unpublish <uuid>|backfill");
        }

        private sealed class LocalAwareValidator : IDhtStoreValidator
        {
            private readonly UuidDhtNode m_node;
            private readonly IDhtStoreValidator m_inner;

            public LocalAwareValidator(UuidDhtNode node, IDhtStoreValidator inner)
            {
                m_node = node;
                m_inner = inner;
            }

            public bool ProofMatches(string homeURI, DhtKey expectedNodeId)
            {
                if (m_inner is DhtHttpClient
                    && expectedNodeId.Equals(m_node.Identity.NodeId)
                    && m_node.IsOwnHome(homeURI))
                    return true;
                return m_inner != null && m_inner.ProofMatches(homeURI, expectedNodeId);
            }

            public bool OwnsUuid(string homeURI, UUID uuid, DhtKey expectedNodeId)
            {
                if (m_inner is DhtHttpClient
                    && expectedNodeId.Equals(m_node.Identity.NodeId)
                    && m_node.IsOwnHome(homeURI))
                    return m_node.OwnsLocal(uuid);
                return m_inner != null && m_inner.OwnsUuid(homeURI, uuid, expectedNodeId);
            }
        }

        private sealed class TokenBucket
        {
            private readonly object m_lock = new object();
            private double m_tokens = 40;
            private long m_last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            public bool TryTake()
            {
                lock (m_lock)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    m_tokens = Math.Min(40, m_tokens + (now - m_last) * 20.0 / 1000.0);
                    m_last = now;
                    if (m_tokens < 1)
                        return false;
                    m_tokens -= 1;
                    return true;
                }
            }
        }
    }
}
