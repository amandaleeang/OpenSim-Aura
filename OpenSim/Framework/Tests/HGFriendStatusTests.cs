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

namespace OpenSim.Framework.Tests
{
    [TestFixture]
    public class HGFriendStatusTests
    {
        const string Home = "http://home.example.com:8002/";
        const string Abroad = "http://other.example.com:8002/";

        [Test]
        public void SameGrid_ignores_slash_and_case()
        {
            Assert.That(OSHHTPHost.SameGrid(Home, "http://home.example.com:8002"), Is.True);
            Assert.That(OSHHTPHost.SameGrid(Home, "http://HOME.EXAMPLE.COM:8002/"), Is.True);
            Assert.That(OSHHTPHost.SameGrid(Home, Abroad), Is.False);
            Assert.That(OSHHTPHost.SameGrid(Home, null), Is.False);
            Assert.That(OSHHTPHost.SameGrid("", Abroad), Is.False);
        }

        [Test]
        public void Home_login_row_is_not_traveling_abroad()
        {
            Assert.That(HGFriendStatus.IsTravelingAbroad(Home, Home), Is.False);
            Assert.That(HGFriendStatus.IsTravelingAbroad(Home, "http://home.example.com:8002"), Is.False);
            Assert.That(HGFriendStatus.IsTravelingAbroad(Home, Abroad), Is.True);
            Assert.That(HGFriendStatus.IsTravelingAbroad(Home, ""), Is.False);
            Assert.That(HGFriendStatus.IsTravelingAbroad(Home, null), Is.False);
        }

        [Test]
        public void Offline_is_not_online()
        {
            // No presence, not abroad — logged out. Leftover RegionID-zero presence
            // without a foreign travel row must not keep HG friends seeing you online.
            // LogoutAgent deletes the foreign travel row, then fans out offline.
            Assert.That(HGFriendStatus.IsOnline(false, true, false), Is.False);
            Assert.That(HGFriendStatus.IsOnline(true, true, false), Is.False);
        }

        [Test]
        public void Home_presence_is_online()
        {
            Assert.That(HGFriendStatus.IsOnline(true, false, false), Is.True);
        }

        [Test]
        public void Traveling_abroad_is_online_even_without_home_presence()
        {
            // After HG TP, home presence is logged out (RegionID zero / missing).
            Assert.That(HGFriendStatus.IsOnline(false, true, true), Is.True);
            Assert.That(HGFriendStatus.IsOnline(true, true, true), Is.True);
        }
    }
}
