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
using System.IO;
using System.Threading;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Services.Interfaces;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    /// <summary>
    /// Two in-process nodes on DhtFakeTransport: join, publish, lookup, move, squat,
    /// tombstone, timeout, loopback reject. No Groups/GridUser HTTP stack.
    /// </summary>
    [TestFixture]
    public class DhtIntegrationTests
    {
        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            TestBin.UseOpenSimBin();
            m_Dir = Path.Combine(Path.GetTempPath(), "uuiddht-int-" + UUID.Random());
            Directory.CreateDirectory(m_Dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(m_Dir, true); } catch { }
        }

        [Test]
        public void PublishOnALookupFromB()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.10:8002/", t, Array.Empty<string>());
            Assert.That(a.Node.Join(), Is.True);
            using NodePack b = Make("b", "http://192.0.2.11:8002/", t, new[] { a.Home });
            Assert.That(b.Node.Join(), Is.True);
            CrossInsert(a, b);

            UUID u = UUID.Random();
            Own(a, u);
            Assert.That(a.Node.Publisher.PublishUuid(u), Is.True);

            UuidDhtClient client = new UuidDhtClient(b.Node);
            UuidDhtHome home = client.GetHomeByUuid(u);
            Assert.That(home, Is.Not.Null);
            Assert.That(home.UserID, Is.EqualTo(u));
            Assert.That(home.HomeURI, Is.EqualTo(a.Home));
            Assert.That(client.GetGroupHomeByUuid(u).HomeURI, Is.EqualTo(a.Home));
        }

        [Test]
        public void MoveKeepsUuidOnSameNodeIdNewHome()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.20:8002/", t, Array.Empty<string>());
            Assert.That(a.Node.Join(), Is.True);
            using NodePack b = Make("b", "http://192.0.2.21:8002/", t, new[] { a.Home });
            Assert.That(b.Node.Join(), Is.True);
            CrossInsert(a, b);

            UUID u = UUID.Random();
            Own(a, u);
            Assert.That(a.Node.Publisher.PublishUuid(u), Is.True);
            Assert.That(new UuidDhtClient(b.Node).GetHomeByUuid(u).HomeURI, Is.EqualTo(a.Home));

            string home2 = "http://192.0.2.22:8002/";
            UuidDhtNode a2 = UuidDhtNode.CreateForTests(Cfg("a2", home2, new[] { b.Home }), a.Id, a.Store, t, t);
            t.Add(home2, a2);
            Assert.That(a2.Join(), Is.True);

            Assert.That(a.Store.TryGet(a.Id.NodeId, out DhtRecord node), Is.True);
            Assert.That(node.HomeURI, Is.EqualTo(home2));
            Assert.That(a.Store.TryGet(DhtKey.ForUuid(u), out DhtRecord uuid), Is.True);
            Assert.That(uuid.NodeId, Is.EqualTo(a.Id.NodeId.ToHex()));
            Assert.That(uuid.Tombstone, Is.False);

            UuidDhtHome home = new UuidDhtClient(b.Node).GetHomeByUuid(u);
            Assert.That(home, Is.Not.Null);
            Assert.That(home.HomeURI, Is.EqualTo(home2));
        }

        [Test]
        public void LiveSquatOfHomeUriIsRejected()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.30:8002/", t, Array.Empty<string>());
            Assert.That(a.Node.Join(), Is.True);
            using NodePack b = Make("b", "http://192.0.2.31:8002/", t, new[] { a.Home });
            Assert.That(b.Node.Join(), Is.True);
            CrossInsert(a, b);

            using NodePack cSame = Make("c-same", a.Home, t, new[] { a.Home }, add: false);
            Assert.That(cSame.Node.Join(), Is.False, "cannot prove HomeURI while A still answers it");

            using NodePack c = Make("c", "http://192.0.2.32:8002/", t, new[] { a.Home });
            Assert.That(c.Node.Join(), Is.True);
            CrossInsert(a, c);

            DhtRecord squat = DhtRecord.CreateLoc(a.Home, c.Id.NodeId, 1, false);
            squat.SignWith(c.Id);
            DhtRecord cNode = DhtRecord.CreateNode(c.Id.NodeId, c.Home, c.Id.PublicKey, c.Id.NodeSeq);
            cNode.SignWith(c.Id);
            c.Node.StoreFanout(squat, cNode, "STORE", CancellationToken.None);

            Assert.That(a.Store.TryGet(DhtKey.ForLoc(a.Home), out DhtRecord loc), Is.True);
            Assert.That(loc.NodeId, Is.EqualTo(a.Id.NodeId.ToHex()));
            Assert.That(loc.Tombstone, Is.False);
        }

        [Test]
        public void TombstoneDoesNotResurrectWithOldSeq()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.40:8002/", t, Array.Empty<string>());
            Assert.That(a.Node.Join(), Is.True);
            using NodePack b = Make("b", "http://192.0.2.41:8002/", t, new[] { a.Home });
            Assert.That(b.Node.Join(), Is.True);
            CrossInsert(a, b);

            UUID u = UUID.Random();
            Own(a, u);
            Assert.That(a.Node.Publisher.PublishUuid(u), Is.True);
            Assert.That(new UuidDhtClient(b.Node).GetHomeByUuid(u), Is.Not.Null);

            Assert.That(a.Node.Publisher.UnpublishUuid(u), Is.True);
            Assert.That(a.Store.TryGet(DhtKey.ForUuid(u), out DhtRecord tomb), Is.True);
            Assert.That(tomb.Tombstone, Is.True);
            long tombSeq = tomb.Seq;

            DhtRecord old = DhtRecord.CreateUuid(u, a.Id.NodeId, 1, false);
            old.SignWith(a.Id);
            DhtRecord nodeRec = a.Node.OwnNodeRecord();
            Assert.That(a.Node.StoreFanout(old, nodeRec, "STORE", CancellationToken.None), Is.False);
            Assert.That(a.Store.TryGet(DhtKey.ForUuid(u), out DhtRecord still), Is.True);
            Assert.That(still.Tombstone, Is.True);
            Assert.That(still.Seq, Is.EqualTo(tombSeq));

            Assert.That(new UuidDhtClient(b.Node).GetHomeByUuid(u), Is.Null);
        }

        [Test]
        public void LookupTimeoutReturnsNull()
        {
            SlowTransport t = new SlowTransport();
            using NodePack a = Make("a", "http://192.0.2.50:8002/", t, Array.Empty<string>(), capMs: 150);
            Assert.That(a.Node.Join(), Is.True);
            using NodePack b = Make("b", "http://192.0.2.51:8002/", t, new[] { a.Home }, capMs: 150);
            Assert.That(b.Node.Join(), Is.True);
            CrossInsert(a, b);

            t.Stall = true;
            UuidDhtHome home = new UuidDhtClient(b.Node).GetHomeByUuid(UUID.Random());
            Assert.That(home, Is.Null);
        }

        [Test]
        public void AllowPrivateHomeUriFalseRejectsLoopback()
        {
            Assert.That(DhtLocator.TryNormalize("http://127.0.0.1:8002/", false, out _), Is.False);

            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack loop = Make("loop", "http://127.0.0.1:8002/", t, Array.Empty<string>());
            Assert.That(loop.Node.Join(), Is.False);

            using NodePack a = Make("a", "http://192.0.2.60:8002/", t, Array.Empty<string>());
            Assert.That(a.Node.Join(), Is.True);
            DhtRecord loopNode = DhtRecord.CreateNode(a.Id.NodeId, "http://127.0.0.1:8002/", a.Id.PublicKey, a.Id.NodeSeq + 1);
            loopNode.SignWith(a.Id);
            Assert.That(a.Store.Put(loopNode, null, t).Ok, Is.False);
        }

        private static void CrossInsert(NodePack x, NodePack y)
        {
            DhtPeer px = new DhtPeer { NodeId = x.Id.NodeId, HomeURI = x.Home, LastSeen = DateTime.UtcNow };
            DhtPeer py = new DhtPeer { NodeId = y.Id.NodeId, HomeURI = y.Home, LastSeen = DateTime.UtcNow };
            x.Node.Table.TryInsert(py, out DhtPeer _);
            y.Node.Table.TryInsert(px, out DhtPeer _);
        }

        private static void Own(NodePack n, UUID u)
        {
            FakeUsers users = new FakeUsers();
            users.Ids.Add(u);
            n.Node.SetUserAccountService(users);
        }

        private NodePack Make(string name, string home, IDhtTransport transport, string[] bootstrap,
            bool add = true, int capMs = 3000)
        {
            IDhtStoreValidator validator = transport as IDhtStoreValidator;
            if (validator == null)
                throw new ArgumentException("transport must validate the store", nameof(transport));

            DhtIdentity id = DhtIdentity.LoadOrCreate(Path.Combine(m_Dir, name + ".json"), null);
            SqliteDhtStore store = new SqliteDhtStore(Path.Combine(m_Dir, name + ".db"));
            UuidDhtConfig cfg = Cfg(name, home, bootstrap, capMs);
            UuidDhtNode node = UuidDhtNode.CreateForTests(cfg, id, store, transport, validator);
            if (add && !string.IsNullOrEmpty(home) && transport is DhtFakeTransport fake)
                fake.Add(home, node);
            else if (add && !string.IsNullOrEmpty(home) && transport is SlowTransport slow)
                slow.Add(home, node);
            return new NodePack { Node = node, Id = id, Home = home, Store = store };
        }

        private UuidDhtConfig Cfg(string name, string home, string[] bootstrap, int capMs = 3000)
        {
            return new UuidDhtConfig
            {
                Enabled = true,
                HomeURI = home,
                IdentityPath = Path.Combine(m_Dir, name + ".json"),
                StorePath = Path.Combine(m_Dir, name + ".db"),
                K = 3,
                SiblingSize = 3,
                MaxRpcBytes = 32768,
                RpcTimeoutMs = 300,
                LookupTimeoutMs = 1500,
                ClientLookupCapMs = capMs,
                CallbackTimeoutMs = 500,
                Alpha = 3,
                DisjointPaths = 3,
                AllowPrivateHomeURI = false,
                MaxPublishUuids = 100000,
                Bootstrap = bootstrap ?? Array.Empty<string>()
            };
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

        private sealed class FakeUsers : IUserAccountService
        {
            public HashSet<UUID> Ids = new HashSet<UUID>();

            public UserAccount GetUserAccount(UUID scopeID, UUID userID)
            {
                return Ids.Contains(userID) ? new UserAccount(userID) : null;
            }

            public UserAccount GetUserAccount(UUID scopeID, string FirstName, string LastName)
            {
                return null;
            }

            public UserAccount GetUserAccount(UUID scopeID, string Email)
            {
                return null;
            }

            public List<UserAccount> GetUserAccounts(UUID scopeID, string query)
            {
                return new List<UserAccount>();
            }

            public List<UserAccount> GetUserAccountsWhere(UUID scopeID, string where)
            {
                return new List<UserAccount>();
            }

            public List<UserAccount> GetUserAccounts(UUID scopeID, List<string> IDs)
            {
                return new List<UserAccount>();
            }

            public bool StoreUserAccount(UserAccount data)
            {
                return false;
            }

            public void InvalidateCache(UUID userID)
            {
            }
        }

        private sealed class SlowTransport : IDhtTransport, IDhtStoreValidator
        {
            private readonly DhtFakeTransport m_inner = new DhtFakeTransport();

            public bool Stall;

            public bool AllowAutoRedirect
            {
                get { return false; }
            }

            public void Add(string homeURI, UuidDhtNode node)
            {
                m_inner.Add(homeURI, node);
            }

            public DhtNodeDocument GetNode(string homeURI, CancellationToken ct)
            {
                WaitIfStalled(ct);
                return m_inner.GetNode(homeURI, ct);
            }

            public DhtOwnsDocument GetOwns(string homeURI, UUID uuid, CancellationToken ct)
            {
                WaitIfStalled(ct);
                return m_inner.GetOwns(homeURI, uuid, ct);
            }

            public DhtRpcResponse Rpc(string homeURI, DhtRpcRequest request, CancellationToken ct)
            {
                WaitIfStalled(ct);
                return m_inner.Rpc(homeURI, request, ct);
            }

            public bool ProofMatches(string homeURI, DhtKey expectedNodeId)
            {
                return m_inner.ProofMatches(homeURI, expectedNodeId);
            }

            public bool OwnsUuid(string homeURI, UUID uuid, DhtKey expectedNodeId)
            {
                return m_inner.OwnsUuid(homeURI, uuid, expectedNodeId);
            }

            public void Dispose()
            {
                m_inner.Dispose();
            }

            private void WaitIfStalled(CancellationToken ct)
            {
                if (!Stall)
                    return;
                ct.WaitHandle.WaitOne();
                ct.ThrowIfCancellationRequested();
            }
        }
    }
}
