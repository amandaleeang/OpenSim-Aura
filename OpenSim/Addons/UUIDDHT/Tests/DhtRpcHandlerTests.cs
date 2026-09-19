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
    public class DhtRpcHandlerTests
    {
        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            TestBin.UseOpenSimBin();
            m_Dir = Path.Combine(Path.GetTempPath(), "uuiddht-rpc-" + UUID.Random());
            Directory.CreateDirectory(m_Dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(m_Dir, true); } catch { }
        }

        [Test]
        public void PingOk()
        {
            using Fixture f = Create();
            DhtRpcRequest ping = DhtRpcRequest.Create(f.Peer, f.Home, "PING", new DhtRpcBody());
            DhtRpcResponse resp = f.Node.HandleRpc(ping);
            Assert.That(resp.Ok, Is.True, resp.Error);
            Assert.That(DhtHttpClient.VerifyResponse(resp), Is.True);
        }

        [Test]
        public void UnknownOpMalformed()
        {
            using Fixture f = Create();
            DhtRpcRequest req = DhtRpcRequest.Create(f.Peer, f.Home, "NOPE", new DhtRpcBody());
            DhtRpcResponse resp = f.Node.HandleRpc(req);
            Assert.That(resp.Ok, Is.False);
            Assert.That(resp.Error, Is.EqualTo("malformed"));
        }

        [Test]
        public void BadSigRejected()
        {
            using Fixture f = Create();
            DhtRpcRequest ping = DhtRpcRequest.Create(f.Peer, f.Home, "PING", new DhtRpcBody());
            ping.Sig = new string('0', 128);
            DhtRpcResponse resp = f.Node.HandleRpc(ping);
            Assert.That(resp.Ok, Is.False);
            Assert.That(resp.Error, Is.EqualTo("bad_sig"));
        }

        [Test]
        public void ReplayNonceRejected()
        {
            using Fixture f = Create();
            DhtRpcRequest ping = DhtRpcRequest.Create(f.Peer, f.Home, "PING", new DhtRpcBody());
            Assert.That(f.Node.HandleRpc(ping).Ok, Is.True);
            DhtRpcResponse again = f.Node.HandleRpc(ping);
            Assert.That(again.Ok, Is.False);
        }

        [Test]
        public void StoreThenFindValue()
        {
            using Fixture f = Create();
            DhtRecord node = DhtRecord.CreateNode(f.Peer.NodeId, f.Home, f.Peer.PublicKey, 1);
            node.SignWith(f.Peer);
            DhtRpcRequest storeNode = DhtRpcRequest.Create(f.Peer, f.Home, "STORE",
                new DhtRpcBody { Record = node });
            Assert.That(f.Node.HandleRpc(storeNode).Ok, Is.True, "store node");

            UUID u = UUID.Random();
            DhtRecord rec = DhtRecord.CreateUuid(u, f.Peer.NodeId, 1, false);
            rec.SignWith(f.Peer);
            DhtRpcRequest store = DhtRpcRequest.Create(f.Peer, f.Home, "STORE",
                new DhtRpcBody { Record = rec, Node = node });
            DhtRpcResponse stored = f.Node.HandleRpc(store);
            Assert.That(stored.Ok, Is.True, stored.Error);

            DhtRpcRequest find = DhtRpcRequest.Create(f.Peer, f.Home, "FIND_VALUE",
                new DhtRpcBody { Key = DhtKey.ForUuid(u).ToHex() });
            DhtRpcResponse found = f.Node.HandleRpc(find);
            Assert.That(found.Ok, Is.True);
            Assert.That(found.Value, Is.Not.Null);
            Assert.That(found.Value.Uuid, Is.EqualTo(u.ToString()));
        }

        [Test]
        public void StoreUuidWithoutNodeMalformed()
        {
            using Fixture f = Create();
            UUID u = UUID.Random();
            DhtRecord rec = DhtRecord.CreateUuid(u, f.Peer.NodeId, 1, false);
            rec.SignWith(f.Peer);
            DhtRpcRequest store = DhtRpcRequest.Create(f.Peer, f.Home, "STORE",
                new DhtRpcBody { Record = rec });
            DhtRpcResponse resp = f.Node.HandleRpc(store);
            Assert.That(resp.Ok, Is.False);
            Assert.That(resp.Error, Is.EqualTo("malformed"));
        }

        [Test]
        public void OversizedBodyIs413()
        {
            byte[] big = new byte[40000];
            using MemoryStream ms = new MemoryStream(big);
            bool tooBig;
            DhtRpcHandler.ReadLimited(ms, 32768, out tooBig);
            Assert.That(tooBig, Is.True);
        }

        [Test]
        public void ParseBootstrapSplitsAndTrims()
        {
            string[] a = UuidDhtConfig.ParseBootstrap("http://a:8002/, http://b:8002/");
            Assert.That(a.Length, Is.EqualTo(2));
            Assert.That(UuidDhtConfig.ParseBootstrap("").Length, Is.EqualTo(0));
        }

        [Test]
        public void FakeTransportPing()
        {
            using Fixture f = Create();
            DhtFakeTransport t = new DhtFakeTransport();
            t.Add(f.Home, f.Node);
            DhtNodeDocument doc = t.GetNode(f.Home, default);
            Assert.That(doc.NodeId, Is.EqualTo(f.Node.Identity.NodeId.ToHex()));
            DhtRpcResponse ping = t.Rpc(f.Home, DhtRpcRequest.Create(f.Peer, f.Home, "PING", new DhtRpcBody()), default);
            Assert.That(ping.Ok, Is.True);
        }

        [Test]
        public void OwnsLocalFalseWithoutServices()
        {
            using Fixture f = Create();
            Assert.That(f.Node.OwnsLocal(UUID.Random()), Is.False);
            DhtOwnsDocument doc = f.Node.CreateOwnsDocument(UUID.Random());
            Assert.That(doc.Has, Is.False);
            Assert.That(doc.Sig, Is.Not.Empty);
        }

        [Test]
        public void OwnsDocumentVerifyRejectsMissingSignature()
        {
            using Fixture f = Create();
            UUID u = UUID.Random();
            DhtOwnsDocument doc = f.Node.CreateOwnsDocument(u);
            doc.Has = true;
            doc.Sig = string.Empty;
            DhtNodeDocument node = f.Node.CreateNodeDocument();
            Assert.That(DhtHttpClient.VerifyOwnsDocument(doc, f.Home, u, f.Node.Identity.NodeId, node), Is.False);

            DhtOwnsDocument signed = f.Node.CreateOwnsDocument(u);
            signed.Has = true;
            string canon = DhtCanonical.Owns(signed.Uuid, signed.NodeId, signed.HomeURI, true, signed.Ts);
            signed.Sig = DhtSigner.ToHex(f.Node.Identity.Sign(DhtCanonical.Utf8(canon)));
            Assert.That(DhtHttpClient.VerifyOwnsDocument(signed, f.Home, u, f.Node.Identity.NodeId, node), Is.True);
        }

        private Fixture Create()
        {
            string home = "http://192.0.2.1:8002/";
            DhtIdentity id = DhtIdentity.LoadOrCreate(Path.Combine(m_Dir, "id.json"), null);
            SqliteDhtStore store = new SqliteDhtStore(Path.Combine(m_Dir, "s.db"));
            UuidDhtConfig cfg = new UuidDhtConfig
            {
                Enabled = true,
                HomeURI = home,
                IdentityPath = Path.Combine(m_Dir, "id.json"),
                StorePath = Path.Combine(m_Dir, "s.db"),
                K = 20,
                SiblingSize = 20,
                MaxRpcBytes = 32768,
                RpcTimeoutMs = 300,
                AllowPrivateHomeURI = false,
                Bootstrap = Array.Empty<string>()
            };
            FakeVal val = new FakeVal();
            UuidDhtNode node = UuidDhtNode.CreateForTests(cfg, id, store, new DhtFakeTransport(), val);
            return new Fixture { Node = node, Peer = id, Home = home, Store = store };
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

        private sealed class FakeVal : IDhtStoreValidator
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
