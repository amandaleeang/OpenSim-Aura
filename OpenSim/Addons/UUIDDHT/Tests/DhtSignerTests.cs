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

using System.Text;
using NUnit.Framework;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class DhtSignerTests
    {
        [Test]
        public void SignVerifyRoundTrip()
        {
            DhtSigner.GenerateKeyPair(out byte[] sk, out byte[] pk);
            byte[] msg = Encoding.UTF8.GetBytes("proof-v1|abc");
            byte[] sig = DhtSigner.Sign(sk, msg);
            Assert.That(sig.Length, Is.EqualTo(64));
            Assert.That(DhtSigner.Verify(pk, msg, sig), Is.True);
        }

        [Test]
        public void VerifyRejectsTamperedMessage()
        {
            DhtSigner.GenerateKeyPair(out byte[] sk, out byte[] pk);
            byte[] sig = DhtSigner.Sign(sk, Encoding.UTF8.GetBytes("a"));
            Assert.That(DhtSigner.Verify(pk, Encoding.UTF8.GetBytes("b"), sig), Is.False);
        }

        [Test]
        public void NodeIdIsSha256OfPubkey()
        {
            DhtSigner.GenerateKeyPair(out byte[] _, out byte[] pk);
            DhtKey id = DhtSigner.NodeIdFromPubkey(pk);
            Assert.That(id, Is.EqualTo(DhtKey.Sha256(pk)));
            Assert.That(id.ToHex().Length, Is.EqualTo(64));
        }
    }
}
