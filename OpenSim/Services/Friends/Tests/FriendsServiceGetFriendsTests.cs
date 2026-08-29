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
using System.Text;
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Data.Null;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Hypergrid;
using OpenSim.Services.Connectors.Hypergrid;
using OpenSim.Services.Friends;
using OpenSim.Services.HypergridService;
using OpenSim.Services.Interfaces;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

namespace OpenSim.Services.Friends.Tests
{
    [TestFixture]
    public class FriendsServiceGetFriendsTests
    {
        IFriendsService m_svc;

        [SetUp]
        public void SetUp()
        {
            NullFriendsData.Clear();
            IniConfigSource config = new IniConfigSource();
            config.AddConfig("FriendsService");
            // LoadPlugin resolves from the process directory; tests run with bin on the probing path.
            config.Configs["FriendsService"].Set("StorageProvider",
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "OpenSim.Data.Null.dll")));
            m_svc = new FriendsService(config);
        }

        [Test]
        public void GetFriendsString_ReturnsUuidAndUuiFriendRows()
        {
            UUID principal = UUID.Random();
            UUID friendUuid = UUID.Random();
            string friendUui = friendUuid + ";http://grid.example:8002/;Alice Bob;abcd1234";

            Assert.That(m_svc.StoreFriend(principal.ToString(), friendUuid.ToString(), 1));
            Assert.That(m_svc.StoreFriend(principal.ToString(), friendUui, 0));

            FriendInfo[] got = m_svc.GetFriends(principal.ToString());
            Assert.That(got.Length, Is.EqualTo(2));
            Assert.That(Array.Exists(got, f => f.Friend == friendUuid.ToString() && f.PrincipalID == principal));
            Assert.That(Array.Exists(got, f => f.Friend == friendUui && f.PrincipalID == principal));
        }

        [Test]
        public void GetFriendsString_SkipsEmptyFriend()
        {
            UUID principal = UUID.Random();
            UUID friend = UUID.Random();
            m_svc.StoreFriend(principal.ToString(), string.Empty, 0);
            m_svc.StoreFriend(principal.ToString(), friend.ToString(), 1);

            FriendInfo[] got = m_svc.GetFriends(principal.ToString());
            Assert.That(got.Length, Is.EqualTo(1));
            Assert.That(got[0].Friend, Is.EqualTo(friend.ToString()));
        }

        [Test]
        public void GetFriendsString_UuiPrincipalDoesNotThrow()
        {
            UUID principal = UUID.Random();
            UUID friend = UUID.Random();
            string principalUui = principal + ";http://home.example:8002/;Pat User";

            m_svc.StoreFriend(principalUui, friend.ToString(), 0);

            FriendInfo[] got = null;
            Assert.DoesNotThrow(() => got = m_svc.GetFriends(principalUui));
            Assert.That(got.Length, Is.EqualTo(1));
            Assert.That(got[0].PrincipalID, Is.EqualTo(principal));
            Assert.That(got[0].Friend, Is.EqualTo(friend.ToString()));
            Assert.That(got[0].TheirFlags, Is.EqualTo(-1));
        }
    }

    [TestFixture]
    public class HgFriendsPr1HelperTests
    {
        [Test]
        public void TryParseSuccess_AcceptsResultTrueAndLegacySuccess()
        {
            Assert.That(HGFriendsServicesConnector.TryParseSuccess(
                new Dictionary<string, object> { ["RESULT"] = "True" }, out bool a) && a);
            Assert.That(HGFriendsServicesConnector.TryParseSuccess(
                new Dictionary<string, object> { ["Result"] = "Success" }, out bool b) && b);
            Assert.That(HGFriendsServicesConnector.TryParseSuccess(
                new Dictionary<string, object> { ["RESULT"] = "False" }, out bool c) && !c);
            Assert.That(!HGFriendsServicesConnector.TryParseSuccess(null, out _));
        }

        [Test]
        public void TryResolveOffererHomeURI_PreservesHttps()
        {
            Assert.That(HGFriendsService.TryResolveOffererHomeURI(
                "Alice.Bob@https://grid.example:8002", out string httpsHome));
            Assert.That(httpsHome.StartsWith("https://", StringComparison.OrdinalIgnoreCase), Is.True, httpsHome);

            Assert.That(HGFriendsService.TryResolveOffererHomeURI(
                "Alice.Bob@grid.example:8002", out string httpHome));
            Assert.That(httpHome.IndexOf("grid.example", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.That(HGFriendsService.TryResolveOffererHomeURI("NoAtSign", out _), Is.False);
        }

        [Test]
        public void ResolveOffererName_PrefersFromName()
        {
            Assert.That(
                OpenSim.Region.CoreModules.Avatar.Friends.FriendsSimpleRequestHandler.ResolveOffererName(
                    "Alice.Bob@grid.example:8002", null),
                Is.EqualTo("Alice.Bob@grid.example:8002"));
            Assert.That(
                OpenSim.Region.CoreModules.Avatar.Friends.FriendsSimpleRequestHandler.ResolveOffererName("", null),
                Is.EqualTo("Unknown"));
        }

        [Test]
        public void DeleteFriendshipHandler_RequiresSecretAndCallsService()
        {
            FakeHGFriendsService svc = new FakeHGFriendsService { DeleteResult = true };
            HGFriendsServerPostHandler handler = new HGFriendsServerPostHandler(svc, null, null);

            byte[] missing = Post(handler, "METHOD=deletefriendship&PrincipalID=" + UUID.Zero + "&Friend=" + UUID.Zero);
            Assert.That(IsTrue(missing), Is.False);
            Assert.That(svc.DeleteCalled, Is.False);

            byte[] empty = Post(handler, "METHOD=deletefriendship&SECRET=&PrincipalID=" + UUID.Zero + "&Friend=" + UUID.Zero);
            Assert.That(IsTrue(empty), Is.False);

            UUID a = UUID.Random();
            UUID b = UUID.Random();
            byte[] ok = Post(handler, "METHOD=deletefriendship&SECRET=abcd1234&PrincipalID=" + a + "&Friend=" + b);
            Assert.That(svc.DeleteCalled, Is.True);
            Assert.That(svc.LastSecret, Is.EqualTo("abcd1234"));
            Assert.That(IsTrue(ok), Is.True);
        }

        [Test]
        public void NewFriendshipHandler_ReturnsParseableBoolResult()
        {
            FakeHGFriendsService svc = new FakeHGFriendsService { NewFriendshipResult = true };
            HGFriendsServerPostHandler handler = new HGFriendsServerPostHandler(svc, null, null);
            byte[] reply = Post(handler, "METHOD=newfriendship&PrincipalID=" + UUID.Random() + "&Friend=" + UUID.Random());
            Dictionary<string, object> parsed = ServerUtils.ParseXmlResponse(Encoding.UTF8.GetString(reply));
            Assert.That(HGFriendsServicesConnector.TryParseSuccess(parsed, out bool ok) && ok);
        }

        static byte[] Post(HGFriendsServerPostHandler handler, string body)
        {
            return handler.Handle("/hgfriends", new MemoryStream(Encoding.UTF8.GetBytes(body)), null, null);
        }

        static bool IsTrue(byte[] reply)
        {
            Dictionary<string, object> parsed = ServerUtils.ParseXmlResponse(Encoding.UTF8.GetString(reply));
            return HGFriendsServicesConnector.TryParseSuccess(parsed, out bool ok) && ok;
        }

        class FakeHGFriendsService : IHGFriendsService
        {
            public bool DeleteCalled;
            public string LastSecret;
            public bool DeleteResult;
            public bool NewFriendshipResult;

            public int GetFriendPerms(UUID userID, UUID friendID) { return -1; }
            public bool NewFriendship(FriendInfo finfo, bool verified) { return NewFriendshipResult; }
            public bool DeleteFriendship(FriendInfo finfo, string secret)
            {
                DeleteCalled = true;
                LastSecret = secret;
                return DeleteResult;
            }
            public bool FriendshipOffered(UUID from, string fromName, UUID to, string message) { return false; }
            public bool ValidateFriendshipOffered(UUID fromID, UUID toID) { return false; }
            public List<UUID> StatusNotification(List<string> friends, UUID userID, bool online) { return new List<UUID>(); }
        }
    }
}
