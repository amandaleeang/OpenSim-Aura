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

namespace OpenSim.Addons.UUIDDHT.Tests
{
    [TestFixture]
    public class DhtSeedFileTests
    {
        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            m_Dir = Path.Combine(Path.GetTempPath(), "uuiddht-seeds-" + UUID.Random());
            Directory.CreateDirectory(m_Dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(m_Dir, true); } catch { }
        }

        [Test]
        public void CollectPutsSelfFirstAndSkipsLoopback()
        {
            string self = "http://192.0.2.10:8002/";
            var list = DhtSeedFile.Collect(
                self,
                new[] { "http://127.0.0.1:8002/", "http://192.0.2.11:8002/" },
                new[] { self, "http://192.0.2.11:8002/" },
                false,
                32);
            Assert.That(list.Count, Is.EqualTo(2));
            Assert.That(list[0], Is.EqualTo(self));
            Assert.That(list[1], Is.EqualTo("http://192.0.2.11:8002/"));
        }

        [Test]
        public void WriteThenReadOneUriPerLine()
        {
            string path = Path.Combine(m_Dir, "dht-seeds");
            string[] want = { "http://192.0.2.10:8002/", "http://192.0.2.11:8002/" };
            Assert.That(DhtSeedFile.Write(path, want), Is.True);
            Assert.That(DhtSeedFile.Write(path, want), Is.False);
            string text = File.ReadAllText(path);
            Assert.That(text, Does.Contain("# UUID-DHT"));
            Assert.That(DhtSeedFile.Read(path), Is.EqualTo(want));
        }

        [Test]
        public void ReadSkipsCommentsAndBlanks()
        {
            string path = Path.Combine(m_Dir, "dht-seeds");
            File.WriteAllText(path,
                "# comment\n;; also comment\n\nhttp://192.0.2.10:8002/\n\"http://192.0.2.11:8002/\"\n");
            string[] got = DhtSeedFile.Read(path);
            Assert.That(got, Is.EqualTo(new[] { "http://192.0.2.10:8002/", "http://192.0.2.11:8002/" }));
        }

        [Test]
        public void FromMergesIniBootstrapAndSeedsFileSelfFirst()
        {
            string path = Path.Combine(m_Dir, "dht-seeds");
            File.WriteAllText(path, "http://192.0.2.12:8002/\n");
            IniConfigSource src = new IniConfigSource();
            IConfig dht = src.AddConfig("UuidDht");
            dht.Set("Enabled", true);
            dht.Set("HomeURI", "http://192.0.2.10:8002/");
            dht.Set("Bootstrap", "http://192.0.2.11:8002/");
            dht.Set("SeedsPath", path);
            dht.Set("AllowPrivateHomeURI", false);

            UuidDhtConfig cfg = UuidDhtConfig.From(src);
            Assert.That(cfg.SeedsPath, Is.EqualTo(path));
            Assert.That(cfg.Bootstrap[0], Is.EqualTo("http://192.0.2.10:8002/"));
            Assert.That(cfg.Bootstrap, Does.Contain("http://192.0.2.11:8002/"));
            Assert.That(cfg.Bootstrap, Does.Contain("http://192.0.2.12:8002/"));
        }

        [Test]
        public void EnsureFromExampleCopiesWhenMissingAndDoesNotOverwrite()
        {
            string path = Path.Combine(m_Dir, "dht-seeds");
            string example = path + ".example";
            File.WriteAllText(example, "http://192.0.2.80:8002/\nhttp://192.0.2.81:8002/\n");
            Assert.That(DhtSeedFile.EnsureFromExample(path), Is.True);
            Assert.That(DhtSeedFile.Read(path), Is.EqualTo(new[] { "http://192.0.2.80:8002/", "http://192.0.2.81:8002/" }));
            File.WriteAllText(path, "http://192.0.2.99:8002/\n");
            Assert.That(DhtSeedFile.EnsureFromExample(path), Is.False);
            Assert.That(DhtSeedFile.Read(path), Is.EqualTo(new[] { "http://192.0.2.99:8002/" }));
        }

        [Test]
        public void FromCopiesExampleSeedsWhenFileMissing()
        {
            string path = Path.Combine(m_Dir, "dht-seeds");
            File.WriteAllText(path + ".example", "http://192.0.2.80:8002/\n");
            IniConfigSource src = new IniConfigSource();
            IConfig dht = src.AddConfig("UuidDht");
            dht.Set("Enabled", true);
            dht.Set("HomeURI", "http://192.0.2.10:8002/");
            dht.Set("SeedsPath", path);
            UuidDhtConfig cfg = UuidDhtConfig.From(src);
            Assert.That(File.Exists(path), Is.True);
            Assert.That(cfg.Bootstrap, Does.Contain("http://192.0.2.80:8002/"));
            Assert.That(cfg.Bootstrap[0], Is.EqualTo("http://192.0.2.10:8002/"));
        }

        [Test]
        public void FromDefaultsAreWanTimeoutsAnd36HourExpire()
        {
            string path = Path.Combine(m_Dir, "no-such-seeds");
            IniConfigSource src = new IniConfigSource();
            IConfig dht = src.AddConfig("UuidDht");
            dht.Set("Enabled", true);
            dht.Set("SeedsPath", path);
            UuidDhtConfig cfg = UuidDhtConfig.From(src);
            Assert.That(cfg.RpcTimeoutMs, Is.EqualTo(UuidDhtConfig.DefaultRpcTimeoutMs));
            Assert.That(cfg.LookupTimeoutMs, Is.EqualTo(UuidDhtConfig.DefaultLookupTimeoutMs));
            Assert.That(cfg.ClientLookupCapMs, Is.EqualTo(UuidDhtConfig.DefaultClientLookupCapMs));
            Assert.That(cfg.CallbackTimeoutMs, Is.EqualTo(UuidDhtConfig.DefaultCallbackTimeoutMs));
            Assert.That(cfg.ExpireHours, Is.EqualTo(36));
            Assert.That(cfg.RpcTimeoutMs, Is.EqualTo(2000));
        }

        [Test]
        public void FromNormalizesHomeUriTrailingSlash()
        {
            IniConfigSource src = new IniConfigSource();
            IConfig hg = src.AddConfig("Hypergrid");
            hg.Set("HomeURI", "http://192.0.2.10:8002");
            IConfig dht = src.AddConfig("UuidDht");
            dht.Set("Enabled", true);

            UuidDhtConfig cfg = UuidDhtConfig.From(src);
            Assert.That(cfg.HomeURI, Is.EqualTo("http://192.0.2.10:8002/"));
        }

        [Test]
        public void LocalServiceModuleImpliesEnabledWithoutFlag()
        {
            IniConfigSource src = new IniConfigSource();
            IConfig gridUser = src.AddConfig("GridUserService");
            gridUser.Set("LocalServiceModule", "OpenSim.Addons.UUIDDHT.dll:UuidDhtGridUserService");
            Assert.That(UuidDhtConfig.IsEnabled(src), Is.True);
            Assert.That(UuidDhtConfig.From(src).Enabled, Is.True);
        }

        [Test]
        public void ServiceListConnectorImpliesEnabledWithoutFlag()
        {
            IniConfigSource src = new IniConfigSource();
            IConfig list = src.AddConfig("ServiceList");
            list.Set("UuidDhtServiceConnector", "8002/OpenSim.Addons.UUIDDHT.dll:UuidDhtServiceConnector");
            Assert.That(UuidDhtConfig.IsEnabled(src), Is.True);
        }
    }
}
