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
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class DhtPublisherTests
    {
        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            TestBin.UseOpenSimBin();
            m_Dir = Path.Combine(Path.GetTempPath(), "uuiddht-pub-" + UUID.Random());
            Directory.CreateDirectory(m_Dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(m_Dir, true); } catch { }
        }

        [Test]
        public void PublishStoresUuidRecord()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.80:8002/", t, 100000);
            Assert.That(a.Node.Join(), Is.True);

            UUID u = UUID.Random();
            FakeUsers users = new FakeUsers();
            users.Ids.Add(u);
            a.Node.SetUserAccountService(users);

            Assert.That(a.Node.Publisher.PublishUuid(u), Is.True);
            Assert.That(a.Store.TryGet(DhtKey.ForUuid(u), out DhtRecord rec), Is.True);
            Assert.That(rec.Tombstone, Is.False);
            Assert.That(rec.NodeId, Is.EqualTo(a.Id.NodeId.ToHex()));
            Assert.That(a.Node.Publisher.PublishedCount, Is.EqualTo(1));
        }

        [Test]
        public void DoesNotPublishWellKnownServiceAgents()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("svc", "http://192.0.2.85:8002/", t, 100000);
            Assert.That(a.Node.Join(), Is.True);

            FakeUsers users = new FakeUsers();
            users.Ids.Add(Constants.servicesGodAgentID);
            users.Ids.Add(Constants.m_MrOpenSimID);
            a.Node.SetUserAccountService(users);
            a.Node.SetLocalUuidSource(users);

            Assert.That(a.Node.Publisher.PublishUuid(Constants.servicesGodAgentID), Is.False);
            Assert.That(a.Node.Publisher.Backfill(), Is.EqualTo(0));
            Assert.That(a.Store.TryGet(DhtKey.ForUuid(Constants.servicesGodAgentID), out _), Is.False);
        }

        [Test]
        public void DoesNotPublishUnowned()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.81:8002/", t, 100000);
            Assert.That(a.Node.Join(), Is.True);
            a.Node.SetUserAccountService(new FakeUsers());

            UUID u = UUID.Random();
            Assert.That(a.Node.Publisher.PublishUuid(u), Is.False);
            Assert.That(a.Store.TryGet(DhtKey.ForUuid(u), out _), Is.False);
        }

        [Test]
        public void UnpublishTombs()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.82:8002/", t, 100000);
            Assert.That(a.Node.Join(), Is.True);

            UUID u = UUID.Random();
            FakeUsers users = new FakeUsers();
            users.Ids.Add(u);
            a.Node.SetUserAccountService(users);

            Assert.That(a.Node.Publisher.PublishUuid(u), Is.True);
            Assert.That(a.Node.Publisher.UnpublishUuid(u), Is.True);
            Assert.That(a.Store.TryGet(DhtKey.ForUuid(u), out DhtRecord rec), Is.True);
            Assert.That(rec.Tombstone, Is.True);
            Assert.That(a.Node.Publisher.PublishedCount, Is.EqualTo(0));
        }

        [Test]
        public void BackfillUsesLocalUserIdsNotNameSearch()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.83:8002/", t, 100000);
            Assert.That(a.Node.Join(), Is.True);

            UUID u = UUID.Random();
            FakeUsers users = new FakeUsers();
            users.Ids.Add(u);
            a.Node.SetUserAccountService(users);
            a.Node.SetLocalUuidSource(users);

            Assert.That(a.Node.Publisher.Backfill(), Is.EqualTo(1));
            Assert.That(users.NameSearchCalls, Is.EqualTo(0));
            Assert.That(a.Store.TryGet(DhtKey.ForUuid(u), out DhtRecord rec), Is.True);
            Assert.That(rec.Tombstone, Is.False);
            Assert.That(a.Node.Publisher.Backfill(), Is.EqualTo(0));
        }

        [Test]
        public void StopsAtMaxPublishUuids()
        {
            DhtFakeTransport t = new DhtFakeTransport();
            using NodePack a = Make("a", "http://192.0.2.84:8002/", t, 1);
            Assert.That(a.Node.Join(), Is.True);

            UUID u1 = UUID.Random();
            UUID u2 = UUID.Random();
            FakeUsers users = new FakeUsers();
            users.Ids.Add(u1);
            users.Ids.Add(u2);
            a.Node.SetUserAccountService(users);
            a.Node.SetLocalUuidSource(users);

            Assert.That(a.Node.Publisher.Backfill(), Is.EqualTo(1));
            Assert.That(a.Node.Publisher.PublishedCount, Is.EqualTo(1));
        }

        private NodePack Make(string name, string home, DhtFakeTransport t, int maxPublish)
        {
            DhtIdentity id = DhtIdentity.LoadOrCreate(Path.Combine(m_Dir, name + ".json"), null);
            SqliteDhtStore store = new SqliteDhtStore(Path.Combine(m_Dir, name + ".db"));
            UuidDhtConfig cfg = new UuidDhtConfig
            {
                Enabled = true,
                HomeURI = home,
                IdentityPath = Path.Combine(m_Dir, name + ".json"),
                StorePath = Path.Combine(m_Dir, name + ".db"),
                K = 3,
                SiblingSize = 3,
                MaxRpcBytes = 32768,
                RpcTimeoutMs = 300,
                LookupTimeoutMs = 1500,
                CallbackTimeoutMs = 500,
                Alpha = 3,
                DisjointPaths = 3,
                AllowPrivateHomeURI = false,
                MaxPublishUuids = maxPublish,
                Bootstrap = Array.Empty<string>()
            };
            UuidDhtNode node = UuidDhtNode.CreateForTests(cfg, id, store, t, t);
            t.Add(home, node);
            return new NodePack { Node = node, Id = id, Home = home, Store = store };
        }

        private sealed class NodePack : IDisposable
        {
            public UuidDhtNode Node;
            public DhtIdentity Id;
            public string Home;
            public IDhtStore Store;

            public void Dispose()
            {
                Store?.Dispose();
            }
        }

        private sealed class FakeUsers : IUserAccountService, IDhtLocalUuidSource
        {
            public HashSet<UUID> Ids = new HashSet<UUID>();
            public int NameSearchCalls;

            public UserAccount GetUserAccount(UUID scopeID, UUID userID)
            {
                return Ids.Contains(userID) ? new UserAccount(userID) : null;
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
                NameSearchCalls++;
                throw new InvalidOperationException("GetUserAccounts is not list-all");
            }

            public List<UserAccount> GetUserAccountsWhere(UUID scopeID, string where)
            {
                throw new InvalidOperationException("GetUserAccountsWhere is not list-all");
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

            public IList<UUID> ListUserIds()
            {
                return new List<UUID>(Ids);
            }

            public IList<UUID> ListGroupIds()
            {
                return Array.Empty<UUID>();
            }
        }
    }
}
