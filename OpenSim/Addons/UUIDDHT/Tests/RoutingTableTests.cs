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
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class RoutingTableTests
    {
        [Test]
        public void InsertsAndIgnoresSelf()
        {
            RoutingTable rt = Table(out DhtPeer self);
            Assert.That(rt.TryInsert(self, out _), Is.EqualTo(DhtInsertStatus.Ignored));
            DhtPeer p = Peer(0x80);
            Assert.That(rt.TryInsert(p, out _), Is.EqualTo(DhtInsertStatus.Added));
            Assert.That(rt.TryInsert(p, out _), Is.EqualTo(DhtInsertStatus.Refreshed));
        }

        [Test]
        public void PingReplaceEvictsWhenPingFails()
        {
            RoutingTable rt = new RoutingTable(Peer(0x00), 2, 4);
            DhtPeer a = Peer(0x80);
            DhtPeer b = Peer(0x81);
            DhtPeer c = Peer(0x82);
            Assert.That(rt.TryInsert(a, out _), Is.EqualTo(DhtInsertStatus.Added));
            Assert.That(rt.TryInsert(b, out _), Is.EqualTo(DhtInsertStatus.Added));
            Assert.That(rt.TryInsert(c, out DhtPeer stale), Is.EqualTo(DhtInsertStatus.NeedPing));
            Assert.That(stale.NodeId, Is.EqualTo(a.NodeId));
            Assert.That(rt.CompletePingReplace(stale, c, false), Is.True);
            List<DhtPeer> all = rt.SnapshotContacts();
            Assert.That(all.Exists(p => p.NodeId.Equals(c.NodeId)), Is.True);
            Assert.That(all.Exists(p => p.NodeId.Equals(a.NodeId)), Is.False);
        }

        [Test]
        public void PingReplaceKeepsHeadWhenPingSucceeds()
        {
            RoutingTable rt = new RoutingTable(Peer(0x00), 2, 4);
            DhtPeer a = Peer(0x80);
            DhtPeer b = Peer(0x81);
            DhtPeer c = Peer(0x82);
            rt.TryInsert(a, out _);
            rt.TryInsert(b, out _);
            rt.TryInsert(c, out DhtPeer stale);
            Assert.That(rt.CompletePingReplace(stale, c, true), Is.False);
            List<DhtPeer> all = rt.SnapshotContacts();
            Assert.That(all.Exists(p => p.NodeId.Equals(a.NodeId)), Is.True);
            Assert.That(all.Exists(p => p.NodeId.Equals(c.NodeId)), Is.False);
        }

        [Test]
        public void ConcurrentInsertsDoNotThrow()
        {
            RoutingTable rt = Table(out _);
            System.Threading.Tasks.Parallel.For(1, 40, i =>
            {
                byte[] b = new byte[32];
                b[0] = (byte)i;
                rt.TryInsert(new DhtPeer { NodeId = DhtKey.FromBytes(b), HomeURI = "http://192.0.2." + i + ":8002/" }, out DhtPeer stale);
                if (stale != null)
                    rt.CompletePingReplace(stale, new DhtPeer { NodeId = DhtKey.FromBytes(b), HomeURI = "http://x/" }, false);
            });
            Assert.That(rt.SnapshotContacts().Count, Is.GreaterThan(0));
        }

        private static RoutingTable Table(out DhtPeer self)
        {
            self = Peer(0x00);
            return new RoutingTable(self, 20, 20);
        }

        private static DhtPeer Peer(byte first)
        {
            byte[] b = new byte[32];
            b[0] = first;
            return new DhtPeer
            {
                NodeId = DhtKey.FromBytes(b),
                HomeURI = "http://192.0.2.1:8002/",
                LastSeen = DateTime.UtcNow
            };
        }
    }
}
