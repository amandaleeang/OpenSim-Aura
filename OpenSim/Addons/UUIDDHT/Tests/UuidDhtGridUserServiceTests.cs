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
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Services.Interfaces;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class UuidDhtGridUserServiceTests
    {
        private string m_DbFile;

        [SetUp]
        public void SetUp()
        {
            TestBin.UseOpenSimBin();
            if (MainConsole.Instance == null)
                MainConsole.Instance = new MockConsole();
            m_DbFile = Path.Combine(Path.GetTempPath(), "uuiddht-griduser-" + UUID.Random() + ".db");
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (m_DbFile != null && File.Exists(m_DbFile))
                    File.Delete(m_DbFile);
            }
            catch
            {
            }
        }

        private IConfigSource MakeConfig(bool dhtEnabled)
        {
            IniConfigSource source = new IniConfigSource();
            IConfig db = source.AddConfig("DatabaseService");
            db.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
            db.Set("ConnectionString", "URI=file:" + m_DbFile);
            IConfig gu = source.AddConfig("GridUserService");
            gu.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
            gu.Set("ConnectionString", "URI=file:" + m_DbFile);
            IConfig dht = source.AddConfig("UuidDht");
            dht.Set("Enabled", dhtEnabled ? "true" : "false");
            return source;
        }

        [Test]
        public void SqlHitDoesNotCallDht()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UUID id = UUID.Random();
            svcLoggedInAndDht(fake, id, out UuidDhtGridUserService svc);

            GridUserInfo info = svc.GetGridUserInfo(id.ToString());
            Assert.That(fake.GridUserLookups, Is.EqualTo(0));
            Assert.That(fake.NameLookups, Is.EqualTo(0));
            Assert.That(info, Is.Not.Null);
            Assert.That(info.UserID, Is.EqualTo(id.ToString()));
        }

        [Test]
        public void SqlMissQueriesDht()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UUID id = UUID.Random();
            fake.Homes[id] = new UuidDhtHome
            {
                UserID = id,
                HomeURI = "http://207.180.199.55:10001/"
            };
            fake.Names[id] = new UuidDhtHomeName
            {
                FirstName = "Amanda",
                LastName = "Lee",
                DisplayName = "Amanda"
            };
            UuidDhtGridUserService svc = new UuidDhtGridUserService(MakeConfig(true), fake, fake);

            GridUserInfo info = svc.GetGridUserInfo(id.ToString());
            Assert.That(fake.GridUserLookups, Is.EqualTo(1));
            Assert.That(fake.NameLookups, Is.EqualTo(1));
            Assert.That(info, Is.Not.Null);
            Assert.That(Util.ParseFullUniversalUserIdentifier(info.UserID, out UUID parsed, out string url, out string first, out string last), Is.True);
            Assert.That(parsed, Is.EqualTo(id));
            Assert.That(first, Is.EqualTo("Amanda"));
            Assert.That(last, Is.EqualTo("Lee"));
            Assert.That(url.Contains("10001"), Is.True);
        }

        [Test]
        public void BatchSqlHitsDoNotCallDhtAndMissesAreCapped()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UuidDhtGridUserService svc = new UuidDhtGridUserService(MakeConfig(true), fake, fake);

            UUID sqlId = UUID.Random();
            svc.LoggedIn(sqlId.ToString());

            UUID[] missIds = new UUID[5];
            string[] ids = new string[6];
            ids[0] = sqlId.ToString();
            for (int i = 0; i < missIds.Length; i++)
            {
                missIds[i] = UUID.Random();
                ids[i + 1] = missIds[i].ToString();
                fake.Homes[missIds[i]] = new UuidDhtHome
                {
                    UserID = missIds[i],
                    HomeURI = "http://207.180.199.55:10001/"
                };
                fake.Names[missIds[i]] = new UuidDhtHomeName
                {
                    FirstName = "Amanda",
                    LastName = "Lee"
                };
            }

            GridUserInfo[] infos = svc.GetGridUserInfo(ids);
            Assert.That(infos.Length, Is.EqualTo(6));
            Assert.That(infos[0], Is.Not.Null);
            Assert.That(infos[0].UserID, Is.EqualTo(sqlId.ToString()));
            Assert.That(fake.GridUserLookups, Is.EqualTo(4));

            int dhtHits = 0;
            int remainingNull = 0;
            for (int i = 1; i < infos.Length; i++)
            {
                if (infos[i] != null)
                    dhtHits++;
                else
                    remainingNull++;
            }
            Assert.That(dhtHits, Is.EqualTo(4));
            Assert.That(remainingNull, Is.EqualTo(1));
        }

        [Test]
        public void OwnHomeAfterDhtHitUsesLocalAccountNotGetUserInfo()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UUID id = UUID.Random();
            string home = "http://192.0.2.10:8002/";
            fake.Homes[id] = new UuidDhtHome { UserID = id, HomeURI = home };
            fake.Names[id] = new UuidDhtHomeName { FirstName = "Should", LastName = "NotUse" };

            IniConfigSource source = (IniConfigSource)MakeConfig(true);
            source.Configs["UuidDht"].Set("HomeURI", home);
            source.Configs["UuidDht"].Set("SeedsPath", Path.Combine(Path.GetTempPath(), "uuiddht-seeds-none-" + UUID.Random()));

            FakeLocalUsers users = new FakeLocalUsers();
            users.Accounts[id] = new UserAccount(UUID.Zero, id, "Amanda", "Lee", "");
            UuidDhtGridUserService svc = new UuidDhtGridUserService(source, fake, fake, users);

            GridUserInfo info = svc.GetGridUserInfo(id.ToString());
            Assert.That(fake.NameLookups, Is.EqualTo(0), "own HomeURI must not HTTP GetUserInfo");
            Assert.That(info, Is.Not.Null);
            Assert.That(Util.ParseFullUniversalUserIdentifier(info.UserID, out UUID parsed, out _, out string first, out string last), Is.True);
            Assert.That(parsed, Is.EqualTo(id));
            Assert.That(first, Is.EqualTo("Amanda"));
            Assert.That(last, Is.EqualTo("Lee"));
        }

        [Test]
        public void GetUserInfoMissReturnsNullLikeUnknownUser()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UUID id = UUID.Random();
            fake.Homes[id] = new UuidDhtHome { UserID = id, HomeURI = "http://207.180.199.55:10001/" };
            UuidDhtGridUserService svc = new UuidDhtGridUserService(MakeConfig(true), fake, fake);

            GridUserInfo info = svc.GetGridUserInfo(id.ToString());
            Assert.That(fake.NameLookups, Is.EqualTo(1));
            Assert.That(info, Is.Null);
        }

        [Test]
        public void UnknownUuidReturnsNullLikeGridUserMiss()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UuidDhtGridUserService svc = new UuidDhtGridUserService(MakeConfig(true), fake, fake);
            GridUserInfo info = svc.GetGridUserInfo(UUID.Random().ToString());
            Assert.That(info, Is.Null);
            Assert.That(fake.GridUserLookups, Is.EqualTo(1));
        }

        [Test]
        public void DisabledUsesGridUserSql()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UuidDhtGridUserService svc = new UuidDhtGridUserService(MakeConfig(false), fake);
            UUID id = UUID.Random();
            svc.LoggedIn(id.ToString());

            GridUserInfo info = svc.GetGridUserInfo(id.ToString());
            Assert.That(info, Is.Not.Null);
            Assert.That(fake.GridUserLookups, Is.EqualTo(0));
        }

        private void svcLoggedInAndDht(FakeUuidDhtClient fake, UUID id, out UuidDhtGridUserService svc)
        {
            fake.Homes[id] = new UuidDhtHome
            {
                UserID = id,
                HomeURI = "http://207.180.199.55:10001/"
            };
            fake.Names[id] = new UuidDhtHomeName
            {
                FirstName = "Amanda",
                LastName = "Lee",
                DisplayName = "Amanda"
            };
            svc = new UuidDhtGridUserService(MakeConfig(true), fake, fake);
            svc.LoggedIn(id.ToString());
        }

        private sealed class FakeLocalUsers : IUserAccountService
        {
            public Dictionary<UUID, UserAccount> Accounts = new Dictionary<UUID, UserAccount>();

            public UserAccount GetUserAccount(UUID scopeID, UUID userID)
            {
                Accounts.TryGetValue(userID, out UserAccount a);
                return a;
            }

            public UserAccount GetUserAccount(UUID scopeID, string FirstName, string LastName)
            {
                return null;
            }

            public UserAccount GetUserAccount(UUID scopeID, string Email)
            {
                return null;
            }

            public List<UserAccount> GetUserAccounts(UUID scopeID, string query)
            {
                return new List<UserAccount>();
            }

            public List<UserAccount> GetUserAccountsWhere(UUID scopeID, string where)
            {
                return new List<UserAccount>();
            }

            public List<UserAccount> GetUserAccounts(UUID scopeID, List<string> IDs)
            {
                return new List<UserAccount>();
            }

            public bool StoreUserAccount(UserAccount data)
            {
                return false;
            }

            public void InvalidateCache(UUID userID)
            {
            }
        }
    }
}
