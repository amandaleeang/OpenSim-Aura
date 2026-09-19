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
using NUnit.Framework;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class SiblingListTests
    {
        [Test]
        public void IncludesSelfAndCapsAtS()
        {
            DhtPeer self = Peer(0x00);
            RoutingTable rt = new RoutingTable(self, 20, 3);
            rt.TryInsert(Peer(0x01), out _);
            rt.TryInsert(Peer(0x02), out _);
            rt.TryInsert(Peer(0x03), out _);
            rt.TryInsert(Peer(0x80), out _);
            IReadOnlyList<DhtPeer> sibs = rt.SnapshotSiblings();
            Assert.That(sibs.Count, Is.EqualTo(3));
            Assert.That(sibs[0].NodeId, Is.EqualTo(self.NodeId));
        }

        [Test]
        public void IsResponsibleWhenAmongSClosestToKey()
        {
            DhtPeer self = Peer(0x00);
            RoutingTable rt = new RoutingTable(self, 20, 2);
            rt.TryInsert(Peer(0x80), out _);
            Assert.That(rt.IsResponsible(self.NodeId), Is.True);
            Assert.That(rt.IsResponsible(Peer(0x80).NodeId), Is.True);
        }

        private static DhtPeer Peer(byte first)
        {
            byte[] b = new byte[32];
            b[0] = first;
            return new DhtPeer { NodeId = DhtKey.FromBytes(b), HomeURI = "http://192.0.2.1:8002/" };
        }
    }
}
