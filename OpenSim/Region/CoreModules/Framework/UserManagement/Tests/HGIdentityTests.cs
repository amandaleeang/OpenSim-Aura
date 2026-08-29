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
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.Framework.UserManagement.Tests
{
    [TestFixture]
    public class HGIdentityTests : OpenSimTestCase
    {
        UserManagementModule m_um;
        TestScene m_scene;

        [SetUp]
        public void SetUp()
        {
            m_um = new UserManagementModule();
            IConfigSource config = new IniConfigSource();
            config.AddConfig("Modules");
            config.Configs["Modules"].Set("UserManagementModule", m_um.Name);
            m_scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(m_scene, config, m_um);
        }

        [Test]
        public void ResolveFriendsServerURI_PrefersCircuitFriendsThenHome()
        {
            TestHelpers.InMethod();

            UUID id = TestHelpers.ParseTail(0x61);
            AgentCircuitData circuit = SceneHelpers.GenerateAgentData(id);
            circuit.ServiceURLs["FriendsServerURI"] = "http://friends.example:8002/";
            circuit.ServiceURLs["HomeURI"] = "http://home.example:8002/";

            string friends = HGIdentity.ResolveFriendsServerURI(null, null, id, circuit);
            Assert.That(friends.Contains("friends.example"), Is.True, friends);

            circuit.ServiceURLs.Remove("FriendsServerURI");
            string home = HGIdentity.ResolveFriendsServerURI(null, null, id, circuit);
            Assert.That(home.Contains("home.example"), Is.True, home);
        }

        [Test]
        public void ResolveHomeURI_UsesUserManagementThenFailsIfUnknown()
        {
            TestHelpers.InMethod();

            UUID known = TestHelpers.ParseTail(0x62);
            UUID unknown = TestHelpers.ParseTail(0x63);
            m_um.AddUser(known, "Pat", "Visitor", "https://cached.example:8002/");

            string home = HGIdentity.ResolveHomeURI(m_scene, m_um, known);
            Assert.That(home.Contains("cached.example"), Is.True, home);

            Assert.That(HGIdentity.ResolveHomeURI(m_scene, m_um, unknown), Is.EqualTo(string.Empty));
        }

        [Test]
        public void TryResolveUUI_UserManagementBeforeGetUui()
        {
            TestHelpers.InMethod();

            UUID requester = TestHelpers.ParseTail(0x64);
            UUID target = TestHelpers.ParseTail(0x65);
            m_um.AddUser(target, "Tgt", "User", "http://target.example:8002/");

            bool called = false;
            bool ok = HGIdentity.TryResolveUUI(m_scene, m_um, requester, target, out string uui,
                (home, from, to) => { called = true; return string.Empty; });

            Assert.That(ok, Is.True);
            Assert.That(HGIdentity.IsFullUui(uui), Is.True, uui);
            Assert.That(called, Is.False, "get_uui must not run when UserManagement already has a full UUI");
        }

        [Test]
        public void TryResolveUUI_GetUuiWhenUnknownThenRemember()
        {
            TestHelpers.InMethod();

            UUID requester = TestHelpers.ParseTail(0x66);
            UUID target = TestHelpers.ParseTail(0x67);
            m_um.AddUser(requester, "Req", "User", "http://req.example:8002/");

            string remote = target + ";http://remote.example:8002/;Rem User;secret00";
            bool ok = HGIdentity.TryResolveUUI(m_scene, m_um, requester, target, out string uui,
                (home, from, to) =>
                {
                    Assert.That(from, Is.EqualTo(requester));
                    Assert.That(to, Is.EqualTo(target));
                    Assert.That(home.Contains("req.example"), Is.True, home);
                    return remote;
                });

            Assert.That(ok, Is.True);
            Assert.That(uui.Contains("remote.example"), Is.True, uui);
            Assert.That(uui.Contains("secret00"), Is.False, "secret must be stripped");
            Assert.That(m_um.GetUserHomeURL(target).Contains("remote.example"), Is.True);
        }

        [Test]
        public void TryResolveUUI_FailsIfUnknown()
        {
            TestHelpers.InMethod();

            UUID requester = TestHelpers.ParseTail(0x68);
            UUID target = TestHelpers.ParseTail(0x69);

            bool ok = HGIdentity.TryResolveUUI(m_scene, m_um, requester, target, out string uui,
                (home, from, to) => string.Empty);
            Assert.That(ok, Is.False);
            Assert.That(uui, Is.EqualTo(string.Empty));
        }

        [Test]
        public void GetUserServerURL_FriendsServerURIFallsBackToHomeUrl()
        {
            TestHelpers.InMethod();

            UUID userId = TestHelpers.ParseTail(0x6a);
            m_um.AddUser(userId, "Pat", "Visitor", "https://home.example:8002/");
            UserData ud = m_um.GetUserData(userId);
            Assert.That(ud, Is.Not.Null);
            ud.ServerURLs = new Dictionary<string, object>();

            string url = m_um.GetUserServerURL(userId, "FriendsServerURI");
            Assert.That(string.IsNullOrEmpty(url), Is.False);
            Assert.That(url.Contains("home.example"), Is.True, url);
        }

        [Test]
        public void RememberContact_SeedsUserManagement()
        {
            TestHelpers.InMethod();

            UUID id = TestHelpers.ParseTail(0x6b);
            HGIdentity.RememberContact(m_scene, m_um, id, "Ann", "Bee", "https://ann.example:8002/");
            string home = m_um.GetUserHomeURL(id);
            Assert.That(home.StartsWith("https://"), Is.True, home);
            Assert.That(home.Contains("ann.example"), Is.True, home);
        }
    }
}
