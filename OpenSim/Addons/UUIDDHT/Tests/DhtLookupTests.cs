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

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class DhtLookupTests
    {
        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            TestBin.UseOpenSimBin();
            m_Dir = Path.Combine(Path.GetTempPath(), "uuiddht-lookup-" + UUID.Random());
            Directory.CreateDirectory(m_Dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(m_Dir, true); } catch { }
        }

        [Test]
        public void FindsValueOnNonSeedViaInLookupAdmission()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack seed = Make("seed", "http://192.0.2.10:8002/", t);
            using NodePack owner = Make("owner", "http://192.0.2.11:8002/", t);
            using NodePack lookup = Make("lookup", "http://192.0.2.12:8002/", t);

            UUID u = UuidCloserTo(owner.Id.NodeId, seed.Id.NodeId);
            DhtRecord nodeRec = DhtRecord.CreateNode(owner.Id.NodeId, owner.Home, owner.Id.PublicKey, 1);
            nodeRec.SignWith(owner.Id);
            Assert.That(owner.Store.Put(nodeRec, null, new AlwaysVal()).Ok, Is.True);
            DhtRecord uuidRec = DhtRecord.CreateUuid(u, owner.Id.NodeId, 1, false);
            uuidRec.SignWith(owner.Id);
            Assert.That(owner.Store.Put(uuidRec, nodeRec, new AlwaysVal()).Ok, Is.True);

            seed.Node.Table.TryInsert(Peer(owner), out DhtPeer _);
            lookup.Node.Table.TryInsert(Peer(seed), out DhtPeer _);

            DhtRecord found = lookup.Node.Lookup.FindValue(DhtKey.ForUuid(u), CancellationToken.None);
            Assert.That(found, Is.Not.Null);
            Assert.That(found.Uuid, Is.EqualTo(u.ToString()));
            Assert.That(found.NodeId, Is.EqualTo(owner.Id.NodeId.ToHex()));
        }

        [Test]
        public void MergeKeepsHighestSeqSameOwner()
        {
            DhtRecord a = new DhtRecord { Kind = "uuid", NodeId = "aa", Seq = 1, Sig = "s1", Key = "uuid:x" };
            DhtRecord b = new DhtRecord { Kind = "uuid", NodeId = "aa", Seq = 3, Sig = "s3", Key = "uuid:x" };
            DhtRecord c = new DhtRecord { Kind = "uuid", NodeId = "aa", Seq = 2, Sig = "s2", Key = "uuid:x" };
            DhtRecord m = DhtLookup.Merge(new List<DhtRecord> { a, b, c });
            Assert.That(m.Seq, Is.EqualTo(3));
        }

        [Test]
        public void MergeDropsSplitOwnersAtSameSeq()
        {
            DhtRecord a = new DhtRecord { Kind = "uuid", NodeId = "aa", Seq = 1, Sig = "s", Key = "k" };
            DhtRecord b = new DhtRecord { Kind = "uuid", NodeId = "bb", Seq = 1, Sig = "t", Key = "k" };
            Assert.That(DhtLookup.Merge(new List<DhtRecord> { a, b }), Is.Null);
        }

        private static UUID UuidCloserTo(DhtKey closer, DhtKey farther)
        {
            for (int i = 0; i < 1000; i++)
            {
                UUID u = UUID.Random();
                DhtKey k = DhtKey.ForUuid(u);
                if (closer.Xor(k).CompareTo(farther.Xor(k)) < 0)
                    return u;
            }
            throw new InvalidOperationException("could not find a uuid closer to owner than seed");
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
