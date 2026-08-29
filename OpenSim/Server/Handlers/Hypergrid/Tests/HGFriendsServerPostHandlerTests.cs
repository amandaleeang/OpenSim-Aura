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
using System.IO;
using System.Text;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

namespace OpenSim.Server.Handlers.Hypergrid.Tests
{
    [TestFixture]
    public class HGFriendsServerPostHandlerTests : OpenSimTestCase
    {
        [Test]
        public void TestDeleteFriendshipRequiresSecret()
        {
            TestHelpers.InMethod();

            FakeHGFriendsService svc = new FakeHGFriendsService();
            HGFriendsServerPostHandler handler = new HGFriendsServerPostHandler(svc, null, null);

            byte[] noSecret = Post(handler, "METHOD=deletefriendship&PrincipalID=" + UUID.Zero + "&Friend=" + UUID.Zero);
            Assert.That(ResultIsTrue(noSecret), Is.False);
            Assert.That(svc.DeleteCalled, Is.False);

            byte[] emptySecret = Post(handler, "METHOD=deletefriendship&SECRET=&PrincipalID=" + UUID.Zero + "&Friend=" + UUID.Zero);
            Assert.That(ResultIsTrue(emptySecret), Is.False);
            Assert.That(svc.DeleteCalled, Is.False);
        }

        [Test]
        public void TestDeleteFriendshipCallsServiceWhenSecretPresent()
        {
            TestHelpers.InMethod();

            FakeHGFriendsService svc = new FakeHGFriendsService { DeleteResult = true };
            HGFriendsServerPostHandler handler = new HGFriendsServerPostHandler(svc, null, null);

            UUID a = TestHelpers.ParseTail(0x51);
            UUID b = TestHelpers.ParseTail(0x52);
            byte[] reply = Post(handler,
                "METHOD=deletefriendship&SECRET=abcd1234&PrincipalID=" + a + "&Friend=" + b);

            Assert.That(svc.DeleteCalled, Is.True);
            Assert.That(svc.LastSecret, Is.EqualTo("abcd1234"));
            Assert.That(ResultIsTrue(reply), Is.True);
        }

        [Test]
        public void TestNewFriendshipReturnsBoolResult()
        {
            TestHelpers.InMethod();

            FakeHGFriendsService svc = new FakeHGFriendsService { NewFriendshipResult = true };
            HGFriendsServerPostHandler handler = new HGFriendsServerPostHandler(svc, null, null);

            UUID a = TestHelpers.ParseTail(0x53);
            byte[] reply = Post(handler,
                "METHOD=newfriendship&PrincipalID=" + a + "&Friend=" + UUID.Zero + ";http://x/;N N;s");

            string xml = Encoding.UTF8.GetString(reply);
            Assert.That(xml.Contains("RESULT") || xml.Contains("Result"), Is.True);
            Dictionary<string, object> parsed = ServerUtils.ParseXmlResponse(xml);
            Assert.That(parsed, Is.Not.Null);
            Assert.That(OpenSim.Services.Connectors.Hypergrid.HGFriendsServicesConnector.TryParseSuccess(parsed, out bool ok) && ok);
        }

        static byte[] Post(HGFriendsServerPostHandler handler, string body)
        {
            MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(body));
            return handler.Handle("/hgfriends", ms, null, null);
        }

        static bool ResultIsTrue(byte[] reply)
        {
            string xml = Encoding.UTF8.GetString(reply);
            Dictionary<string, object> parsed = ServerUtils.ParseXmlResponse(xml);
            return OpenSim.Services.Connectors.Hypergrid.HGFriendsServicesConnector.TryParseSuccess(parsed, out bool ok) && ok;
        }

        class FakeHGFriendsService : IHGFriendsService
        {
            public bool DeleteCalled;
            public string LastSecret;
            public bool DeleteResult;
            public bool NewFriendshipResult;

            public int GetFriendPerms(UUID userID, UUID friendID) { return -1; }
            public bool NewFriendship(FriendInfo finfo, bool verified) { return NewFriendshipResult; }
            public bool NewFriendship(FriendInfo finfo, bool verified, out string reason)
            {
                reason = NewFriendshipResult ? "upgraded" : "no_pending";
                return NewFriendshipResult;
            }
            public bool NewFriendship(FriendInfo finfo, bool verified, UUID sessionId, out string reason)
            {
                return NewFriendship(finfo, verified, out reason);
            }
            public bool DeleteFriendship(FriendInfo finfo, string secret)
            {
                DeleteCalled = true;
                LastSecret = secret;
                return DeleteResult;
            }
            public bool FriendshipOffered(UUID from, string fromName, UUID to, string message) { return false; }
            public bool FriendshipOffered(HGFriendshipOffer offer, out bool delivered)
            {
                delivered = false;
                return false;
            }
            public bool StoreReversePending(UUID fromId, UUID toId, string fromUui) { return false; }
            public bool DropReversePending(UUID fromId, UUID toId) { return false; }
            public bool ValidateFriendshipOffered(UUID fromID, UUID toID) { return false; }
            public List<UUID> StatusNotification(List<string> friends, UUID userID, bool online)
            {
                return new List<UUID>();
            }
        }
    }
}
