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

using System.Data.SQLite;
using System.IO;
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class DhtLocalUuidSqlTests
    {
        private string m_dbFile;

        [SetUp]
        public void SetUp()
        {
            TestBin.UseOpenSimBin();
            m_dbFile = Path.Combine(Path.GetTempPath(), "uuid-dht-sql-" + Path.GetRandomFileName() + ".db");
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(m_dbFile) && File.Exists(m_dbFile))
                File.Delete(m_dbFile);
        }

        [Test]
        public void ListsUsersAndLocalGroupsSkipsForeignLocation()
        {
            UUID user = UUID.Random();
            UUID localGroup = UUID.Random();
            UUID foreignGroup = UUID.Random();
            using (SQLiteConnection conn = new SQLiteConnection("URI=file:" + m_dbFile))
            {
                conn.Open();
                using SQLiteCommand cmd = conn.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE UserAccounts (PrincipalID TEXT, ScopeID TEXT);"
                    + "CREATE TABLE os_groups_groups (GroupID TEXT, Location TEXT);"
                    + "INSERT INTO UserAccounts VALUES ('" + user + "','" + UUID.Zero + "');"
                    + "INSERT INTO os_groups_groups VALUES ('" + localGroup + "','');"
                    + "INSERT INTO os_groups_groups VALUES ('" + foreignGroup + "','http://192.0.2.9:8002/');";
                cmd.ExecuteNonQuery();
            }

            IniConfigSource src = new IniConfigSource();
            IConfig db = src.AddConfig("DatabaseService");
            db.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
            db.Set("ConnectionString", "URI=file:" + m_dbFile);
            src.AddConfig("UserAccountService");
            src.AddConfig("Groups");

            DhtLocalUuidSql sql = new DhtLocalUuidSql(src);
            Assert.That(sql.ListUserIds(), Does.Contain(user));
            Assert.That(sql.ListGroupIds(), Does.Contain(localGroup));
            Assert.That(sql.ListGroupIds(), Does.Not.Contain(foreignGroup));
        }
    }
}
