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
using System.Text;
using System.Threading;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    [TestFixture]
    public class UuidGathererTests : OpenSimTestCase
    {
        protected IAssetService m_assetService;
        protected UuidGatherer m_uuidGatherer;

        protected static string noteBase = @"Linden text version 2\n{\nLLEmbeddedItems version 1\n
{\ncount 0\n}\nText length xxx\n"; // len does not matter on this test
        [SetUp]
        public void Init()
        {
            // FIXME: We don't need a full scene here - it would be enough to set up the asset service.
            Scene scene = new SceneHelpers().SetupScene();
            m_assetService = scene.AssetService;
            m_uuidGatherer = new UuidGatherer(m_assetService);
        }

        [Test]
        public void TestCorruptAsset()
        {
            TestHelpers.InMethod();

            UUID corruptAssetUuid = UUID.Parse("00000000-0000-0000-0000-000000000666");
            AssetBase corruptAsset
                = AssetHelpers.CreateAsset(corruptAssetUuid, AssetType.Notecard, noteBase + "CORRUPT ASSET", UUID.Zero);
            m_assetService.Store(corruptAsset);

            m_uuidGatherer.AddForInspection(corruptAssetUuid);
            m_uuidGatherer.GatherAll();

            // We count the uuid as gathered even if the asset itself is corrupt.
            Assert.That(m_uuidGatherer.GatheredUuids.Count, Is.EqualTo(1));
        }

        /// <summary>
        /// Test requests made for non-existent assets while we're gathering
        /// </summary>
        [Test]
        public void TestMissingAsset()
        {
            TestHelpers.InMethod();

            UUID missingAssetUuid = UUID.Parse("00000000-0000-0000-0000-000000000666");

            m_uuidGatherer.AddForInspection(missingAssetUuid);
            m_uuidGatherer.GatherAll();

            Assert.That(m_uuidGatherer.GatheredUuids.Count, Is.EqualTo(0));
        }

        [Test]
        public void TestNotecardAsset()
        {
            TestHelpers.InMethod();
            // TestHelpers.EnableLogging();

            UUID ownerId = TestHelpers.ParseTail(0x10);
            UUID embeddedId = TestHelpers.ParseTail(0x20);
            UUID secondLevelEmbeddedId = TestHelpers.ParseTail(0x21);
            UUID missingEmbeddedId = TestHelpers.ParseTail(0x22);
            UUID ncAssetId = TestHelpers.ParseTail(0x30);

            AssetBase ncAsset
                = AssetHelpers.CreateNotecardAsset(
                    ncAssetId, string.Format("{0}Hello{1}World{2}", noteBase, embeddedId, missingEmbeddedId));
            m_assetService.Store(ncAsset);

            AssetBase embeddedAsset
                = AssetHelpers.CreateNotecardAsset(embeddedId, string.Format("{0}{1} We'll meet again.", noteBase, secondLevelEmbeddedId));
            m_assetService.Store(embeddedAsset);

            AssetBase secondLevelEmbeddedAsset
                = AssetHelpers.CreateNotecardAsset(secondLevelEmbeddedId, noteBase + "Don't know where, don't know when.");
            m_assetService.Store(secondLevelEmbeddedAsset);

            m_uuidGatherer.AddForInspection(ncAssetId);
            m_uuidGatherer.GatherAll();

            // foreach (UUID key in m_uuidGatherer.GatheredUuids.Keys)
            // System.Console.WriteLine("key : {0}", key);

            Assert.That(m_uuidGatherer.GatheredUuids.Count, Is.EqualTo(3));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(ncAssetId));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(embeddedId));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(secondLevelEmbeddedId));
        }

        [Test]
        public void TestTaskItems()
        {
            TestHelpers.InMethod();
            // TestHelpers.EnableLogging();

            UUID ownerId = TestHelpers.ParseTail(0x10);

            SceneObjectGroup soL0 = SceneHelpers.CreateSceneObject(1, ownerId, "l0", 0x20);
            SceneObjectGroup soL1 = SceneHelpers.CreateSceneObject(1, ownerId, "l1", 0x21);
            SceneObjectGroup soL2 = SceneHelpers.CreateSceneObject(1, ownerId, "l2", 0x22);

            TaskInventoryHelpers.AddScript(
                m_assetService, soL2.RootPart, TestHelpers.ParseTail(0x33), TestHelpers.ParseTail(0x43), "l3-script", "gibberish");

            TaskInventoryHelpers.AddSceneObject(
                m_assetService, soL1.RootPart, "l2-item", TestHelpers.ParseTail(0x32), soL2, TestHelpers.ParseTail(0x42));
            TaskInventoryHelpers.AddSceneObject(
                m_assetService, soL0.RootPart, "l1-item", TestHelpers.ParseTail(0x31), soL1, TestHelpers.ParseTail(0x41));

            m_uuidGatherer.AddForInspection(soL0);
            m_uuidGatherer.GatherAll();

//                        foreach (UUID key in m_uuidGatherer.GatheredUuids.Keys)
//                            System.Console.WriteLine("key : {0}", key);

            // We expect to see the default prim texture and the assets of the contained task items
            Assert.That(m_uuidGatherer.GatheredUuids.Count, Is.EqualTo(4));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(new UUID(Constants.DefaultTexture)));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(TestHelpers.ParseTail(0x41)));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(TestHelpers.ParseTail(0x42)));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(TestHelpers.ParseTail(0x43)));
        }

        [Test]
        public void TestNotecardAssetConcurrent()
        {
            TestHelpers.InMethod();

            UUID embeddedId = TestHelpers.ParseTail(0x20);
            UUID secondLevelEmbeddedId = TestHelpers.ParseTail(0x21);
            UUID missingEmbeddedId = TestHelpers.ParseTail(0x22);
            UUID ncAssetId = TestHelpers.ParseTail(0x30);

            AssetBase ncAsset
                = AssetHelpers.CreateNotecardAsset(
                    ncAssetId, string.Format("{0}Hello{1}World{2}", noteBase, embeddedId, missingEmbeddedId));
            m_assetService.Store(ncAsset);

            AssetBase embeddedAsset
                = AssetHelpers.CreateNotecardAsset(embeddedId, string.Format("{0}{1} We'll meet again.", noteBase, secondLevelEmbeddedId));
            m_assetService.Store(embeddedAsset);

            AssetBase secondLevelEmbeddedAsset
                = AssetHelpers.CreateNotecardAsset(secondLevelEmbeddedId, noteBase + "Don't know where, don't know when.");
            m_assetService.Store(secondLevelEmbeddedAsset);

            m_uuidGatherer.AddForInspection(ncAssetId);
            Assert.That(m_uuidGatherer.GatherAllConcurrent(4, 5000), Is.True);

            Assert.That(m_uuidGatherer.GatheredUuids.Count, Is.EqualTo(3));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(ncAssetId));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(embeddedId));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(secondLevelEmbeddedId));
        }

        [Test]
        public void TestTaskItemsConcurrent()
        {
            TestHelpers.InMethod();

            UUID ownerId = TestHelpers.ParseTail(0x10);

            SceneObjectGroup soL0 = SceneHelpers.CreateSceneObject(1, ownerId, "l0", 0x20);
            SceneObjectGroup soL1 = SceneHelpers.CreateSceneObject(1, ownerId, "l1", 0x21);
            SceneObjectGroup soL2 = SceneHelpers.CreateSceneObject(1, ownerId, "l2", 0x22);

            TaskInventoryHelpers.AddScript(
                m_assetService, soL2.RootPart, TestHelpers.ParseTail(0x33), TestHelpers.ParseTail(0x43), "l3-script", "gibberish");

            TaskInventoryHelpers.AddSceneObject(
                m_assetService, soL1.RootPart, "l2-item", TestHelpers.ParseTail(0x32), soL2, TestHelpers.ParseTail(0x42));
            TaskInventoryHelpers.AddSceneObject(
                m_assetService, soL0.RootPart, "l1-item", TestHelpers.ParseTail(0x31), soL1, TestHelpers.ParseTail(0x41));

            m_uuidGatherer.AddForInspection(soL0);
            Assert.That(m_uuidGatherer.GatherAllConcurrent(4, 5000), Is.True);

            Assert.That(m_uuidGatherer.GatheredUuids.Count, Is.EqualTo(4));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(new UUID(Constants.DefaultTexture)));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(TestHelpers.ParseTail(0x41)));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(TestHelpers.ParseTail(0x42)));
            Assert.That(m_uuidGatherer.GatheredUuids.ContainsKey(TestHelpers.ParseTail(0x43)));
        }

        /// <summary>
        /// A parent GET that misses the wave timeout must not drop nested UUIDs.
        /// GatherAllConcurrent returns; the drain inspects the late parent and fetches children.
        /// </summary>
        [Test]
        public void TestTimedOutInspectStillGathersChildren()
        {
            TestHelpers.InMethod();

            UUID embeddedId = TestHelpers.ParseTail(0x20);
            UUID secondLevelEmbeddedId = TestHelpers.ParseTail(0x21);
            UUID missingEmbeddedId = TestHelpers.ParseTail(0x22);
            UUID ncAssetId = TestHelpers.ParseTail(0x30);

            m_assetService.Store(AssetHelpers.CreateNotecardAsset(
                ncAssetId, string.Format("{0}Hello{1}World{2}", noteBase, embeddedId, missingEmbeddedId)));
            m_assetService.Store(AssetHelpers.CreateNotecardAsset(
                embeddedId, string.Format("{0}{1} We'll meet again.", noteBase, secondLevelEmbeddedId)));
            m_assetService.Store(AssetHelpers.CreateNotecardAsset(
                secondLevelEmbeddedId, noteBase + "Don't know where, don't know when."));

            DelayedGetUuidGatherer gatherer = new DelayedGetUuidGatherer(m_assetService);
            gatherer.DelayMs[ncAssetId] = 300;

            FireAndForgetMethod previous = Util.FireAndForgetMethod;
            try
            {
                Util.FireAndForgetMethod = FireAndForgetMethod.QueueUserWorkItem;

                gatherer.AddForInspection(ncAssetId);

                int tickStart = Environment.TickCount;
                Assert.That(gatherer.GatherAllConcurrent(4, 50), Is.True);
                int elapsed = Environment.TickCount - tickStart;

                Assert.That(elapsed, Is.LessThan(200), "GatherAllConcurrent must return on wave timeout");
                Assert.That(gatherer.FetchTimeouts, Is.GreaterThan(0));
                Assert.That(gatherer.GatheredUuids.ContainsKey(ncAssetId), Is.False,
                    "Timed-out parent must not be inspected on the calling thread");
                Assert.That(gatherer.FailedUUIDs.Contains(ncAssetId), Is.False,
                    "In-flight parent must not be recorded as a permanent miss");

                Assert.That(gatherer.WaitForPendingFetches(5000), Is.True);

                Assert.That(gatherer.GatheredUuids.ContainsKey(ncAssetId), Is.True);
                Assert.That(gatherer.GatheredUuids.ContainsKey(embeddedId), Is.True);
                Assert.That(gatherer.GatheredUuids.ContainsKey(secondLevelEmbeddedId), Is.True);
                Assert.That(gatherer.FailedUUIDs.Contains(ncAssetId), Is.False);
                Assert.That(m_assetService.Get(embeddedId.ToString()), Is.Not.Null);
                Assert.That(m_assetService.Get(secondLevelEmbeddedId.ToString()), Is.Not.Null);
            }
            finally
            {
                Util.FireAndForgetMethod = previous;
            }
        }

        private sealed class DelayedGetUuidGatherer : UuidGatherer
        {
            public Dictionary<UUID, int> DelayMs { get; } = new Dictionary<UUID, int>();

            public DelayedGetUuidGatherer(IAssetService assetService) : base(assetService) {}

            protected override AssetBase GetAsset(UUID uuid)
            {
                if (DelayMs.TryGetValue(uuid, out int ms) && ms > 0)
                    Thread.Sleep(ms);
                return base.GetAsset(uuid);
            }
        }
    }
}
