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
using OpenSim.Groups;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class UuidDhtGroupsServiceTests
    {
        private string m_dbFile;

        [SetUp]
        public void SetUp()
        {
            TestBin.UseOpenSimBin();
        }

        private IConfigSource MakeConfig(bool dhtEnabled)
        {
            m_dbFile = Path.Combine(Path.GetTempPath(), "uuid-dht-groups-" + Path.GetRandomFileName() + ".db");
            IniConfigSource source = new IniConfigSource();
            IConfig db = source.AddConfig("DatabaseService");
            db.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
            db.Set("ConnectionString", "URI=file:" + m_dbFile);
            IConfig groups = source.AddConfig("Groups");
            groups.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
            groups.Set("ConnectionString", "URI=file:" + m_dbFile);
            IConfig gridUser = source.AddConfig("GridUserService");
            gridUser.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
            gridUser.Set("ConnectionString", "URI=file:" + m_dbFile);
            IConfig dht = source.AddConfig("UuidDht");
            dht.Set("Enabled", dhtEnabled ? "true" : "false");
            return source;
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(m_dbFile) && File.Exists(m_dbFile))
                File.Delete(m_dbFile);
        }

        [Test]
        public void SqlHitDoesNotCallDht()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UuidDhtGroupsService svc = new UuidDhtGroupsService(MakeConfig(true), fake, fake);

            string reason;
            UUID groupID = svc.CreateGroup(UUID.Random().ToString(), "Local Group", "charter", true, UUID.Zero,
                0, true, true, false, UUID.Random(), out reason);
            Assert.That(groupID.IsZero(), Is.False, reason);

            ExtendedGroupRecord got = svc.GetGroupRecord(UUID.Zero.ToString(), groupID);
            Assert.That(got, Is.Not.Null);
            Assert.That(got.GroupName, Is.EqualTo("Local Group"));
            Assert.That(fake.GroupLookups, Is.EqualTo(0));
        }

        [Test]
        public void LocalGroupRecordDoesNotCallDhtOnSqlMiss()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UUID groupID = UUID.Random();
            fake.GroupHomes[groupID] = new UuidDhtHome
            {
                UserID = groupID,
                HomeURI = "http://192.0.2.55:8002/"
            };
            fake.HomeGroups[groupID] = new ExtendedGroupRecord
            {
                GroupID = groupID,
                GroupName = "Dht Group",
                Charter = "from home"
            };

            UuidDhtGroupsService svc = new UuidDhtGroupsService(MakeConfig(true), fake, fake);
            Assert.That(svc.GetLocalGroupRecord(UUID.Zero.ToString(), groupID), Is.Null);
            Assert.That(fake.GroupLookups, Is.EqualTo(0));
            Assert.That(svc.GetGroupRecord(UUID.Zero.ToString(), groupID), Is.Not.Null);
            Assert.That(fake.GroupLookups, Is.EqualTo(1));
        }

        [Test]
        public void SqlMissDhtHitReturnsGroup()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UUID groupID = UUID.Random();
            fake.GroupHomes[groupID] = new UuidDhtHome
            {
                UserID = groupID,
                HomeURI = "http://207.180.199.55:10001/"
            };
            fake.HomeGroups[groupID] = new ExtendedGroupRecord
            {
                GroupID = groupID,
                GroupName = "Dht Group",
                Charter = "from home"
            };

            UuidDhtGroupsService svc = new UuidDhtGroupsService(MakeConfig(true), fake, fake);
            ExtendedGroupRecord got = svc.GetGroupRecord(UUID.Zero.ToString(), groupID);

            Assert.That(got, Is.Not.Null);
            Assert.That(got.GroupName, Is.EqualTo("Dht Group"));
            Assert.That(got.ServiceLocation, Is.EqualTo("http://207.180.199.55:10001/"));
            Assert.That(fake.GroupLookups, Is.EqualTo(1));
            Assert.That(fake.HomeGroupLookups, Is.EqualTo(1));
        }

        [Test]
        public void SqlMissDhtHitHomeMissReturnsNull()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UUID groupID = UUID.Random();
            fake.GroupHomes[groupID] = new UuidDhtHome
            {
                UserID = groupID,
                HomeURI = "http://207.180.199.55:10001/"
            };
            UuidDhtGroupsService svc = new UuidDhtGroupsService(MakeConfig(true), fake, fake);
            Assert.That(svc.GetGroupRecord(UUID.Zero.ToString(), groupID), Is.Null);
            Assert.That(fake.HomeGroupLookups, Is.EqualTo(1));
        }

        [Test]
        public void SqlMissDhtMissReturnsNull()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UuidDhtGroupsService svc = new UuidDhtGroupsService(MakeConfig(true), fake, fake);

            ExtendedGroupRecord got = svc.GetGroupRecord(UUID.Zero.ToString(), UUID.Random());
            Assert.That(got, Is.Null);
            Assert.That(fake.GroupLookups, Is.EqualTo(1));
        }

        [Test]
        public void DisabledDoesNotCallDhtOnMiss()
        {
            FakeUuidDhtClient fake = new FakeUuidDhtClient();
            UuidDhtGroupsService svc = new UuidDhtGroupsService(MakeConfig(false), fake);

            ExtendedGroupRecord got = svc.GetGroupRecord(UUID.Zero.ToString(), UUID.Random());
            Assert.That(got, Is.Null);
            Assert.That(fake.GroupLookups, Is.EqualTo(0));
        }
    }
}
