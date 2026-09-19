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
    public class DhtIdentityTests
    {
        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            m_Dir = Path.Combine(Path.GetTempPath(), "uuiddht-id-" + UUID.Random());
            Directory.CreateDirectory(m_Dir);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (m_Dir != null && Directory.Exists(m_Dir))
                    Directory.Delete(m_Dir, true);
            }
            catch
            {
            }
        }

        [Test]
        public void CreateThenReloadSameNodeId()
        {
            string path = Path.Combine(m_Dir, "identity-9000.json");
            DhtIdentity a = DhtIdentity.LoadOrCreate(path, null);
            Assert.That(a.NodeSeq, Is.EqualTo(1));
            DhtIdentity b = DhtIdentity.LoadOrCreate(path, null);
            Assert.That(b.NodeId, Is.EqualTo(a.NodeId));
            Assert.That(b.PublicKey, Is.EqualTo(a.PublicKey));
        }

        [Test]
        public void MissingIdentityWithStoreOwnerRefuses()
        {
            string path = Path.Combine(m_Dir, "identity-9001.json");
            FakeStore store = new FakeStore { Owner = DhtKey.Sha256Utf8("other") };
            Assert.That(() => DhtIdentity.LoadOrCreate(path, store), Throws.TypeOf<DhtIdentityException>());
            Assert.That(File.Exists(path), Is.False);
        }

        [Test]
        public void TamperedNodeIdRefuses()
        {
            string path = Path.Combine(m_Dir, "identity-9002.json");
            DhtIdentity.LoadOrCreate(path, null);
            string json = File.ReadAllText(path);
            int i = json.IndexOf("\"nodeId\":\"", StringComparison.Ordinal) + 10;
            json = json.Substring(0, i) + new string('0', 64) + json.Substring(i + 64);
            File.WriteAllText(path, json);
            Assert.That(() => DhtIdentity.LoadOrCreate(path, null), Throws.TypeOf<DhtIdentityException>());
        }

        [Test]
        public void ProofDocumentVerifies()
        {
            string path = Path.Combine(m_Dir, "identity-9003.json");
            DhtIdentity id = DhtIdentity.LoadOrCreate(path, null);
            long ts = 1789737600;
            string nodeId = id.NodeId.ToHex();
            string pub = DhtSigner.ToHex(id.PublicKey);
            string home = "http://grid.example:8002/";
            byte[] sig = id.Sign(DhtCanonical.Utf8(DhtCanonical.Proof(nodeId, home, pub, ts)));
            Assert.That(DhtIdentity.Verify(id.PublicKey, DhtCanonical.Utf8(DhtCanonical.Proof(nodeId, home, pub, ts)), sig), Is.True);
        }

        private sealed class FakeStore : IDhtStore
        {
            public DhtKey? Owner;

            public bool TryGetSelfNodeId(out DhtKey nodeId)
            {
                if (Owner.HasValue)
                {
                    nodeId = Owner.Value;
                    return true;
                }
                nodeId = default;
                return false;
            }

            public void SetSelfNodeId(DhtKey nodeId)
            {
                Owner = nodeId;
            }

            public bool TryGet(DhtKey dhtKey, out DhtRecord record)
            {
                record = null;
                return false;
            }

            public DhtStorePutResult Put(DhtRecord record, DhtRecord attachedNode, IDhtStoreValidator validator)
            {
                return DhtStorePutResult.Fail("malformed");
            }

            public int CountLiveUuidByOwner(DhtKey nodeId)
            {
                return 0;
            }

            public System.Collections.Generic.IEnumerable<DhtRecord> AllLiveOwnedBy(DhtKey nodeId)
            {
                return System.Array.Empty<DhtRecord>();
            }

            public void Expire()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
