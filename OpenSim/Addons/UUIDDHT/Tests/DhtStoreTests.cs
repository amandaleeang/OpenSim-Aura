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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using OpenMetaverse;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class DhtStoreTests
    {
        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            TestBin.UseOpenSimBin();
            m_Dir = Path.Combine(Path.GetTempPath(), "uuiddht-store-" + UUID.Random());
            Directory.CreateDirectory(m_Dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(m_Dir, true); } catch { }
        }

        [Test]
        public void NativeCandidatesIncludeOpenSimLib64()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Assert.Ignore("linux native layout");
            string[] c = SqliteDhtStore.NativeCandidates("/opensim/bin").ToArray();
            Assert.That(c, Does.Contain("/opensim/bin/libe_sqlite3.so"));
            Assert.That(c, Does.Contain("/opensim/bin/lib64/libe_sqlite3.so"));
        }

        [Test]
        public void EnsureSqliteNativeLoadsWithoutOpenSimDataSqlite()
        {
            Assert.That(SqliteDhtStore.EnsureSqliteNative(), Is.True);
        }

        [Test]
        public void MalloryCannotOverwriteAliceUuidAtSeq1()
        {
            using SqliteDhtStore store = OpenStore();
            DhtIdentity alice = Ident("alice");
            DhtIdentity mallory = Ident("mallory");
            FakeVal v = new FakeVal();
            UUID u = UUID.Random();

            DhtRecord aliceNode = SignedNode(alice);
            Assert.That(store.Put(aliceNode, null, v).Ok, Is.True);

            DhtRecord aliceUuid = DhtRecord.CreateUuid(u, alice.NodeId, 1, false);
            aliceUuid.SignWith(alice);
            Assert.That(store.Put(aliceUuid, aliceNode, v).Ok, Is.True);

            DhtRecord malloryNode = SignedNode(mallory);
            store.Put(malloryNode, null, v);
            DhtRecord steal = DhtRecord.CreateUuid(u, mallory.NodeId, 1, false);
            steal.SignWith(mallory);
            DhtStorePutResult r = store.Put(steal, malloryNode, v);
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Is.EqualTo("taken"));
        }

        [Test]
        public void LocLiveOccupantBlocksUntilProofFails()
        {
            using SqliteDhtStore store = OpenStore();
            DhtIdentity a = Ident("a");
            DhtIdentity b = Ident("b");
            string home = "http://192.0.2.1:8002/";
            FakeVal live = new FakeVal { Proof = true };
            DhtRecord locA = DhtRecord.CreateLoc(home, a.NodeId, 1, false);
            locA.SignWith(a);
            Assert.That(store.Put(locA, SignedNode(a), live).Ok, Is.True);

            DhtRecord locB = DhtRecord.CreateLoc(home, b.NodeId, 1, false);
            locB.SignWith(b);
            Assert.That(store.Put(locB, SignedNode(b), live).Error, Is.EqualTo("home_taken"));

            FakeVal dead = new FakeVal { Proof = false };
            Assert.That(store.Put(locB, SignedNode(b), dead).Ok, Is.True);
        }

        [Test]
        public void ReplicaPeerNodeDoesNotBindIdentity()
        {
            string storePath = Path.Combine(m_Dir, "store.db");
            string idPath = Path.Combine(m_Dir, "identity.json");
            DhtIdentity peer = Ident("peer");
            using (SqliteDhtStore store = new SqliteDhtStore(storePath))
            {
                Assert.That(store.Put(SignedNode(peer), null, new FakeVal()).Ok, Is.True);
                DhtIdentity self = DhtIdentity.LoadOrCreate(idPath, store);
                Assert.That(self.NodeId, Is.Not.EqualTo(peer.NodeId));
                Assert.That(store.TryGetSelfNodeId(out DhtKey bound), Is.True);
                Assert.That(bound, Is.EqualTo(self.NodeId));
            }
        }

        [Test]
        public void MissingIdentityWithSelfMetaRefuses()
        {
            string storePath = Path.Combine(m_Dir, "store.db");
            string idPath = Path.Combine(m_Dir, "identity.json");
            DhtIdentity alice = Ident("alice");
            using (SqliteDhtStore store = new SqliteDhtStore(storePath))
            {
                store.SetSelfNodeId(alice.NodeId);
                Assert.That(() => DhtIdentity.LoadOrCreate(idPath, store), Throws.TypeOf<DhtIdentityException>());
            }
        }

        [Test]
        public void LiveUuidExpiresAfter36Hours()
        {
            using SqliteDhtStore store = OpenStore();
            store.NowUnixOverride = 1_700_000_000;
            DhtIdentity alice = Ident("exp");
            FakeVal v = new FakeVal();
            UUID u = UUID.Random();
            DhtRecord node = SignedNode(alice);
            Assert.That(store.Put(node, null, v).Ok, Is.True);
            DhtRecord uuid = DhtRecord.CreateUuid(u, alice.NodeId, 1, false);
            uuid.SignWith(alice);
            Assert.That(store.Put(uuid, node, v).Ok, Is.True);
            Assert.That(store.TryGet(DhtKey.ForUuid(u), out _), Is.True);

            store.NowUnixOverride = 1_700_000_000 + 36 * 3600;
            Assert.That(store.TryGet(DhtKey.ForUuid(u), out _), Is.False, "36h replica TTL");
            store.Expire();
            store.NowUnixOverride = 1_700_000_000;
            Assert.That(store.TryGet(DhtKey.ForUuid(u), out _), Is.False, "Expire deletes the row");
        }

        [Test]
        public void TombstoneLasts168Hours()
        {
            using SqliteDhtStore store = OpenStore();
            store.NowUnixOverride = 1_700_000_000;
            DhtIdentity alice = Ident("tomb");
            FakeVal v = new FakeVal();
            UUID u = UUID.Random();
            DhtRecord node = SignedNode(alice);
            store.Put(node, null, v);
            DhtRecord tomb = DhtRecord.CreateUuid(u, alice.NodeId, 1, true);
            tomb.SignWith(alice);
            Assert.That(store.Put(tomb, node, v).Ok, Is.True);
            store.NowUnixOverride = 1_700_000_000 + 36 * 3600;
            Assert.That(store.TryGet(DhtKey.ForUuid(u), out DhtRecord still), Is.True);
            Assert.That(still.Tombstone, Is.True);
            store.NowUnixOverride = 1_700_000_000 + 168 * 3600;
            Assert.That(store.TryGet(DhtKey.ForUuid(u), out _), Is.False);
        }

        [Test]
        public void ConcurrentPutAndGet()
        {
            using SqliteDhtStore store = OpenStore();
            DhtIdentity id = Ident("c");
            DhtRecord node = SignedNode(id);
            store.Put(node, null, new FakeVal());
            List<UUID> ids = new List<UUID>();
            for (int i = 0; i < 20; i++)
                ids.Add(UUID.Random());

            System.Threading.Tasks.Parallel.ForEach(ids, u =>
            {
                DhtRecord rec = DhtRecord.CreateUuid(u, id.NodeId, 1, false);
                rec.SignWith(id);
                DhtStorePutResult r = store.Put(rec, node, new FakeVal());
                Assert.That(r.Ok, Is.True, r.Error);
                Assert.That(store.TryGet(DhtKey.ForUuid(u), out DhtRecord got), Is.True);
                Assert.That(got.Uuid, Is.EqualTo(u.ToString()));
            });
        }

        private SqliteDhtStore OpenStore()
        {
            return new SqliteDhtStore(Path.Combine(m_Dir, "s.db"));
        }

        private DhtIdentity Ident(string name)
        {
            return DhtIdentity.LoadOrCreate(Path.Combine(m_Dir, name + ".json"), null);
        }

        private static DhtRecord SignedNode(DhtIdentity id)
        {
            DhtRecord n = DhtRecord.CreateNode(id.NodeId, "http://192.0.2.1:8002/", id.PublicKey, 1);
            n.SignWith(id);
            return n;
        }

        private sealed class FakeVal : IDhtStoreValidator
        {
            public bool Proof = true;

            public bool ProofMatches(string homeURI, DhtKey expectedNodeId)
            {
                return Proof;
            }

            public bool OwnsUuid(string homeURI, UUID uuid, DhtKey expectedNodeId)
            {
                return true;
            }
        }
    }
}
