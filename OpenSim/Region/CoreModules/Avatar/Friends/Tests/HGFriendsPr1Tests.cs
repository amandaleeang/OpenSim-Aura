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
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Data.Null;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Connectors.Hypergrid;
using OpenSim.Services.HypergridService;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

namespace OpenSim.Region.CoreModules.Avatar.Friends.Tests
{
    [TestFixture]
    public class HGFriendsPr1Tests : OpenSimTestCase
    {
        private FriendsModule m_fm;
        private TestScene m_scene;

        [TestFixtureSetUp]
        public void FixtureInit()
        {
            Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        }

        [TestFixtureTearDown]
        public void TearDown()
        {
            Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        }

        [SetUp]
        public void Init()
        {
            NullFriendsData.Clear();

            IConfigSource config = new IniConfigSource();
            config.AddConfig("Modules");
            config.Configs["Modules"].Set("FriendsModule", "FriendsModule");
            config.AddConfig("Friends");
            config.Configs["Friends"].Set("Connector", "OpenSim.Services.FriendsService.dll");
            config.AddConfig("FriendsService");
            config.Configs["FriendsService"].Set("StorageProvider", "OpenSim.Data.Null.dll");

            m_scene = new SceneHelpers().SetupScene();
            m_fm = new FriendsModule();
            SceneHelpers.SetupSceneModules(m_scene, config, m_fm);
        }

        [Test]
        public void TestGetFriendsStringReturnsUuidAndUuiRows()
        {
            TestHelpers.InMethod();

            UUID principal = TestHelpers.ParseTail(0x10);
            UUID friendUuid = TestHelpers.ParseTail(0x11);
            string friendUui = friendUuid + ";http://grid.example:8002/;Alice Bob;abcd1234";

            m_fm.FriendsService.StoreFriend(principal.ToString(), friendUuid.ToString(), 1);
            m_fm.FriendsService.StoreFriend(principal.ToString(), friendUui, 0);

            FriendInfo[] got = m_fm.FriendsService.GetFriends(principal.ToString());
            Assert.That(got.Length, Is.EqualTo(2));

            FriendInfo uuidRow = System.Array.Find(got, f => f.Friend == friendUuid.ToString());
            FriendInfo uuiRow = System.Array.Find(got, f => f.Friend == friendUui);
            Assert.That(uuidRow, Is.Not.Null);
            Assert.That(uuiRow, Is.Not.Null);
            Assert.That(uuidRow.PrincipalID, Is.EqualTo(principal));
            Assert.That(uuiRow.PrincipalID, Is.EqualTo(principal));
        }

        [Test]
        public void TestGetFriendsStringSkipsEmptyFriend()
        {
            TestHelpers.InMethod();

            UUID principal = TestHelpers.ParseTail(0x12);
            m_fm.FriendsService.StoreFriend(principal.ToString(), string.Empty, 0);
            m_fm.FriendsService.StoreFriend(principal.ToString(), TestHelpers.ParseTail(0x13).ToString(), 1);

            FriendInfo[] got = m_fm.FriendsService.GetFriends(principal.ToString());
            Assert.That(got.Length, Is.EqualTo(1));
            Assert.That(got[0].Friend, Is.Not.EqualTo(string.Empty));
        }

        [Test]
        public void TestGetFriendsStringUuiPrincipalDoesNotThrow()
        {
            TestHelpers.InMethod();

            UUID principal = TestHelpers.ParseTail(0x14);
            UUID friend = TestHelpers.ParseTail(0x15);
            string principalUui = principal + ";http://home.example:8002/;Pat User";

            m_fm.FriendsService.StoreFriend(principalUui, friend.ToString(), 0);

            FriendInfo[] got = null;
            Assert.DoesNotThrow(() => got = m_fm.FriendsService.GetFriends(principalUui));
            Assert.That(got, Is.Not.Null);
            Assert.That(got.Length, Is.EqualTo(1));
            Assert.That(got[0].PrincipalID, Is.EqualTo(principal));
            Assert.That(got[0].Friend, Is.EqualTo(friend.ToString()));
            Assert.That(got[0].TheirFlags, Is.EqualTo(-1));
        }

        [Test]
        public void TestResolveOffererNamePrefersFromName()
        {
            TestHelpers.InMethod();

            Assert.That(
                FriendsSimpleRequestHandler.ResolveOffererName("Alice.Bob@grid.example:8002", null),
                Is.EqualTo("Alice.Bob@grid.example:8002"));

            UserAccount acc = new UserAccount { FirstName = "Local", LastName = "User" };
            Assert.That(FriendsSimpleRequestHandler.ResolveOffererName("", acc), Is.EqualTo("Local User"));
            Assert.That(FriendsSimpleRequestHandler.ResolveOffererName(null, null), Is.EqualTo("Unknown"));
        }

        [Test]
        public void TestNewFriendshipReplyParse()
        {
            TestHelpers.InMethod();

            Assert.That(HGFriendsServicesConnector.TryParseSuccess(
                new Dictionary<string, object> { ["RESULT"] = "True" }, out bool a) && a);
            Assert.That(HGFriendsServicesConnector.TryParseSuccess(
                new Dictionary<string, object> { ["Result"] = "Success" }, out bool b) && b);
            Assert.That(HGFriendsServicesConnector.TryParseSuccess(
                new Dictionary<string, object> { ["RESULT"] = "False" }, out bool c) && !c);
            Assert.That(!HGFriendsServicesConnector.TryParseSuccess(null, out _));
        }

        [Test]
        public void TestResolveOffererHomeUriDoesNotRewriteHttps()
        {
            TestHelpers.InMethod();

            Assert.That(
                HGFriendsService.TryResolveOffererHomeURI("Alice.Bob@https://grid.example:8002", out string httpsHome));
            Assert.That(httpsHome.StartsWith("https://"), Is.True, httpsHome);

            Assert.That(
                HGFriendsService.TryResolveOffererHomeURI("Alice.Bob@grid.example:8002", out string httpHome));
            Assert.That(httpHome.Contains("grid.example"), Is.True, httpHome);
            Assert.That(HGFriendsService.TryResolveOffererHomeURI("NoAtSign", out _), Is.False);
        }

        [Test]
        public void TestDeletePreviousHGRelationsDeletesBothSides()
        {
            TestHelpers.InMethod();

            UUID a1 = TestHelpers.ParseTail(0x21);
            UUID a2 = TestHelpers.ParseTail(0x22);
            string a2uui = a2 + ";http://other.example:8002/;Other User;deadbeef";
            string a1uui = a1 + ";http://other.example:8002/;This User;deadbeef";

            TestHGFriendsModule hg = CreateHgModule();

            hg.FriendsService.StoreFriend(a1.ToString(), a2uui, 0);
            hg.FriendsService.StoreFriend(a2.ToString(), a1uui, 0);

            hg.SeedCache(a1, new FriendInfo[]
            {
                new FriendInfo { PrincipalID = a1, Friend = a2uui, MyFlags = 0, TheirFlags = -1 }
            });
            hg.SeedCache(a2, new FriendInfo[]
            {
                new FriendInfo { PrincipalID = a2, Friend = a1uui, MyFlags = 0, TheirFlags = -1 }
            });

            hg.CallDeletePreviousHGRelations(a1, a2);

            Assert.That(hg.FriendsService.GetFriends(a1.ToString()).Length, Is.EqualTo(0));
            Assert.That(hg.FriendsService.GetFriends(a2.ToString()).Length, Is.EqualTo(0));
        }

        [Test]
        public void TestLocalFriendshipOfferedUsesHttpsHomeUri()
        {
            TestHelpers.InMethod();

            UserManagementModule uman = new UserManagementModule();
            IConfigSource config = new IniConfigSource();
            config.AddConfig("Modules");
            config.Configs["Modules"].Set("UserManagementModule", uman.Name);
            config.Configs["Modules"].Set("FriendsModule", "HGFriendsModule");
            config.AddConfig("Friends");
            config.Configs["Friends"].Set("Connector", "OpenSim.Services.FriendsService.dll");
            config.AddConfig("FriendsService");
            config.Configs["FriendsService"].Set("StorageProvider", "OpenSim.Data.Null.dll");

            NullFriendsData.Clear();
            TestScene scene = new SceneHelpers().SetupScene();
            TestHGFriendsModule hg = new TestHGFriendsModule();
            SceneHelpers.SetupSceneModules(scene, config, uman, hg);

            UUID fromId = TestHelpers.ParseTail(0x31);
            UUID toId = TestHelpers.ParseTail(0x32);
            SceneHelpers.AddScenePresence(scene, toId);

            GridInstantMessage im = new GridInstantMessage(
                scene, fromId, "Alice.Bob@grid.example:8002", toId,
                (byte)InstantMessageDialog.FriendshipOffered, "hi", false, Vector3.Zero);
            im.fromAgentHomeURI = "https://grid.example:8002/";

            Assert.That(hg.LocalFriendshipOffered(toId, im), Is.True);
            string home = uman.GetUserHomeURL(fromId);
            Assert.That(home.StartsWith("https://"), Is.True, home);
        }

        [Test]
        public void TestFriendsServerUriFallsBackToHomeUrl()
        {
            TestHelpers.InMethod();

            UserManagementModule uman = new UserManagementModule();
            IConfigSource config = new IniConfigSource();
            config.AddConfig("Modules");
            config.Configs["Modules"].Set("UserManagementModule", uman.Name);

            TestScene scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(scene, config, uman);

            UUID userId = TestHelpers.ParseTail(0x41);
            uman.AddUser(userId, "Pat", "Visitor", "https://home.example:8002/");
            UserData ud = uman.GetUserData(userId);
            Assert.That(ud, Is.Not.Null);
            ud.ServerURLs = new Dictionary<string, object>();

            string url = uman.GetUserServerURL(userId, "FriendsServerURI");
            Assert.That(string.IsNullOrEmpty(url), Is.False);
            Assert.That(url.Contains("home.example"), Is.True, url);
        }

        TestHGFriendsModule CreateHgModule()
        {
            IConfigSource config = new IniConfigSource();
            config.AddConfig("Modules");
            config.Configs["Modules"].Set("FriendsModule", "HGFriendsModule");
            config.AddConfig("Friends");
            config.Configs["Friends"].Set("Connector", "OpenSim.Services.FriendsService.dll");
            config.AddConfig("FriendsService");
            config.Configs["FriendsService"].Set("StorageProvider", "OpenSim.Data.Null.dll");

            TestScene scene = new SceneHelpers().SetupScene();
            TestHGFriendsModule hg = new TestHGFriendsModule();
            SceneHelpers.SetupSceneModules(scene, config, hg);
            return hg;
        }

        class TestHGFriendsModule : HGFriendsModule
        {
            public void SeedCache(UUID user, FriendInfo[] friends)
            {
                m_Friends[user] = new UserFriendData
                {
                    PrincipalID = user,
                    Friends = friends,
                    Refcount = 1
                };
            }

            public void CallDeletePreviousHGRelations(UUID a1, UUID a2)
            {
                DeletePreviousHGRelations(a1, a2);
            }
        }
    }
}
