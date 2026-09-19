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

using NUnit.Framework;
using OpenMetaverse;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class DhtKeyTests
    {
        [Test]
        public void XorIsSymmetricAndSelfIsZero()
        {
            DhtKey a = DhtKey.Sha256Utf8("a");
            DhtKey b = DhtKey.Sha256Utf8("b");
            Assert.That(a.Xor(b), Is.EqualTo(b.Xor(a)));
            Assert.That(a.BucketIndex(a), Is.EqualTo(-1));
        }

        [Test]
        public void BucketIndexMsbIs255LsbIs0()
        {
            DhtKey zero = DhtKey.FromBytes(new byte[32]);
            byte[] msb = new byte[32];
            msb[0] = 0x80;
            byte[] lsb = new byte[32];
            lsb[31] = 0x01;
            Assert.That(zero.BucketIndex(DhtKey.FromBytes(msb)), Is.EqualTo(255));
            Assert.That(zero.BucketIndex(DhtKey.FromBytes(lsb)), Is.EqualTo(0));
        }

        [Test]
        public void ForUuidIsStable()
        {
            UUID id = new UUID("51e2de20-ec0a-4840-9b7a-b3990775a624");
            Assert.That(DhtKey.ForUuid(id), Is.EqualTo(DhtKey.Sha256Utf8("uuid:" + id)));
        }
    }
}
