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
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Services.Interfaces;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class DhtJoinTests
    {
        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            TestBin.UseOpenSimBin();
            m_Dir = Path.Combine(Path.GetTempPath(), "uuiddht-join-" + UUID.Random());
            Directory.CreateDirectory(m_Dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(m_Dir, true); } catch { }
        }

        [Test]
        public void IsolatedJoinStoresNodeAndLoc()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.10:8002/", t, Array.Empty<string>());

            Assert.That(a.Node.Join(), Is.True);
            Assert.That(a.Store.TryGet(a.Id.NodeId, out DhtRecord node), Is.True);
            Assert.That(node.HomeURI, Is.EqualTo(a.Home));
            Assert.That(node.Tombstone, Is.False);
            Assert.That(a.Store.TryGet(DhtKey.ForLoc(a.Home), out DhtRecord loc), Is.True);
            Assert.That(loc.NodeId, Is.EqualTo(a.Id.NodeId.ToHex()));
            Assert.That(loc.Tombstone, Is.False);

            string[] seeds = DhtSeedFile.Read(a.SeedsPath);
            Assert.That(seeds.Length, Is.GreaterThanOrEqualTo(1));
            Assert.That(seeds[0], Is.EqualTo(a.Home));
        }

        [Test]
        public void SecondNodeJoinsViaSeed()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.20:8002/", t, Array.Empty<string>());
            Assert.That(a.Node.Join(), Is.True);

            using NodePack b = Make("b", "http://192.0.2.21:8002/", t, new[] { a.Home });
            Assert.That(b.Node.Join(), Is.True);

            Assert.That(a.Store.TryGet(b.Id.NodeId, out DhtRecord bNode), Is.True, "seed stores joiner node");
            Assert.That(bNode.HomeURI, Is.EqualTo(b.Home));
            Assert.That(a.Store.TryGet(DhtKey.ForLoc(b.Home), out DhtRecord bLoc), Is.True);
            Assert.That(bLoc.NodeId, Is.EqualTo(b.Id.NodeId.ToHex()));

            string[] bSeeds = DhtSeedFile.Read(b.SeedsPath);
            Assert.That(bSeeds.Length, Is.GreaterThanOrEqualTo(2));
            Assert.That(bSeeds[0], Is.EqualTo(b.Home));
            Assert.That(bSeeds, Does.Contain(a.Home));
        }

        [Test]
        public void HttpJoinProvesHomeInProcessWithoutListening()
        {
            using DhtHttpClient http = new DhtHttpClient(false, 300);
            DhtIdentity id = DhtIdentity.LoadOrCreate(Path.Combine(m_Dir, "http.json"), null);
            SqliteDhtStore store = new SqliteDhtStore(Path.Combine(m_Dir, "http.db"));
            UuidDhtConfig cfg = Cfg("http", "http://192.0.2.99:8002/", Array.Empty<string>());
            UuidDhtNode node = UuidDhtNode.CreateForTests(cfg, id, store, http, http);

            Stopwatch sw = Stopwatch.StartNew();
            bool joined = node.Join();
            sw.Stop();

            Assert.That(joined, Is.True, "self-proof must not HTTP-GET HomeURI");
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(250),
                "HTTP self-GET would wait ~RpcTimeoutMs; in-process proof must be faster");
            Assert.That(store.TryGet(id.NodeId, out DhtRecord rec), Is.True);
            Assert.That(rec.HomeURI, Is.EqualTo(cfg.HomeURI));
            store.Dispose();
        }

        [Test]
        public void JoinRefusesUnusableOrUnprovenHome()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack loop = Make("loop", "http://127.0.0.1:8002/", t, Array.Empty<string>());
            Assert.That(loop.Node.Join(), Is.False);

            using NodePack empty = Make("empty", "", t, Array.Empty<string>());
            Assert.That(empty.Node.Join(), Is.False);

            using NodePack a = Make("a", "http://192.0.2.30:8002/", t, Array.Empty<string>());
            t.Remove(a.Home);
            Assert.That(a.Node.Join(), Is.False);
        }

        [Test]
        public void LiveOccupantRejectsSquatLoc()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.40:8002/", t, Array.Empty<string>());
            Assert.That(a.Node.Join(), Is.True);
            using NodePack c = Make("c", "http://192.0.2.41:8002/", t, new[] { a.Home });
            Assert.That(c.Node.Join(), Is.True);

            DhtRecord squat = DhtRecord.CreateLoc(a.Home, c.Id.NodeId, 1, false);
            squat.SignWith(c.Id);
            DhtRecord cNode = DhtRecord.CreateNode(c.Id.NodeId, c.Home, c.Id.PublicKey, c.Id.NodeSeq);
            cNode.SignWith(c.Id);
            c.Node.StoreFanout(squat, cNode, "STORE", CancellationToken.None);

            Assert.That(a.Store.TryGet(DhtKey.ForLoc(a.Home), out DhtRecord loc), Is.True);
            Assert.That(loc.NodeId, Is.EqualTo(a.Id.NodeId.ToHex()));
        }

        [Test]
        public void DeadOccupantAllowsTakeover()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack seed = Make("seed", "http://192.0.2.49:8002/", t, Array.Empty<string>());
            Assert.That(seed.Node.Join(), Is.True);
            using NodePack a = Make("a", "http://192.0.2.50:8002/", t, new[] { seed.Home });
            Assert.That(a.Node.Join(), Is.True);

            t.Remove(a.Home);
            using NodePack c = Make("c", a.Home, t, new[] { seed.Home });
            Assert.That(c.Node.Join(), Is.True);
            Assert.That(seed.Store.TryGet(DhtKey.ForLoc(a.Home), out DhtRecord loc), Is.True);
            Assert.That(loc.NodeId, Is.EqualTo(c.Id.NodeId.ToHex()));
        }

        [Test]
        public void MoveUpdatesNodeAndTombsOldLoc()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.60:8002/", t, Array.Empty<string>());
            Assert.That(a.Node.Join(), Is.True);

            string home2 = "http://192.0.2.61:8002/";
            UuidDhtConfig cfg2 = Cfg("a2", home2, Array.Empty<string>());
            UuidDhtNode a2 = UuidDhtNode.CreateForTests(cfg2, a.Id, a.Store, t, t);
            t.Add(home2, a2);
            Assert.That(a2.Join(), Is.True);

            Assert.That(a.Store.TryGet(a.Id.NodeId, out DhtRecord node), Is.True);
            Assert.That(node.HomeURI, Is.EqualTo(home2));
            Assert.That(node.Seq, Is.GreaterThan(1));
            Assert.That(a.Store.TryGet(DhtKey.ForLoc(home2), out DhtRecord loc2), Is.True);
            Assert.That(loc2.NodeId, Is.EqualTo(a.Id.NodeId.ToHex()));
            Assert.That(loc2.Tombstone, Is.False);
            Assert.That(a.Store.TryGet(DhtKey.ForLoc(a.Home), out DhtRecord oldLoc), Is.True);
            Assert.That(oldLoc.Tombstone, Is.True);
        }

        [Test]
        public void OwnsCallbackUsesUserAccount()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.70:8002/", t, Array.Empty<string>());
            Assert.That(a.Node.Join(), Is.True);

            UUID u = UUID.Random();
            Assert.That(a.Node.OwnsLocal(u), Is.False);
            Assert.That(t.OwnsUuid(a.Home, u, a.Id.NodeId), Is.False);

            FakeUsers users = new FakeUsers();
            users.Ids.Add(u);
            a.Node.SetUserAccountService(users);
            Assert.That(a.Node.OwnsLocal(u), Is.True);
            Assert.That(a.Node.CreateOwnsDocument(u).Has, Is.True);
            Assert.That(t.OwnsUuid(a.Home, u, a.Id.NodeId), Is.True);
        }

        private NodePack Make(string name, string home, DhtFakeTransport t, string[] bootstrap)
        {
            DhtIdentity id = DhtIdentity.LoadOrCreate(Path.Combine(m_Dir, name + ".json"), null);
            SqliteDhtStore store = new SqliteDhtStore(Path.Combine(m_Dir, name + ".db"));
            UuidDhtConfig cfg = Cfg(name, home, bootstrap);
            UuidDhtNode node = UuidDhtNode.CreateForTests(cfg, id, store, t, t);
            if (!string.IsNullOrEmpty(home))
                t.Add(home, node);
            return new NodePack { Node = node, Id = id, Home = home, Store = store, SeedsPath = cfg.SeedsPath };
        }

        private UuidDhtConfig Cfg(string name, string home, string[] bootstrap)
        {
            return new UuidDhtConfig
            {
                Enabled = true,
                HomeURI = home,
                IdentityPath = Path.Combine(m_Dir, name + ".json"),
                StorePath = Path.Combine(m_Dir, name + ".db"),
                SeedsPath = Path.Combine(m_Dir, name + "-seeds"),
                K = 3,
                SiblingSize = 3,
                MaxRpcBytes = 32768,
                RpcTimeoutMs = 300,
                LookupTimeoutMs = 1500,
                CallbackTimeoutMs = 500,
                Alpha = 3,
                DisjointPaths = 3,
                AllowPrivateHomeURI = false,
                Bootstrap = bootstrap ?? Array.Empty<string>()
            };
        }

        private sealed class NodePack : IDisposable
        {
            public UuidDhtNode Node;
            public DhtIdentity Id;
            public string Home;
            public IDhtStore Store;
            public string SeedsPath;

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
    }
}
