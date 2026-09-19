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
using System.IO;
using NUnit.Framework;
using OpenMetaverse;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class UuidDhtClientTests
    {
        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            TestBin.UseOpenSimBin();
            m_Dir = Path.Combine(Path.GetTempPath(), "uuiddht-client-" + UUID.Random());
            Directory.CreateDirectory(m_Dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(m_Dir, true); } catch { }
        }

        [Test]
        public void TwoLevelFindReturnsHome()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack replica = Make("replica", "http://192.0.2.10:8002/", t);
            using NodePack lookup = Make("lookup", "http://192.0.2.12:8002/", t);

            UUID u = UUID.Random();
            Publish(replica, u);
            Assert.That(lookup.Node.Table.TryInsert(Peer(replica), out DhtPeer _), Is.EqualTo(DhtInsertStatus.Added));

            UuidDhtClient client = new UuidDhtClient(lookup.Node);
            UuidDhtHome home = client.GetHomeByUuid(u);
            Assert.That(home, Is.Not.Null);
            Assert.That(home.UserID, Is.EqualTo(u));
            Assert.That(home.HomeURI, Is.EqualTo(replica.Home));
        }

        [Test]
        public void GroupLookupSharesUuidKeyspace()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack owner = Make("owner", "http://192.0.2.21:8002/", t);
            UUID g = UUID.Random();
            Publish(owner, g);

            UuidDhtClient client = new UuidDhtClient(owner.Node);
            UuidDhtHome user = client.GetHomeByUuid(g);
            UuidDhtHome group = client.GetGroupHomeByUuid(g);
            Assert.That(user, Is.Not.Null);
            Assert.That(group, Is.Not.Null);
            Assert.That(group.HomeURI, Is.EqualTo(user.HomeURI));
            Assert.That(group.UserID, Is.EqualTo(g));
        }

        [Test]
        public void LocalStoreHitDoesNotNeedPeers()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack n = Make("solo", "http://192.0.2.30:8002/", t);
            UUID u = UUID.Random();
            Publish(n, u);

            UuidDhtClient client = new UuidDhtClient(n.Node);
            int rpcs = t.RpcCount;
            UuidDhtHome home = client.GetHomeByUuid(u);
            Assert.That(home, Is.Not.Null);
            Assert.That(home.HomeURI, Is.EqualTo(n.Home));
            Assert.That(t.RpcCount, Is.EqualTo(rpcs));
        }

        [Test]
        public void PositiveCacheSkipsSecondFind()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack replica = Make("replica", "http://192.0.2.40:8002/", t);
            using NodePack lookup = Make("lookup", "http://192.0.2.42:8002/", t);

            UUID u = UUID.Random();
            Publish(replica, u);
            Assert.That(lookup.Node.Table.TryInsert(Peer(replica), out DhtPeer _), Is.EqualTo(DhtInsertStatus.Added));

            UuidDhtClient client = new UuidDhtClient(lookup.Node);
            Assert.That(client.GetHomeByUuid(u), Is.Not.Null);
            int rpcs = t.RpcCount;
            int gets = t.GetNodeCount;

            DhtRecord tomb = DhtRecord.CreateUuid(u, replica.Id.NodeId, 2, true);
            tomb.SignWith(replica.Id);
            Assert.That(replica.Store.Put(tomb, null, new AlwaysVal()).Ok, Is.True);

            UuidDhtHome home = client.GetHomeByUuid(u);
            Assert.That(home, Is.Not.Null);
            Assert.That(home.HomeURI, Is.EqualTo(replica.Home));
            Assert.That(t.RpcCount, Is.EqualTo(rpcs));
            Assert.That(t.GetNodeCount, Is.EqualTo(gets));
        }

        [Test]
        public void UnknownUuidIsMiss()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack n = Make("solo", "http://192.0.2.50:8002/", t);
            UuidDhtClient client = new UuidDhtClient(n.Node);
            Assert.That(client.GetHomeByUuid(UUID.Random()), Is.Null);
            Assert.That(client.GetGroupHomeByUuid(UUID.Random()), Is.Null);
        }

        [Test]
        public void TombstoneIsMiss()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack n = Make("solo", "http://192.0.2.60:8002/", t);
            UUID u = UUID.Random();
            DhtRecord nodeRec = DhtRecord.CreateNode(n.Id.NodeId, n.Home, n.Id.PublicKey, 1);
            nodeRec.SignWith(n.Id);
            Assert.That(n.Store.Put(nodeRec, null, new AlwaysVal()).Ok, Is.True);
            DhtRecord tomb = DhtRecord.CreateUuid(u, n.Id.NodeId, 1, true);
            tomb.SignWith(n.Id);
            Assert.That(n.Store.Put(tomb, null, new AlwaysVal()).Ok, Is.True);

            UuidDhtClient client = new UuidDhtClient(n.Node);
            Assert.That(client.GetHomeByUuid(u), Is.Null);
        }

        [Test]
        public void ZeroUuidAndNullNodeAreMiss()
        {
            Assert.That(new UuidDhtClient((UuidDhtNode)null).GetHomeByUuid(UUID.Random()), Is.Null);
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack n = Make("solo", "http://192.0.2.70:8002/", t);
            UuidDhtClient client = new UuidDhtClient(n.Node);
            Assert.That(client.GetHomeByUuid(UUID.Zero), Is.Null);
            Assert.That(client.GetGroupHomeByUuid(UUID.Zero), Is.Null);
        }

        [Test]
        public void TryBuildHomeRejectsBadSigAndLoopback()
        {
            DhtIdentity id = DhtIdentity.LoadOrCreate(Path.Combine(m_Dir, "id.json"), null);
            UUID u = UUID.Random();
            DhtRecord node = DhtRecord.CreateNode(id.NodeId, "http://192.0.2.80:8002/", id.PublicKey, 1);
            node.SignWith(id);
            DhtRecord uuid = DhtRecord.CreateUuid(u, id.NodeId, 1, false);
            uuid.SignWith(id);

            Assert.That(UuidDhtClient.TryBuildHome(uuid, node, u, false), Is.Not.Null);

            char last = uuid.Sig[uuid.Sig.Length - 1];
            uuid.Sig = uuid.Sig.Substring(0, uuid.Sig.Length - 1) + (last == '0' ? '1' : '0');
            Assert.That(UuidDhtClient.TryBuildHome(uuid, node, u, false), Is.Null);

            DhtRecord loop = DhtRecord.CreateNode(id.NodeId, "http://127.0.0.1:8002/", id.PublicKey, 1);
            loop.SignWith(id);
            DhtRecord uuid2 = DhtRecord.CreateUuid(u, id.NodeId, 1, false);
            uuid2.SignWith(id);
            Assert.That(UuidDhtClient.TryBuildHome(uuid2, loop, u, true), Is.Null);
        }

        private void Publish(NodePack owner, UUID u)
        {
            DhtRecord nodeRec = DhtRecord.CreateNode(owner.Id.NodeId, owner.Home, owner.Id.PublicKey, 1);
            nodeRec.SignWith(owner.Id);
            Assert.That(owner.Store.Put(nodeRec, null, new AlwaysVal()).Ok, Is.True);
            DhtRecord uuidRec = DhtRecord.CreateUuid(u, owner.Id.NodeId, 1, false);
            uuidRec.SignWith(owner.Id);
            Assert.That(owner.Store.Put(uuidRec, nodeRec, new AlwaysVal()).Ok, Is.True);
        }

        private NodePack Make(string name, string home, DhtFakeTransport t)
        {
            DhtIdentity id = DhtIdentity.LoadOrCreate(Path.Combine(m_Dir, name + ".json"), null);
            SqliteDhtStore store = new SqliteDhtStore(Path.Combine(m_Dir, name + ".db"));
            UuidDhtConfig cfg = new UuidDhtConfig
            {
                Enabled = true,
                HomeURI = home,
                IdentityPath = Path.Combine(m_Dir, name + ".json"),
                StorePath = Path.Combine(m_Dir, name + ".db"),
                K = 20,
                SiblingSize = 20,
                MaxRpcBytes = 32768,
                RpcTimeoutMs = 300,
                LookupTimeoutMs = 1500,
                ClientLookupCapMs = 3000,
                PositiveCacheSec = 600,
                NegativeCacheSec = 60,
                Alpha = 3,
                DisjointPaths = 3,
                AllowPrivateHomeURI = false,
                Bootstrap = Array.Empty<string>()
            };
            UuidDhtNode node = UuidDhtNode.CreateForTests(cfg, id, store, t, new AlwaysVal());
            t.Add(home, node);
            return new NodePack { Node = node, Id = id, Home = home, Store = store };
        }

        private static DhtPeer Peer(NodePack p)
        {
            return new DhtPeer { NodeId = p.Id.NodeId, HomeURI = p.Home, LastSeen = DateTime.UtcNow };
        }

        private sealed class NodePack : IDisposable
        {
            public UuidDhtNode Node;
            public DhtIdentity Id;
            public string Home;
            public IDhtStore Store;

            public void Dispose()
            {
                Store?.Dispose();
            }
        }

        private sealed class AlwaysVal : IDhtStoreValidator
        {
            public bool ProofMatches(string homeURI, DhtKey expectedNodeId)
            {
                return true;
            }

            public bool OwnsUuid(string homeURI, UUID uuid, DhtKey expectedNodeId)
            {
                return true;
            }
        }
    }
}
