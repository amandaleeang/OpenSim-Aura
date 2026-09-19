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
    public class DhtHostilePeerTests
    {
        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            TestBin.UseOpenSimBin();
            m_Dir = Path.Combine(Path.GetTempPath(), "uuiddht-hostile-" + UUID.Random());
            Directory.CreateDirectory(m_Dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(m_Dir, true); } catch { }
        }

        [Test]
        public void MalloryCannotPublishAliceNodeId()
        {
            using SqliteDhtStore store = OpenStore();
            DhtIdentity alice = Ident("alice");
            DhtIdentity mallory = Ident("mallory");
            DhtRecord steal = DhtRecord.CreateNode(alice.NodeId, "http://192.0.2.9:8002/", mallory.PublicKey, 1);
            steal.SignWith(mallory);
            DhtStorePutResult r = store.Put(steal, null, new Val());
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Is.EqualTo("bad_sig").Or.EqualTo("malformed"));
        }

        [Test]
        public void MalloryCannotForgeAliceUuidSignature()
        {
            using SqliteDhtStore store = OpenStore();
            DhtIdentity alice = Ident("alice");
            DhtIdentity mallory = Ident("mallory");
            Val v = new Val();
            DhtRecord aliceNode = SignedNode(alice);
            store.Put(aliceNode, null, v);

            UUID u = UUID.Random();
            DhtRecord forged = DhtRecord.CreateUuid(u, alice.NodeId, 1, false);
            forged.SignWith(mallory);
            DhtStorePutResult r = store.Put(forged, aliceNode, v);
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Is.EqualTo("bad_sig"));
        }

        [Test]
        public void MalloryCannotOverwriteAliceUuid()
        {
            using SqliteDhtStore store = OpenStore();
            DhtIdentity alice = Ident("alice");
            DhtIdentity mallory = Ident("mallory");
            Val v = new Val();
            UUID u = UUID.Random();
            DhtRecord aliceNode = SignedNode(alice);
            store.Put(aliceNode, null, v);
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
        public void MalloryCannotSquatLiveHome()
        {
            using SqliteDhtStore store = OpenStore();
            DhtIdentity alice = Ident("alice");
            DhtIdentity mallory = Ident("mallory");
            string home = "http://192.0.2.10:8002/";
            Val live = new Val { Proof = true };
            DhtRecord locA = DhtRecord.CreateLoc(home, alice.NodeId, 1, false);
            locA.SignWith(alice);
            Assert.That(store.Put(locA, SignedNode(alice), live).Ok, Is.True);

            DhtRecord locM = DhtRecord.CreateLoc(home, mallory.NodeId, 1, false);
            locM.SignWith(mallory);
            Assert.That(store.Put(locM, SignedNode(mallory), live).Error, Is.EqualTo("home_taken"));
        }

        [Test]
        public void MalloryOwnsCallbackRejected()
        {
            using SqliteDhtStore store = OpenStore();
            DhtIdentity mallory = Ident("mallory");
            Val deny = new Val { Owns = false };
            DhtRecord node = SignedNode(mallory);
            store.Put(node, null, new Val());
            UUID u = UUID.Random();
            DhtRecord rec = DhtRecord.CreateUuid(u, mallory.NodeId, 1, false);
            rec.SignWith(mallory);
            DhtStorePutResult r = store.Put(rec, node, deny);
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Is.EqualTo("owns_failed"));
        }

        [Test]
        public void MalloryUnsignedRpcRejected()
        {
            using Fixture f = CreateAlice();
            DhtIdentity mallory = Ident("mallory");
            DhtRpcRequest ping = DhtRpcRequest.Create(mallory, "http://192.0.2.9:8002/", "PING", new DhtRpcBody());
            ping.Sig = string.Empty;
            DhtRpcResponse resp = f.Node.HandleRpc(ping);
            Assert.That(resp.Ok, Is.False);
            Assert.That(resp.Error, Is.EqualTo("malformed").Or.EqualTo("bad_sig"));
        }

        [Test]
        public void MalloryCannotStoreAliceUuidOverRpc()
        {
            using Fixture f = CreateAlice();
            DhtIdentity alice = f.Peer;
            UUID u = UUID.Random();
            DhtRecord aliceNode = DhtRecord.CreateNode(alice.NodeId, f.Home, alice.PublicKey, 1);
            aliceNode.SignWith(alice);
            DhtRecord aliceUuid = DhtRecord.CreateUuid(u, alice.NodeId, 1, false);
            aliceUuid.SignWith(alice);
            DhtRpcRequest storeAlice = DhtRpcRequest.Create(alice, f.Home, "STORE",
                new DhtRpcBody { Record = aliceUuid, Node = aliceNode });
            Assert.That(f.Node.HandleRpc(storeAlice).Ok, Is.True);

            DhtIdentity mallory = Ident("mallory");
            DhtRecord malloryNode = DhtRecord.CreateNode(mallory.NodeId, "http://192.0.2.9:8002/", mallory.PublicKey, 1);
            malloryNode.SignWith(mallory);
            DhtRecord steal = DhtRecord.CreateUuid(u, mallory.NodeId, 1, false);
            steal.SignWith(mallory);
            DhtRpcRequest storeSteal = DhtRpcRequest.Create(mallory, "http://192.0.2.9:8002/", "STORE",
                new DhtRpcBody { Record = steal, Node = malloryNode });
            DhtRpcResponse resp = f.Node.HandleRpc(storeSteal);
            Assert.That(resp.Ok, Is.False);
            Assert.That(resp.Error, Is.EqualTo("taken"));
        }

        [Test]
        public void ClientRejectsTamperedUuidRecord()
        {
            DhtIdentity alice = Ident("alice");
            UUID u = UUID.Random();
            DhtRecord uuidRec = DhtRecord.CreateUuid(u, alice.NodeId, 1, false);
            uuidRec.SignWith(alice);
            DhtRecord nodeRec = DhtRecord.CreateNode(alice.NodeId, "http://192.0.2.10:8002/", alice.PublicKey, 1);
            nodeRec.SignWith(alice);
            Assert.That(UuidDhtClient.TryBuildHome(uuidRec, nodeRec, u, false), Is.Not.Null);

            uuidRec.Seq = 99;
            Assert.That(UuidDhtClient.TryBuildHome(uuidRec, nodeRec, u, false), Is.Null);
        }

        private Fixture CreateAlice()
        {
            string home = "http://192.0.2.10:8002/";
            DhtIdentity id = Ident("alice-rpc");
            SqliteDhtStore store = new SqliteDhtStore(Path.Combine(m_Dir, "rpc.db"));
            UuidDhtConfig cfg = new UuidDhtConfig
            {
                Enabled = true,
                HomeURI = home,
                IdentityPath = Path.Combine(m_Dir, "alice-rpc.json"),
                StorePath = Path.Combine(m_Dir, "rpc.db"),
                K = 20,
                SiblingSize = 20,
                MaxRpcBytes = 32768,
                RpcTimeoutMs = 300,
                AllowPrivateHomeURI = false,
                Bootstrap = Array.Empty<string>()
            };
            UuidDhtNode node = UuidDhtNode.CreateForTests(cfg, id, store, new DhtFakeTransport(), new Val());
            return new Fixture { Node = node, Peer = id, Home = home, Store = store };
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
            DhtRecord n = DhtRecord.CreateNode(id.NodeId, "http://192.0.2.10:8002/", id.PublicKey, 1);
            n.SignWith(id);
            return n;
        }

        private sealed class Fixture : IDisposable
        {
            public UuidDhtNode Node;
            public DhtIdentity Peer;
            public string Home;
            public IDhtStore Store;

            public void Dispose()
            {
                Store?.Dispose();
            }
        }

        private sealed class Val : IDhtStoreValidator
        {
            public bool Proof = true;
            public bool Owns = true;

            public bool ProofMatches(string homeURI, DhtKey expectedNodeId)
            {
                return Proof;
            }

            public bool OwnsUuid(string homeURI, UUID uuid, DhtKey expectedNodeId)
            {
                return Owns;
            }
        }
    }
}
