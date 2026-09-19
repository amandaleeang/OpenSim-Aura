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

using System.Net;
using NUnit.Framework;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class DhtLocatorTests
    {
        [Test]
        public void RejectsLoopbackEvenIfPrivateAllowed()
        {
            Assert.That(DhtLocator.TryNormalize("http://127.0.0.1:9000/", true, out _), Is.False);
            Assert.That(DhtLocator.TryNormalize("http://localhost:9000/", true, out _), Is.False);
        }

        [Test]
        public void SameHomeComparesNormalized()
        {
            Assert.That(DhtLocator.SameHome("http://192.0.2.10:8002", "http://192.0.2.10:8002/", false), Is.True);
            Assert.That(DhtLocator.SameHome("http://192.0.2.10:8002/", "http://192.0.2.11:8002/", false), Is.False);
        }

        [Test]
        public void RejectsRfc1918UnlessAllowed()
        {
            Assert.That(DhtLocator.TryNormalize("http://192.168.1.10:8002/", false, out _), Is.False);
            Assert.That(DhtLocator.TryNormalize("http://192.168.1.10:8002/", true, out string n), Is.True);
            Assert.That(n, Is.EqualTo("http://192.168.1.10:8002/"));
        }

        [Test]
        public void NormalizesPublicIpAndDropsDefaultPort()
        {
            Assert.That(DhtLocator.TryNormalize("http://192.0.2.1:8002", false, out string a), Is.True);
            Assert.That(a, Is.EqualTo("http://192.0.2.1:8002/"));
            Assert.That(DhtLocator.TryNormalize("http://192.0.2.1:80/", false, out string b), Is.True);
            Assert.That(b, Is.EqualTo("http://192.0.2.1/"));
        }

        [Test]
        public void RejectsIpv4MappedPrivateAndCgnat()
        {
            Assert.That(DhtLocator.RejectAddress(IPAddress.Parse("::ffff:192.168.0.1"), false), Is.True);
            Assert.That(DhtLocator.RejectAddress(IPAddress.Parse("::ffff:10.0.0.1"), false), Is.True);
            Assert.That(DhtLocator.RejectAddress(IPAddress.Parse("100.64.0.1"), false), Is.True);
            Assert.That(DhtLocator.RejectAddress(IPAddress.Parse("224.0.0.1"), false), Is.True);
            Assert.That(DhtLocator.RejectAddress(IPAddress.Parse("192.0.2.1"), false), Is.False);
        }
    }
}
