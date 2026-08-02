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
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Text;
using System.Timers;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

using Mono.Addins;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.CoreModules.Avatar.AvatarFactory
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "AvatarFactoryModule")]
    public class AvatarFactoryModule : IAvatarFactoryModule, INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string BAKED_TEXTURES_REPORT_FORMAT = "    {0,-9}  {1}";

        private Scene m_scene = null;

        private int m_savetime = 5; // seconds to wait before saving changed appearance
        private int m_sendtime = 2; // seconds to wait before sending changed appearance

        private int m_checkTime = 500; // milliseconds to wait between checks for appearance updates
        private System.Timers.Timer m_updateTimer = new System.Timers.Timer();
        private ConcurrentDictionary<UUID,long> m_savequeue = new ConcurrentDictionary<UUID,long>();
        private ConcurrentDictionary<UUID,long> m_sendqueue = new ConcurrentDictionary<UUID,long>();
        private object m_updatesLock = new object();
        private int m_updatesbusy = 0;

        private object m_setAppearanceLock = new object();

        // add throttle (per bake UUID — legacy; wave throttle below for ISSUE-005)
        private const int REBAKE_THROTTLE_SECONDS = 30;
        readonly ExpiringKey<string> m_rebakeThrottle = new(500 * REBAKE_THROTTLE_SECONDS);

        // ISSUE-004: cache bake assets by Texture UUID for this sim (login/TP reuse).
        // Policy: trust TE UUIDs from client/transfer; hydrate bytes from disk if we have that UUID;
        // only after checking ALL bake faces, request one coalesced client rebake for missing IDs.
        private bool m_localBakeStoreEnabled = true;
        private string m_localBakeStorePath = "avatar_bake_cache";

        // ISSUE-005: defer multi-face rebake until HG appearance gather completes
        private bool m_rebakeDeferEnabled = true;
        private int m_rebakeDeferTimeoutMs = 20000;
        private int m_rebakeWaveThrottleSeconds = 30;
        // Defer enter-time rebake until first AgentSetAppearance with non-empty WearableCacheItems
        // (viewer further along in appearance transaction). Safety: same RebakeDeferTimeoutMs.
        private bool m_rebakeDeferUntilClientAppearance = true;
        readonly ExpiringKey<string> m_rebakeWaveThrottle = new(500 * REBAKE_THROTTLE_SECONDS);
        private readonly ConcurrentDictionary<UUID, byte> m_gatherInProgress = new();
        private readonly ConcurrentDictionary<UUID, byte> m_awaitingClientAppearance = new();
        private readonly ConcurrentDictionary<UUID, ConcurrentDictionary<UUID, byte>> m_pendingRebakeFaces = new();
        private readonly ConcurrentDictionary<UUID, System.Timers.Timer> m_rebakeDeferTimers = new();
        
        #region Region Module interface

        public void Initialise(IConfigSource config)
        {

            IConfig appearanceConfig = config.Configs["Appearance"];
            if (appearanceConfig != null)
            {
                m_savetime = appearanceConfig.GetInt("DelayBeforeAppearanceSave", m_savetime);
                m_sendtime = appearanceConfig.GetInt("DelayBeforeAppearanceSend", m_sendtime);
                // m_log.InfoFormat("[AVFACTORY] configured for {0} save and {1} send",m_savetime,m_sendtime);

                m_localBakeStoreEnabled = appearanceConfig.GetBoolean("LocalBakeStoreEnabled", m_localBakeStoreEnabled);
                m_localBakeStorePath = appearanceConfig.GetString("LocalBakeStorePath", m_localBakeStorePath);

                m_rebakeDeferEnabled = appearanceConfig.GetBoolean("RebakeDeferUntilGather", m_rebakeDeferEnabled);
                m_rebakeDeferTimeoutMs = appearanceConfig.GetInt("RebakeDeferTimeoutMs", m_rebakeDeferTimeoutMs);
                m_rebakeWaveThrottleSeconds = appearanceConfig.GetInt("RebakeWaveThrottleSeconds", m_rebakeWaveThrottleSeconds);
                m_rebakeDeferUntilClientAppearance = appearanceConfig.GetBoolean(
                    "RebakeDeferUntilClientAppearance", m_rebakeDeferUntilClientAppearance);
            }

            if (string.IsNullOrWhiteSpace(m_localBakeStorePath))
                m_localBakeStorePath = "avatar_bake_cache";

            if (m_rebakeDeferTimeoutMs < 1000)
                m_rebakeDeferTimeoutMs = 1000;
            else if (m_rebakeDeferTimeoutMs > 120000)
                m_rebakeDeferTimeoutMs = 120000;

            if (m_rebakeWaveThrottleSeconds < 5)
                m_rebakeWaveThrottleSeconds = 5;

            if (m_localBakeStoreEnabled)
                m_log.InfoFormat(
                    "[AVFACTORY]: Bake texture cache enabled (path={0}) — UUID lookup on enter; one rebake wave only after full face check",
                    m_localBakeStorePath);

            if (m_rebakeDeferEnabled)
                m_log.InfoFormat(
                    "[AVFACTORY]: ISSUE-005 rebake defer until HG gather enabled (timeoutMs={0}, waveThrottleSec={1})",
                    m_rebakeDeferTimeoutMs, m_rebakeWaveThrottleSeconds);

            if (m_rebakeDeferUntilClientAppearance)
                m_log.InfoFormat(
                    "[AVFACTORY]: Rebake defer until client appearance cache items enabled (timeoutMs={0})",
                    m_rebakeDeferTimeoutMs);
        }

        public void AddRegion(Scene scene)
        {
            if (m_scene == null)
                m_scene = scene;

            scene.RegisterModuleInterface<IAvatarFactoryModule>(this);
            scene.EventManager.OnNewClient += SubscribeToClientEvents;
        }

        public void RemoveRegion(Scene scene)
        {
            if (scene == m_scene)
            {
                scene.UnregisterModuleInterface<IAvatarFactoryModule>(this);
                scene.EventManager.OnNewClient -= SubscribeToClientEvents;
            }

            m_scene = null;
        }

        public void RegionLoaded(Scene scene)
        {
            m_updateTimer.Enabled = false;
            m_updateTimer.AutoReset = true;
            m_updateTimer.Interval = m_checkTime; // 500 milliseconds wait to start async ops
            m_updateTimer.Elapsed += new ElapsedEventHandler(HandleAppearanceUpdateTimer);
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "Default Avatar Factory"; }
        }

        public bool IsSharedModule
        {
            get { return false; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }


        private void SubscribeToClientEvents(IClientAPI client)
        {
            client.OnRequestWearables += Client_OnRequestWearables;
            client.OnSetAppearance += Client_OnSetAppearance;
            client.OnAvatarNowWearing += Client_OnAvatarNowWearing;
            //client.OnCachedTextureRequest += Client_OnCachedTextureRequest;
        }

        #endregion

        #region IAvatarFactoryModule

        /// </summary>
        /// <param name="sp"></param>
        /// <param name="texture"></param>
        /// <param name="visualParam"></param>
        public void SetAppearance(IScenePresence sp, AvatarAppearance appearance, WearableCacheItem[] cacheItems)
        {
            SetAppearance(sp, appearance.Texture, appearance.VisualParams, cacheItems);
        }


        public void SetAppearance(IScenePresence sp, Primitive.TextureEntry textureEntry, byte[] visualParams, Vector3 avSize, WearableCacheItem[] cacheItems)
        {
            float oldoff = sp.Appearance.AvatarFeetOffset;
            Vector3 oldbox = sp.Appearance.AvatarBoxSize;

            SetAppearance(sp, textureEntry, visualParams, cacheItems);
            sp.Appearance.SetSize(avSize);

            float off = sp.Appearance.AvatarFeetOffset;
            Vector3 box = sp.Appearance.AvatarBoxSize;
            if (oldoff != off || oldbox != box)
                ((ScenePresence)sp).SetSize(box, off);
        }

        /// <summary>
        /// Set appearance data (texture asset IDs and slider settings)
        /// </summary>
        /// <param name="sp"></param>
        /// <param name="texture"></param>
        /// <param name="visualParam"></param>
        public void SetAppearance(IScenePresence sp, Primitive.TextureEntry textureEntry, byte[] visualParams, WearableCacheItem[] cacheItems)
        {
//            m_log.DebugFormat(
//                "[AVFACTORY]: start SetAppearance for {0}, te {1}, visualParams {2}",
//                sp.Name, textureEntry, visualParams);

            // TODO: This is probably not necessary any longer, just assume the
            // textureEntry set implies that the appearance transaction is complete
            bool changed = false;

            // Process the texture entry transactionally, this doesn't guarantee that Appearance is
            // going to be handled correctly but it does serialize the updates to the appearance
            lock (m_setAppearanceLock)
            {
                // Process the visual params, this may change height as well
                if (visualParams != null)
                {
                    changed = sp.Appearance.SetVisualParams(visualParams);
                }

                // Process the baked texture array
                if (textureEntry != null)
                {
                    m_log.InfoFormat(
                        "[AVFACTORY]: Received texture update for {0} {1} (cacheItems={2})",
                        sp.Name, sp.UUID, cacheItems != null ? cacheItems.Length : 0);

//                    WriteBakedTexturesReport(sp, m_log.DebugFormat);

                    changed = sp.Appearance.SetTextureEntries(textureEntry) || changed;

                    //WriteBakedTexturesReport(sp, m_log.DebugFormat);

                    if (cacheItems == null || cacheItems.Length == 0)
                        m_log.InfoFormat(
                            "[AVFACTORY]: Texture update for {0} has empty WearableCacheItems — bake faces not validated this packet",
                            sp.Name);

                    UpdateBakedTextureCache(sp, cacheItems);

                    // This appears to be set only in the final stage of the appearance
                    // update transaction. In theory, we should be able to do an immediate
                    // appearance send and save here.
                }

                // NPC should send to clients immediately and skip saving appearance
                if (((ScenePresence)sp).PresenceType == PresenceType.Npc)
                {
                    SendAppearance((ScenePresence)sp);
                    return;
                }
                // save only if there were changes
                if (changed)
                    QueueAppearanceSave(sp.ControllingClient.AgentId);
                QueueAppearanceSend(sp.ControllingClient.AgentId);
            }

            // m_log.WarnFormat("[AVFACTORY]: complete SetAppearance for {0}:\n{1}",client.AgentId,sp.Appearance.ToString());
        }

        private void SendAppearance(ScenePresence sp)
        {
            // Send the appearance to everyone in the scene
            sp.SendAppearanceToAllOtherAgents();

            // Send animations back to the avatar as well
            if(sp.Animator != null)
                sp.Animator.SendAnimPack();
        }

        public bool SendAppearance(UUID agentId)
        {
            ScenePresence sp = m_scene.GetScenePresence(agentId);
            if (sp == null || sp.IsDeleted)
                return false;

            SendAppearance(sp);
            return true;
        }

        public Dictionary<BakeType, Primitive.TextureEntryFace> GetBakedTextureFaces(UUID agentId)
        {
            ScenePresence sp = m_scene.GetScenePresence(agentId);

            if (sp == null)
                return new Dictionary<BakeType, Primitive.TextureEntryFace>();

            return GetBakedTextureFaces(sp);
        }

        public WearableCacheItem[] GetCachedItems(UUID agentId)
        {
            ScenePresence sp = m_scene.GetScenePresence(agentId);
            WearableCacheItem[] items = sp.Appearance.WearableCacheItems;
            //foreach (WearableCacheItem item in items)
            //{

            //}
            return items;
        }

        public bool SaveBakedTextures(UUID agentId)
        {
            ScenePresence sp = m_scene.GetScenePresence(agentId);

            if (sp == null)
                return false;

            m_log.DebugFormat(
                "[AV FACTORY]: Permanently saving baked textures for {0} in {1}",
                sp.Name, m_scene.RegionInfo.RegionName);

            Dictionary<BakeType, Primitive.TextureEntryFace> bakedTextures = GetBakedTextureFaces(sp);

            if (bakedTextures.Count == 0)
                return false;

            IAssetCache cache = sp.Scene.RequestModuleInterface<IAssetCache>();
            if(cache == null)
                return true; // no baked local caching so nothing to do

            foreach (BakeType bakeType in bakedTextures.Keys)
            {
                Primitive.TextureEntryFace bakedTextureFace = bakedTextures[bakeType];

                if (bakedTextureFace == null || bakedTextureFace.TextureID.Equals(AppearanceManager.DEFAULT_AVATAR_TEXTURE))
                    continue;

                AssetBase asset;
                cache.Get(bakedTextureFace.TextureID.ToString(), out asset);

                if (asset != null && asset.Local)
                {
                    // cache does not update asset contents
                    cache.Expire(bakedTextureFace.TextureID.ToString());

                    // Replace an HG ID with the simple asset ID so that we can persist textures for foreign HG avatars
                    asset.ID = asset.FullID.ToString();

                    asset.Description ="NPC BAKED";
                    asset.Temporary = false;
                    asset.Local = false;
                    //asset.Flags &= ~AssetFlags.AvatarBake; // this can cause issues on older grids
                    m_scene.AssetService.Store(asset);
                }

                if (asset == null)
                {
                    m_log.WarnFormat(
                        "[AV FACTORY]: Baked texture id {0} not found for bake {1} for avatar {2} in {3} when trying to save permanently",
                        bakedTextureFace.TextureID, bakeType, sp.Name, m_scene.RegionInfo.RegionName);
                }
            }
            return true;
        }

        /// <summary>
        /// Queue up a request to send appearance.
        /// </summary>
        /// <remarks>
        /// Makes it possible to accumulate changes without sending out each one separately.
        /// </remarks>
        /// <param name="agentId"></param>
        public void QueueAppearanceSend(UUID agentid)
        {
//            m_log.DebugFormat("[AVFACTORY]: Queue appearance send for {0}", agentid);

            // 10000 ticks per millisecond, 1000 milliseconds per second
            long timestamp = DateTime.Now.Ticks + Convert.ToInt64(m_sendtime * 1000 * 10000);
            m_sendqueue[agentid] = timestamp;
            m_updateTimer.Start();
        }

        public void QueueAppearanceSave(UUID agentid)
        {
//            m_log.DebugFormat("[AVFACTORY]: Queueing appearance save for {0}", agentid);

            // 10000 ticks per millisecond, 1000 milliseconds per second
            long timestamp = DateTime.Now.Ticks + Convert.ToInt64(m_savetime * 1000 * 10000);
            m_savequeue[agentid] = timestamp;
            m_updateTimer.Start();
        }

        // called on textures update
        public bool UpdateBakedTextureCache(IScenePresence sp, WearableCacheItem[] cacheItems)
        {
            if(cacheItems == null || cacheItems.Length == 0)
                return false;

            // npcs dont have baked cache
            if (((ScenePresence)sp).IsNPC)
                return true;

            // First non-empty WearableCacheItems: client is in a real appearance bake transaction.
            // Release enter-time "wait for client" defer (HG gather may still hold).
            ClearAwaitingClientAppearance(sp.UUID);

            // uploaded baked textures will be in assets local cache
            IAssetCache cache = m_scene.RequestModuleInterface<IAssetCache>();

            // Hydrate disk bake cache into Flotsam by UUID (does not change TE).
            if (m_localBakeStoreEnabled && cache != null)
                HydrateLocalBakeStoreIntoCache(sp.UUID, cache);

            int validDirtyBakes = 0;
            int hits = 0;

            // our main cacheIDs mapper is p.Appearance.WearableCacheItems
            bool hadSkirt = false;

            WearableCacheItem[] wearableCache = sp.Appearance.WearableCacheItems;
            if (wearableCache == null)
                wearableCache = WearableCacheItem.GetDefaultCacheItem();
            else
            {
                hadSkirt = wearableCache[19].CacheId.IsNotZero(); // .TextureID.IsNotZero();
            }

            HashSet<uint> updatedFaces = new HashSet<uint>();
            List<UUID> missing = new List<UUID>();

            // Process received baked textures
            for (int i = 0; i < cacheItems.Length; i++)
            {
                var curCacheItem = cacheItems[i];
                uint idx = curCacheItem.TextureIndex;
                if (idx >= AvatarAppearance.TEXTURE_COUNT)
                {
                    hits++;
                    continue;
                }

                updatedFaces.Add(idx);

                var wcacheidx = wearableCache[idx];
                wcacheidx.TextureAsset = null; // just in case
                Primitive.TextureEntryFace face = sp.Appearance.Texture.FaceTextures[idx];

                if (face == null || face.TextureID.IsZero() || face.TextureID.Equals(AppearanceManager.DEFAULT_AVATAR_TEXTURE))
                {
                    wcacheidx.CacheId = UUID.Zero;
                    wcacheidx.TextureID = UUID.Zero;
                    if (idx == 19)
                    {
                        hits++;
                        if(hadSkirt)
                            validDirtyBakes++;
                    }
                    continue;
                }

                if (cache != null)
                {
                    AssetBase asb = null;
                    cache.Get(face.TextureID.ToString(), out asb);
                    wcacheidx.TextureAsset = asb;
                }

                if (wcacheidx.TextureAsset != null)
                {
                    if (wcacheidx.TextureID.NotEqual(face.TextureID) ||
                            wcacheidx.CacheId.NotEqual(curCacheItem.CacheId))
                        validDirtyBakes++;

                    wcacheidx.TextureID = face.TextureID;
                    wcacheidx.CacheId = curCacheItem.CacheId;
                    hits++;
                }
                else
                {
                    wcacheidx.CacheId = UUID.Zero;
                    wcacheidx.TextureID = UUID.Zero;
                    missing.Add(face.TextureID);
                    continue;
                }
            }

            // this may be a current fs bug
            for (int i = AvatarAppearance.BAKES_COUNT_PV7; i < AvatarAppearance.BAKE_INDICES.Length; i++)
            {
                uint idx = AvatarAppearance.BAKE_INDICES[i];
                if(updatedFaces.Contains(idx))
                    continue;

                sp.Appearance.Texture.FaceTextures[idx] = null;

                var wcacheidx = wearableCache[idx];
                wcacheidx.CacheId = UUID.Zero;
                wcacheidx.TextureID = UUID.Zero;
                wcacheidx.TextureAsset = null;
            }
 
            sp.Appearance.WearableCacheItems = wearableCache;

            // After ALL faces checked: one coalesced client rebake request (not per-face during the loop).
            if (missing.Count > 0)
            {
                m_log.InfoFormat(
                    "[AVFACTORY]: Bake cache miss for {0}: {1} face UUID(s) missing after full check — one rebake wave",
                    sp.Name, missing.Count);
                QueueOrSendRebakeWave(sp, missing);
            }
            else
            {
                // Full hit on this transaction — drop any enter-time pending faces.
                m_pendingRebakeFaces.TryRemove(sp.UUID, out _);
            }

            bool changed = false;
            if (validDirtyBakes > 0 && hits == cacheItems.Length)
            {
                // if we got a full set of baked textures save all in BakedTextureModule
                IBakedTextureModule m_BakedTextureModule = m_scene.RequestModuleInterface<IBakedTextureModule>();
                if (m_BakedTextureModule != null)
                {
                    m_log.DebugFormat("[UpdateBakedCache] Uploading to Bakes Server: cache hits: {0} changed entries: {1} rebakes {2}",
                        hits.ToString(), validDirtyBakes.ToString(), missing.Count);

                    m_BakedTextureModule.Store(sp.UUID, wearableCache);
                    changed = true;
                }
            }
            else
                m_log.DebugFormat("[UpdateBakedCache] cache hits: {0} changed entries: {1} rebakes {2}",
                        hits.ToString(), validDirtyBakes.ToString(), missing.Count);

            // ISSUE-004: persist full bake set under agent UUID for next visit to this sim
            if (m_localBakeStoreEnabled && missing.Count == 0 && cache != null)
                StoreLocalAgentBakes(sp, wearableCache, cache);

            for (int iter = 0; iter < AvatarAppearance.BAKE_INDICES.Length; iter++)
            {
                int j = AvatarAppearance.BAKE_INDICES[iter];
                sp.Appearance.WearableCacheItems[j].TextureAsset = null;
//                m_log.Debug("[UpdateBCache] {" + iter + "/" +
//                                    sp.Appearance.WearableCacheItems[j].TextureIndex + "}: c-" +
//                                    sp.Appearance.WearableCacheItems[j].CacheId + ", t-" +
//                                    sp.Appearance.WearableCacheItems[j].TextureID);
            }

            return changed;
        }

        // called when we get a new root avatar (login / TP complete)
        public bool ValidateBakedTextureCache(IScenePresence sp)
        {
            if (((ScenePresence)sp).IsNPC)
                return true;

            IAssetCache cache = m_scene.RequestModuleInterface<IAssetCache>();
            if (cache == null)
                return false;

            // 1) Load any previously cached bake *bytes* for this agent into Flotsam (by Texture UUID).
            //    Does NOT rewrite appearance TE — TE UUIDs from transfer/client are authoritative.
            if (m_localBakeStoreEnabled)
                HydrateLocalBakeStoreIntoCache(sp.UUID, cache);

            // 2) Check EVERY bake face UUID in TE against cache (full pass, no rebake mid-loop).
            List<UUID> missing = CollectMissingBakeTextureIds(sp, cache, out int present, out int listed);

            m_log.InfoFormat(
                "[AVFACTORY]: Bake UUID check on enter for {0}: listed={1} present={2} missing={3}",
                sp.Name, listed, present, missing.Count);

            // 2b) CacheId reconcile: a TE face may carry a UUID minted by another sim for the same
            //     outfit.  If the local store has bytes for that slot with the SAME CacheId under a
            //     different UUID, reuse them under the current TE UUID so we skip the rebake.
            if (missing.Count > 0 && m_localBakeStoreEnabled)
            {
                int reconciled = ReconcileLocalBakeStoreToTE(sp.UUID, sp, cache, missing);
                if (reconciled > 0)
                {
                    missing = CollectMissingBakeTextureIds(sp, cache, out present, out listed);
                    m_log.InfoFormat(
                        "[AVFACTORY]: Bake UUID check after CacheId reconcile for {0}: listed={1} present={2} missing={3} (reconciled={4})",
                        sp.Name, listed, present, missing.Count, reconciled);
                }
            }

            // 3) Optional XBakes only if still missing after local hydrate — avoid remote GET on full local hit.
            if (missing.Count > 0)
            {
                int injected = TryInjectXBakesIntoCache(sp.UUID, cache);
                if (injected > 0)
                {
                    missing = CollectMissingBakeTextureIds(sp, cache, out present, out listed);
                    m_log.InfoFormat(
                        "[AVFACTORY]: Bake UUID check after XBakes for {0}: listed={1} present={2} missing={3} (injected={4})",
                        sp.Name, listed, present, missing.Count, injected);
                }
            }

            // 4) On miss: hold rebake until first non-empty AgentSetAppearance cache items
            //    (and/or HG gather). Safety flush via RebakeDeferTimeoutMs.
            if (missing.Count > 0)
            {
                if (m_rebakeDeferUntilClientAppearance)
                    MarkAwaitingClientAppearance(sp.UUID);
                QueueOrSendRebakeWave(sp, missing);
            }
            else
            {
                ClearAwaitingClientAppearance(sp.UUID);
            }

            // Sync WearableCacheItems TextureIDs from TE for faces we have
            WearableCacheItem[] wearableCache = sp.Appearance.WearableCacheItems
                ?? WearableCacheItem.GetDefaultCacheItem();
            if (sp.Appearance?.Texture?.FaceTextures != null)
            {
                for (int i = 0; i < AvatarAppearance.BAKE_INDICES.Length; i++)
                {
                    int idx = AvatarAppearance.BAKE_INDICES[i];
                    Primitive.TextureEntryFace face = sp.Appearance.Texture.FaceTextures[idx];
                    if (face == null || face.TextureID.IsZero() ||
                            face.TextureID.Equals(AppearanceManager.DEFAULT_AVATAR_TEXTURE))
                        continue;
                    if (!cache.Check(face.TextureID.ToString()))
                        continue;
                    wearableCache[idx].TextureIndex = (uint)idx;
                    wearableCache[idx].TextureID = face.TextureID;
                    if (wearableCache[idx].CacheId.IsZero())
                        wearableCache[idx].CacheId = face.TextureID;
                }
                sp.Appearance.WearableCacheItems = wearableCache;
            }

            return missing.Count == 0;
        }

        public int RequestRebake(IScenePresence sp, bool missingTexturesOnly)
        {
            if (((ScenePresence)sp).IsNPC)
                return 0;

            IAssetCache cache = m_scene.RequestModuleInterface<IAssetCache>();
            if (cache != null && m_localBakeStoreEnabled)
                HydrateLocalBakeStoreIntoCache(sp.UUID, cache);

            List<UUID> missing;
            if (missingTexturesOnly)
            {
                missing = CollectMissingBakeTextureIds(sp, cache, out _, out _);
            }
            else
            {
                // Full rebake: all non-default bake face UUIDs
                missing = new List<UUID>();
                HashSet<UUID> seen = new HashSet<UUID>();
                if (sp.Appearance?.Texture?.FaceTextures != null)
                {
                    for (int i = 0; i < AvatarAppearance.BAKE_INDICES.Length; i++)
                    {
                        int idx = AvatarAppearance.BAKE_INDICES[i];
                        Primitive.TextureEntryFace face = sp.Appearance.Texture.FaceTextures[idx];
                        if (face == null || face.TextureID.IsZero() ||
                                face.TextureID.Equals(AppearanceManager.DEFAULT_AVATAR_TEXTURE))
                            continue;
                        if (seen.Add(face.TextureID))
                            missing.Add(face.TextureID);
                    }
                }
            }

            if (missing.Count == 0)
                return 0;

            // Force path: do not defer when caller asked for full rebake of known faces
            if (!missingTexturesOnly)
            {
                FlushRebakeWave(sp, missing, "request-full");
                return missing.Count;
            }

            QueueOrSendRebakeWave(sp, missing);
            return missing.Count;
        }

        /// <summary>
        /// ISSUE-005: HG attachment/appearance gather started — hold rebake waves.
        /// </summary>
        public void MarkAppearanceGatherInProgress(UUID agentId)
        {
            if (!m_rebakeDeferEnabled || agentId.IsZero())
                return;

            m_gatherInProgress[agentId] = 0;
            ScheduleRebakeDeferTimeout(agentId);

            m_log.DebugFormat(
                "[AVFACTORY]: Appearance gather in progress for {0} — rebake deferred (timeout {1} ms)",
                agentId, m_rebakeDeferTimeoutMs);
        }

        /// <summary>
        /// ISSUE-005: gather finished — flush any pending coalesced rebake (after full face checks already queued).
        /// If still waiting for first non-empty client cache items, keep holding until that or timeout.
        /// </summary>
        public void NotifyAppearanceGatherComplete(UUID agentId)
        {
            if (agentId.IsZero())
                return;

            m_gatherInProgress.TryRemove(agentId, out _);

            ScenePresence sp = m_scene.GetScenePresence(agentId);
            if (sp == null || sp.IsDeleted || sp.IsChildAgent)
            {
                m_pendingRebakeFaces.TryRemove(agentId, out _);
                m_awaitingClientAppearance.TryRemove(agentId, out _);
                CancelRebakeDeferTimeout(agentId);
                return;
            }

            // Still waiting for AgentSetAppearance with WearableCacheItems — do not rebake yet.
            if (m_rebakeDeferUntilClientAppearance && m_awaitingClientAppearance.ContainsKey(agentId))
            {
                m_log.InfoFormat(
                    "[AVFACTORY]: Appearance gather complete for {0} — still waiting for client appearance cache items (or timeout {1} ms)",
                    sp.Name, m_rebakeDeferTimeoutMs);
                ScheduleRebakeDeferTimeout(agentId);
                return;
            }

            CancelRebakeDeferTimeout(agentId);

            // Re-check all bake UUIDs once after gather (assets may have been pulled); one wave only if still missing.
            IAssetCache cache = m_scene.RequestModuleInterface<IAssetCache>();
            if (cache != null && m_localBakeStoreEnabled)
                HydrateLocalBakeStoreIntoCache(sp.UUID, cache);

            List<UUID> missingNow = CollectMissingBakeTextureIds(sp, cache, out int present, out int listed);
            if (missingNow.Count > 0)
            {
                m_log.InfoFormat(
                    "[AVFACTORY]: Post-gather bake UUID check for {0}: listed={1} present={2} missing={3} — one rebake wave",
                    sp.Name, listed, present, missingNow.Count);
                FlushRebakeWave(sp, missingNow, "gather-complete");
                return;
            }

            // Clear any stale pending faces
            m_pendingRebakeFaces.TryRemove(agentId, out _);
            m_log.DebugFormat(
                "[AVFACTORY]: Post-gather bake UUID check for {0}: all {1} present — no rebake",
                sp.Name, listed);
        }

        #endregion

        #region AvatarFactoryModule private methods

        private void MarkAwaitingClientAppearance(UUID agentId)
        {
            if (!m_rebakeDeferUntilClientAppearance || agentId.IsZero())
                return;

            m_awaitingClientAppearance[agentId] = 0;
            ScheduleRebakeDeferTimeout(agentId);
            m_log.DebugFormat(
                "[AVFACTORY]: Waiting for client appearance cache items for {0} before rebake (timeout {1} ms)",
                agentId, m_rebakeDeferTimeoutMs);
        }

        private void ClearAwaitingClientAppearance(UUID agentId)
        {
            if (agentId.IsZero())
                return;

            if (m_awaitingClientAppearance.TryRemove(agentId, out _))
            {
                m_log.DebugFormat(
                    "[AVFACTORY]: Client appearance cache items received for {0} — enter-time client wait cleared",
                    agentId);
            }

            // Drop timer only if nothing else is holding the defer.
            if (!m_gatherInProgress.ContainsKey(agentId))
                CancelRebakeDeferTimeout(agentId);
        }

        private bool IsRebakeDeferred(UUID agentId, out string reason)
        {
            reason = null;
            if (agentId.IsZero())
                return false;

            if (m_rebakeDeferEnabled && m_gatherInProgress.ContainsKey(agentId))
            {
                reason = "appearance gather";
                return true;
            }

            if (m_rebakeDeferUntilClientAppearance && m_awaitingClientAppearance.ContainsKey(agentId))
            {
                reason = "client appearance cache items";
                return true;
            }

            return false;
        }

        private void QueueOrSendRebakeWave(IScenePresence sp, List<UUID> missing)
        {
            if (sp == null || missing == null || missing.Count == 0)
                return;

            ConcurrentDictionary<UUID, byte> pending = m_pendingRebakeFaces.GetOrAdd(
                sp.UUID, _ => new ConcurrentDictionary<UUID, byte>());
            foreach (UUID id in missing)
            {
                if (id.IsNotZero())
                    pending[id] = 0;
            }

            if (IsRebakeDeferred(sp.UUID, out string deferReason))
            {
                m_log.InfoFormat(
                    "[AVFACTORY]: Rebake deferred for {0}: {1} face(s) pending (waiting for {2})",
                    sp.Name, pending.Count, deferReason);
                ScheduleRebakeDeferTimeout(sp.UUID);
                return;
            }

            List<UUID> faces = new List<UUID>(pending.Keys);
            FlushRebakeWave(sp, faces, "immediate");
        }

        private void FlushRebakeWave(IScenePresence sp, List<UUID> faceIds, string reason)
        {
            if (sp == null || ((ScenePresence)sp).IsNPC)
                return;

            m_pendingRebakeFaces.TryRemove(sp.UUID, out _);
            m_gatherInProgress.TryRemove(sp.UUID, out _);
            m_awaitingClientAppearance.TryRemove(sp.UUID, out _);
            CancelRebakeDeferTimeout(sp.UUID);

            IAssetCache cache = m_scene.RequestModuleInterface<IAssetCache>();
            if (cache != null && m_localBakeStoreEnabled)
                HydrateLocalBakeStoreIntoCache(sp.UUID, cache);

            // Full re-check of TE (and any queued ids) — only then send one wave to client.
            List<UUID> still = CollectMissingBakeTextureIds(sp, cache, out _, out _);
            HashSet<UUID> seen = new HashSet<UUID>(still);
            if (faceIds != null)
            {
                foreach (UUID id in faceIds)
                {
                    if (id.IsZero() || id.Equals(AppearanceManager.DEFAULT_AVATAR_TEXTURE))
                        continue;
                    if (cache != null && cache.Check(id.ToString()))
                        continue;
                    if (seen.Add(id))
                        still.Add(id);
                }
            }

            if (still.Count == 0)
            {
                m_log.InfoFormat(
                    "[AVFACTORY]: Rebake wave skipped for {0} reason={1} (all bake UUIDs present after full check)",
                    sp.Name, reason);
                return;
            }

            // One wave per agent per throttle window (ISSUE-005)
            string waveKey = sp.UUID.ToString();
            if (m_rebakeWaveThrottle.AddOrUpdate(waveKey, 1000 * m_rebakeWaveThrottleSeconds))
            {
                m_log.DebugFormat(
                    "[AVFACTORY]: Rebake wave throttled for {0} reason={1} ({2} face(s) not sent)",
                    sp.Name, reason, still.Count);
                return;
            }

            int sent = 0;
            foreach (UUID id in still)
            {
                string faceKey = waveKey + id.ToString();
                if (m_rebakeThrottle.AddOrUpdate(faceKey, 1000 * REBAKE_THROTTLE_SECONDS))
                    continue;

                try
                {
                    sp.ControllingClient.SendRebakeAvatarTextures(id);
                    sent++;
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[AVFACTORY]: SendRebake failed for {0} {1}: {2}", sp.Name, id, e.Message);
                }
            }

            m_log.InfoFormat(
                "[AVFACTORY]: Rebake wave for {0}: sent={1} faces={2} reason={3}",
                sp.Name, sent, still.Count, reason);
        }

        private void ScheduleRebakeDeferTimeout(UUID agentId)
        {
            // Active if either HG gather defer or client-appearance defer may hold a wave.
            if ((!m_rebakeDeferEnabled && !m_rebakeDeferUntilClientAppearance) || agentId.IsZero())
                return;

            // Already have a timer — do not reset (enter-time / gather should share one deadline).
            if (m_rebakeDeferTimers.ContainsKey(agentId))
                return;

            System.Timers.Timer timer = new System.Timers.Timer(m_rebakeDeferTimeoutMs)
            {
                AutoReset = false
            };
            timer.Elapsed += (sender, args) =>
            {
                try
                {
                    bool heldGather = m_gatherInProgress.ContainsKey(agentId);
                    bool heldClient = m_awaitingClientAppearance.ContainsKey(agentId);
                    bool hadPending = m_pendingRebakeFaces.ContainsKey(agentId);
                    if (!heldGather && !heldClient && !hadPending)
                        return;

                    m_log.InfoFormat(
                        "[AVFACTORY]: Rebake defer timeout ({0} ms) for {1} — flushing (gather={2} clientWait={3} pending={4})",
                        m_rebakeDeferTimeoutMs, agentId, heldGather, heldClient, hadPending);

                    m_gatherInProgress.TryRemove(agentId, out _);
                    m_awaitingClientAppearance.TryRemove(agentId, out _);

                    ScenePresence sp = m_scene.GetScenePresence(agentId);
                    if (sp == null || sp.IsDeleted || sp.IsChildAgent)
                    {
                        m_pendingRebakeFaces.TryRemove(agentId, out _);
                        return;
                    }

                    List<UUID> pending = null;
                    if (m_pendingRebakeFaces.TryGetValue(agentId, out ConcurrentDictionary<UUID, byte> map))
                        pending = new List<UUID>(map.Keys);

                    FlushRebakeWave(sp, pending, "defer-timeout");
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[AVFACTORY]: Defer timeout handler error: {0}", e.Message);
                }
                finally
                {
                    CancelRebakeDeferTimeout(agentId);
                }
            };

            if (m_rebakeDeferTimers.TryAdd(agentId, timer))
                timer.Start();
            else
            {
                try { timer.Dispose(); } catch { }
            }
        }

        private void CancelRebakeDeferTimeout(UUID agentId)
        {
            if (m_rebakeDeferTimers.TryRemove(agentId, out System.Timers.Timer timer))
            {
                try
                {
                    timer.Stop();
                    timer.Dispose();
                }
                catch { }
            }
        }


        private string GetAgentBakeStoreFile(UUID agentId)
        {
            return Path.Combine(m_localBakeStorePath, agentId.ToString() + ".osd");
        }

        /// <summary>
        /// Optional XBakes / IBakedTextureModule package: inject TextureAssets into Flotsam by UUID.
        /// Call only after local hydrate when TE bake faces are still missing (avoids remote GET on full hit).
        /// Returns number of assets cached (0 if module off, empty, or error).
        /// </summary>
        private int TryInjectXBakesIntoCache(UUID agentId, IAssetCache cache)
        {
            if (cache == null || agentId.IsZero())
                return 0;

            IBakedTextureModule bakedModule = m_scene.RequestModuleInterface<IBakedTextureModule>();
            if (bakedModule == null)
                return 0;

            try
            {
                WearableCacheItem[] bakedModuleCache = bakedModule.Get(agentId);
                if (bakedModuleCache == null || bakedModuleCache.Length == 0)
                    return 0;

                int injected = 0;
                foreach (WearableCacheItem item in bakedModuleCache)
                {
                    if (item?.TextureAsset == null || item.TextureID.IsZero())
                        continue;
                    item.TextureAsset.Temporary = true;
                    item.TextureAsset.Local = true;
                    cache.Cache(item.TextureAsset);
                    injected++;
                }
                return injected;
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[AVFACTORY]: XBakes Get failed for {0}: {1}", agentId, e.Message);
                return 0;
            }
        }

        /// <summary>
        /// Load bake asset bytes from disk store into IAssetCache keyed by their Texture UUIDs.
        /// Does not modify appearance TE — TE remains whatever login/TP provided.
        /// </summary>
        private void HydrateLocalBakeStoreIntoCache(UUID agentId, IAssetCache cache)
        {
            WearableCacheItem[] loaded = LoadLocalBakeStore(agentId, cache);
            if (loaded == null || loaded.Length == 0)
                return;

            int n = 0;
            foreach (WearableCacheItem item in loaded)
            {
                if (item != null && item.TextureID.IsNotZero() && cache.Check(item.TextureID.ToString()))
                    n++;
            }
            m_log.DebugFormat(
                "[AVFACTORY]: Hydrated {0} bake asset(s) from local store into cache for {1}",
                n, agentId);
        }

        /// <summary>
        /// Read the agent bake store from disk and inject every embedded asset's bytes into the
        /// cache keyed by its stored Texture UUID.  Returns the stored cache items
        /// (TextureIndex/CacheId/TextureID) so callers can match by CacheId; null if no store.
        /// </summary>
        private WearableCacheItem[] LoadLocalBakeStore(UUID agentId, IAssetCache cache)
        {
            if (!m_localBakeStoreEnabled || cache == null || agentId.IsZero())
                return null;

            string path = GetAgentBakeStoreFile(agentId);
            if (!File.Exists(path))
                return null;

            try
            {
                byte[] raw = File.ReadAllBytes(path);
                if (raw == null || raw.Length == 0)
                    return null;

                OSD osd = OSDParser.DeserializeLLSDXml(raw);
                // FromOSD injects assetdata into cache under each item's TextureID
                return WearableCacheItem.FromOSD(osd, cache);
            }
            catch (Exception e)
            {
                m_log.DebugFormat(
                    "[AVFACTORY]: Load local bake store failed for {0}: {1}",
                    agentId, e.Message);
                return null;
            }
        }

        /// <summary>
        /// Scan all bake faces on the appearance TE. Returns UUIDs not present in cache.
        /// Call only after HydrateLocalBakeStoreIntoCache so disk hits count as present.
        /// </summary>
        private static List<UUID> CollectMissingBakeTextureIds(
            IScenePresence sp, IAssetCache cache, out int present, out int listed)
        {
            List<UUID> missing = new List<UUID>();
            present = 0;
            listed = 0;
            if (sp?.Appearance?.Texture?.FaceTextures == null)
                return missing;

            HashSet<UUID> seen = new HashSet<UUID>();
            Primitive.TextureEntryFace[] faces = sp.Appearance.Texture.FaceTextures;

            for (int i = 0; i < AvatarAppearance.BAKE_INDICES.Length; i++)
            {
                int idx = AvatarAppearance.BAKE_INDICES[i];
                if (idx >= faces.Length)
                    continue;

                Primitive.TextureEntryFace face = faces[idx];
                if (face == null || face.TextureID.IsZero() ||
                        face.TextureID.Equals(AppearanceManager.DEFAULT_AVATAR_TEXTURE))
                    continue;

                if (!seen.Add(face.TextureID))
                    continue;

                listed++;
                if (cache != null && cache.Check(face.TextureID.ToString()))
                {
                    present++;
                    continue;
                }
                missing.Add(face.TextureID);
            }

            return missing;
        }

        /// <summary>
        /// Save bake face textures for this agent (bytes + UUIDs) for the next visit to this sim.
        /// </summary>
        private void StoreLocalAgentBakes(IScenePresence sp, WearableCacheItem[] wearableCache, IAssetCache cache)
        {
            if (!m_localBakeStoreEnabled || sp == null || wearableCache == null || cache == null)
                return;

            try
            {
                List<WearableCacheItem> bakeItems = new List<WearableCacheItem>();
                for (int i = 0; i < AvatarAppearance.BAKE_INDICES.Length; i++)
                {
                    int idx = AvatarAppearance.BAKE_INDICES[i];
                    if (idx >= wearableCache.Length)
                        continue;
                    WearableCacheItem item = wearableCache[idx];
                    if (item == null || item.TextureID.IsZero() ||
                            item.TextureID.Equals(AppearanceManager.DEFAULT_AVATAR_TEXTURE))
                        continue;
                    if (!cache.Check(item.TextureID.ToString()))
                        continue;
                    bakeItems.Add(item);
                }

                // Need a solid body set (skirt optional) — BAKES_COUNT_PV7 includes skirt at index slot 19
                int bodyCount = 0;
                foreach (WearableCacheItem item in bakeItems)
                {
                    if (item.TextureIndex != 19)
                        bodyCount++;
                }
                if (bodyCount < 5)
                    return;

                OSD osd = WearableCacheItem.ToOSD(bakeItems.ToArray(), cache);
                if (osd == null)
                    return;

                // Ensure every item actually embedded asset bytes
                if (osd is OSDArray arr)
                {
                    int withData = 0;
                    foreach (OSD o in arr)
                    {
                        if (o is OSDMap map && map.ContainsKey("assetdata"))
                            withData++;
                    }
                    if (withData < 5)
                        return;
                }

                if (WriteLocalBakeStore(sp.UUID, bakeItems.ToArray(), cache))
                    m_log.InfoFormat(
                        "[AVFACTORY]: Stored local bakes for {0} ({1} face(s))",
                        sp.Name, bakeItems.Count);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[AVFACTORY]: Failed to store local bakes for {0}: {1}", sp.Name, e.Message);
            }
        }

        /// <summary>
        /// Serialize and persist a bake cache item set to disk, embedding each item's asset bytes
        /// from the cache (by its TextureID).
        /// </summary>
        private bool WriteLocalBakeStore(UUID agentId, WearableCacheItem[] items, IAssetCache cache)
        {
            if (!m_localBakeStoreEnabled || agentId.IsZero() || items == null || items.Length == 0 || cache == null)
                return false;

            try
            {
                OSD osd = WearableCacheItem.ToOSD(items, cache);
                if (osd == null)
                    return false;

                Directory.CreateDirectory(m_localBakeStorePath);
                string path = GetAgentBakeStoreFile(agentId);
                byte[] data = OSDParser.SerializeLLSDXmlBytes(osd);
                File.WriteAllBytes(path, data);

                m_log.InfoFormat(
                    "[AVFACTORY]: Wrote local bakes for {0} ({1} face(s), {2} bytes) → {3}",
                    agentId, items.Length, data.Length, path);
                return true;
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[AVFACTORY]: Failed to write local bakes for {0}: {1}", agentId, e.Message);
                return false;
            }
        }

        /// <summary>
        /// Break the endless rebake loop when teleporting between sims mints a new random bake
        /// UUID each time even though the outfit is unchanged.
        /// </summary>
        /// <remarks>
        /// For each TE bake face that is missing from cache, look up the local store entry for that
        /// slot.  If its CacheId equals the incoming CacheId (same outfit) but the stored TextureID
        /// differs (UUID minted elsewhere), copy the stored bytes under the current TE UUID so the
        /// face is served without a rebake.  Re-keys the store entry to the current TE UUID and
        /// expires the old key so the store always tracks the current UUIDs — bounded, no unbounded
        /// R1→R2→R3 accumulation.
        ///
        /// Only matches when both CacheIds are non-zero; zero CacheIds fall back to a rebake.
        /// </remarks>
        /// <returns>Number of faces reconciled (bytes reused, rebake avoided).</returns>
        private int ReconcileLocalBakeStoreToTE(UUID agentId, IScenePresence sp, IAssetCache cache, List<UUID> missing)
        {
            if (!m_localBakeStoreEnabled || cache == null || agentId.IsZero())
                return 0;
            if (missing == null || missing.Count == 0)
                return 0;
            if (sp?.Appearance?.WearableCacheItems == null || sp.Appearance.Texture?.FaceTextures == null)
                return 0;

            WearableCacheItem[] storeItems = LoadLocalBakeStore(agentId, cache);
            if (storeItems == null || storeItems.Length == 0)
                return 0;

            WearableCacheItem[] wearableCache = sp.Appearance.WearableCacheItems;
            Primitive.TextureEntryFace[] faces = sp.Appearance.Texture.FaceTextures;
            HashSet<UUID> missingSet = new HashSet<UUID>(missing);

            int reconciled = 0;
            List<UUID> expired = new List<UUID>();

            for (int i = 0; i < AvatarAppearance.BAKE_INDICES.Length; i++)
            {
                int idx = AvatarAppearance.BAKE_INDICES[i];
                if (idx >= faces.Length || idx >= wearableCache.Length)
                    continue;

                Primitive.TextureEntryFace face = faces[idx];
                if (face == null || face.TextureID.IsZero() ||
                        face.TextureID.Equals(AppearanceManager.DEFAULT_AVATAR_TEXTURE))
                    continue;

                UUID teId = face.TextureID;
                if (!missingSet.Contains(teId) || cache.Check(teId.ToString()))
                    continue;

                UUID incomingCacheId = wearableCache[idx].CacheId;
                if (incomingCacheId.IsZero())
                    continue;

                WearableCacheItem storeItem = WearableCacheItem.SearchTextureIndex((uint)idx, storeItems);
                if (storeItem == null || storeItem.CacheId.IsZero() || !storeItem.CacheId.Equals(incomingCacheId))
                    continue;

                UUID storedId = storeItem.TextureID;
                if (storedId.IsZero() || storedId.Equals(teId))
                    continue;

                AssetBase stored;
                if (!cache.Get(storedId.ToString(), out stored) || stored == null || stored.Data == null)
                    continue;

                AssetBase copy = new AssetBase(teId, stored.Name, stored.Type, stored.CreatorID)
                {
                    Data = stored.Data,
                    Temporary = true,
                    Local = true
                };
                cache.Cache(copy, true);

                storeItem.TextureID = teId;
                expired.Add(storedId);
                reconciled++;
            }

            if (reconciled > 0)
            {
                // Persist the re-keyed store so it tracks the current TE UUIDs, then drop the old keys.
                WriteLocalBakeStore(agentId, storeItems, cache);
                foreach (UUID oldId in expired)
                    cache.Expire(oldId.ToString());

                m_log.InfoFormat(
                    "[AVFACTORY]: Reconcile {0} bake face(s) for {1} by CacheId — reused stored bytes under current TE UUIDs, no rebake",
                    reconciled, agentId);
            }

            return reconciled;
        }

        private Dictionary<BakeType, Primitive.TextureEntryFace> GetBakedTextureFaces(ScenePresence sp)
        {
            if (sp.IsChildAgent)
                return new Dictionary<BakeType, Primitive.TextureEntryFace>();

            Dictionary<BakeType, Primitive.TextureEntryFace> bakedTextures
                = new Dictionary<BakeType, Primitive.TextureEntryFace>();

            AvatarAppearance appearance = sp.Appearance;
            Primitive.TextureEntryFace[] faceTextures = appearance.Texture.FaceTextures;

            foreach (int i in Enum.GetValues(typeof(BakeType)))
            {
                BakeType bakeType = (BakeType)i;
                if (bakeType == BakeType.NumberOfEntries)
                    break;

                if (bakeType == BakeType.Unknown)
                    continue;

                //m_log.DebugFormat(
                //    "[AVFACTORY]: NPC avatar {0} has texture id {1} : {2}",
                //     acd.AgentID, i, acd.Appearance.Texture.FaceTextures[i]);

                int ftIndex = (int)AppearanceManager.BakeTypeToAgentTextureIndex(bakeType);
                Primitive.TextureEntryFace texture = faceTextures[ftIndex];    // this will be null if there's no such baked texture
                bakedTextures[bakeType] = texture;
            }

            return bakedTextures;
        }

        private void HandleAppearanceUpdateTimer(object sender, EventArgs ea)
        {
            if(Monitor.TryEnter(m_updatesLock))
            {
                UUID id;
                long now = DateTime.Now.Ticks;

                foreach (KeyValuePair<UUID, long> kvp in m_sendqueue)
                {
                    long sendTime = kvp.Value;
                    if (sendTime > now)
                    continue;

                    id = kvp.Key;
                    m_sendqueue.TryRemove(id, out sendTime);
                    SendAppearance(id);
                }

                if(m_updatesbusy == 0)
                {
                    m_updatesbusy = -1;
                    List<UUID> saves = new List<UUID>(m_savequeue.Count);
                    foreach (KeyValuePair<UUID, long> kvp in m_savequeue)
                    {
                        long sendTime = kvp.Value;
                        if (sendTime > now)
                            continue;

                        id = kvp.Key;
                        m_savequeue.TryRemove(id, out sendTime);
                            saves.Add(id);
                    }

                    m_updatesbusy = 0;
                    if (saves.Count > 0)
                    {
                        ++m_updatesbusy;
                        WorkManager.RunInThreadPool(
                            delegate
                            {
                                SaveAppearance(saves);
                                saves = null;
                                --m_updatesbusy;
                            }, null, string.Format("SaveAppearance ({0})", m_scene.Name));
                    }
                }

                if (m_savequeue.Count == 0 && m_sendqueue.Count == 0)
                    m_updateTimer.Stop();

                 Monitor.Exit(m_updatesLock);
            }
        }

        private void SaveAppearance(List<UUID> ids)
        {
//            m_log.DebugFormat("[AVFACTORY]: Saving appearance for avatar {0}", agentid);

            foreach(UUID id in ids)
            {
                ScenePresence sp = m_scene.GetScenePresence(id);
                if(sp == null)
                    continue;
                // This could take awhile since it needs to pull inventory
                // We need to do it at the point of save so that there is a sufficient delay for any upload of new body part/shape
                // assets and item asset id changes to complete.
                // I don't think we need to worry about doing this within m_setAppearanceLock since the queueing avoids
                // multiple save requests.

                SetAppearanceAssets(id, sp.Appearance);

                m_scene.AvatarService.SetAppearance(id, sp.Appearance);
                //m_scene.EventManager.TriggerAvatarAppearanceChanged(sp);
            }
        }

        /// <summary>
        /// For a given set of appearance items, check whether the items are valid and add their asset IDs to
        /// appearance data.
        /// </summary>
        /// <param name='userID'></param>
        /// <param name='appearance'></param>
        private void SetAppearanceAssets(UUID userID, AvatarAppearance appearance)
        {
            IInventoryService invService = m_scene.InventoryService;

            if (invService.GetRootFolder(userID) != null)
            {
                for (int i = 0; i < appearance.Wearables.Length; i++)
                {
                    for (int j = 0; j < appearance.Wearables[i].Count; j++)
                    {
                        if (appearance.Wearables[i][j].ItemID.IsZero())
                        {
                            m_log.WarnFormat(
                                "[AVFACTORY]: Wearable item {0}:{1} for user {2} unexpectedly UUID.Zero.  Ignoring.",
                                i, j, userID);

                            continue;
                        }

                        // Ignore ruth's assets
                        if (i < AvatarWearable.DefaultWearables.Length)
                        {
                            if (appearance.Wearables[i][j].ItemID == AvatarWearable.DefaultWearables[i][0].ItemID)
                                continue;
                        }

                        InventoryItemBase baseItem = invService.GetItem(userID, appearance.Wearables[i][j].ItemID);

                        if (baseItem != null)
                        {
                            appearance.Wearables[i].Add(appearance.Wearables[i][j].ItemID, baseItem.AssetID);
                        }
                        else
                        {
                            m_log.WarnFormat(
                                "[AVFACTORY]: Can't find inventory item {0} for {1}, setting to default",
                                appearance.Wearables[i][j].ItemID, (WearableType)i);

                            appearance.Wearables[i].RemoveItem(appearance.Wearables[i][j].ItemID);
                        }
                    }
                }
            }
            else
            {
                m_log.WarnFormat("[AVFACTORY]: user {0} has no inventory, appearance isn't going to work", userID);
            }

            //IInventoryService invService = m_scene.InventoryService;
            //bool resetwearable = false;
            //if (invService.GetRootFolder(userID) != null)
            //{
            //    for (int i = 0; i < AvatarWearable.MAX_WEARABLES; i++)
            //    {
            //        for (int j = 0; j < appearance.Wearables[i].Count; j++)
            //        {
            //            // Check if the default wearables are not set
            //            if (appearance.Wearables[i][j].ItemID.IsZero())
            //            {
            //                switch ((WearableType) i)
            //                {
            //                    case WearableType.Eyes:
            //                    case WearableType.Hair:
            //                    case WearableType.Shape:
            //                    case WearableType.Skin:
            //                    //case WearableType.Underpants:
            //                        TryAndRepairBrokenWearable((WearableType)i, invService, userID, appearance);
            //                        resetwearable = true;
            //                        m_log.Warn("[AVFACTORY]: UUID.Zero Wearables, passing fake values.");
            //                        resetwearable = true;
            //                        break;
            //
            //                }
            //                continue;
            //            }
            //
            //            // Ignore ruth's assets except for the body parts! missing body parts fail avatar appearance on V1
            //            if (appearance.Wearables[i][j].ItemID == AvatarWearable.DefaultWearables[i][0].ItemID)
            //            {
            //                switch ((WearableType)i)
            //                {
            //                    case WearableType.Eyes:
            //                    case WearableType.Hair:
            //                    case WearableType.Shape:
            //                    case WearableType.Skin:
            //                    //case WearableType.Underpants:
            //                        TryAndRepairBrokenWearable((WearableType)i, invService, userID, appearance);
            //
            //                        m_log.WarnFormat("[AVFACTORY]: {0} Default Wearables, passing existing values.", (WearableType)i);
            //                        resetwearable = true;
            //                        break;
            //
            //                }
            //                continue;
            //            }
            //
            //            InventoryItemBase baseItem = new InventoryItemBase(appearance.Wearables[i][j].ItemID, userID);
            //            baseItem = invService.GetItem(baseItem);
            //
            //            if (baseItem != null)
            //            {
            //                appearance.Wearables[i].Add(appearance.Wearables[i][j].ItemID, baseItem.AssetID);
            //                int unmodifiedWearableIndexForClosure = i;
            //                m_scene.AssetService.Get(baseItem.AssetID.ToString(), this,
            //                                                          delegate(string x, object y, AssetBase z)
            //                                                          {
            //                                                              if (z == null)
            //                                                              {
            //                                                                  TryAndRepairBrokenWearable(
            //                                                                      (WearableType)unmodifiedWearableIndexForClosure, invService,
            //                                                                      userID, appearance);
            //                                                              }
            //                                                          });
            //            }
            //            else
            //            {
            //                m_log.ErrorFormat(
            //                    "[AVFACTORY]: Can't find inventory item {0} for {1}, setting to default",
            //                    appearance.Wearables[i][j].ItemID, (WearableType)i);
            //
            //                TryAndRepairBrokenWearable((WearableType)i, invService, userID, appearance);
            //                resetwearable = true;
            //
            //            }
            //        }
            //    }
            //
            //    // I don't know why we have to test for this again...  but the above switches do not capture these scenarios for some reason....
            //    if (appearance.Wearables[(int) WearableType.Eyes] == null)
            //    {
            //        m_log.WarnFormat("[AVFACTORY]: {0} Eyes are Null, passing existing values.", (WearableType.Eyes));
            //
            //        TryAndRepairBrokenWearable(WearableType.Eyes, invService, userID, appearance);
            //        resetwearable = true;
            //    }
            //    else
            //    {
            //        if (appearance.Wearables[(int) WearableType.Eyes][0].ItemID == UUID.Zero)
            //        {
            //            m_log.WarnFormat("[AVFACTORY]: Eyes are UUID.Zero are broken, {0} {1}",
            //                             appearance.Wearables[(int) WearableType.Eyes][0].ItemID,
            //                             appearance.Wearables[(int) WearableType.Eyes][0].AssetID);
            //            TryAndRepairBrokenWearable(WearableType.Eyes, invService, userID, appearance);
            //            resetwearable = true;
            //
            //        }
            //
            //    }
            //    // I don't know why we have to test for this again...  but the above switches do not capture these scenarios for some reason....
            //    if (appearance.Wearables[(int)WearableType.Shape] == null)
            //    {
            //        m_log.WarnFormat("[AVFACTORY]: {0} shape is Null, passing existing values.", (WearableType.Shape));
            //
            //        TryAndRepairBrokenWearable(WearableType.Shape, invService, userID, appearance);
            //        resetwearable = true;
            //    }
            //    else
            //    {
            //        if (appearance.Wearables[(int)WearableType.Shape][0].ItemID == UUID.Zero)
            //        {
            //            m_log.WarnFormat("[AVFACTORY]: Shape is UUID.Zero and broken, {0} {1}",
            //                             appearance.Wearables[(int)WearableType.Shape][0].ItemID,
            //                             appearance.Wearables[(int)WearableType.Shape][0].AssetID);
            //            TryAndRepairBrokenWearable(WearableType.Shape, invService, userID, appearance);
            //            resetwearable = true;
            //
            //        }
            //
            //    }
            //    // I don't know why we have to test for this again...  but the above switches do not capture these scenarios for some reason....
            //    if (appearance.Wearables[(int)WearableType.Hair] == null)
            //    {
            //        m_log.WarnFormat("[AVFACTORY]: {0} Hair is Null, passing existing values.", (WearableType.Hair));
            //
            //        TryAndRepairBrokenWearable(WearableType.Hair, invService, userID, appearance);
            //        resetwearable = true;
            //    }
            //    else
            //    {
            //        if (appearance.Wearables[(int)WearableType.Hair][0].ItemID == UUID.Zero)
            //        {
            //            m_log.WarnFormat("[AVFACTORY]: Hair is UUID.Zero and broken, {0} {1}",
            //                             appearance.Wearables[(int)WearableType.Hair][0].ItemID,
            //                             appearance.Wearables[(int)WearableType.Hair][0].AssetID);
            //            TryAndRepairBrokenWearable(WearableType.Hair, invService, userID, appearance);
            //            resetwearable = true;
            //
            //        }
            //
            //    }
            //    // I don't know why we have to test for this again...  but the above switches do not capture these scenarios for some reason....
            //    if (appearance.Wearables[(int)WearableType.Skin] == null)
            //    {
            //        m_log.WarnFormat("[AVFACTORY]: {0} Skin is Null, passing existing values.", (WearableType.Skin));
            //
            //        TryAndRepairBrokenWearable(WearableType.Skin, invService, userID, appearance);
            //        resetwearable = true;
            //    }
            //    else
            //    {
            //        if (appearance.Wearables[(int)WearableType.Skin][0].ItemID == UUID.Zero)
            //        {
            //            m_log.WarnFormat("[AVFACTORY]: Skin is UUID.Zero and broken, {0} {1}",
            //                             appearance.Wearables[(int)WearableType.Skin][0].ItemID,
            //                             appearance.Wearables[(int)WearableType.Skin][0].AssetID);
            //            TryAndRepairBrokenWearable(WearableType.Skin, invService, userID, appearance);
            //            resetwearable = true;
            //
            //        }
            //
            //    }
            //    if (resetwearable)
            //    {
            //        ScenePresence presence = null;
            //        if (m_scene.TryGetScenePresence(userID, out presence))
            //        {
            //            presence.ControllingClient.SendWearables(presence.Appearance.Wearables,
            //                                                     presence.Appearance.Serial++);
            //        }
            //    }
            //
            //}
            //else
            //{
            //    m_log.WarnFormat("[AVFACTORY]: user {0} has no inventory, appearance isn't going to work", userID);
            //}
        }

        private void TryAndRepairBrokenWearable(WearableType type, IInventoryService invService, UUID userID,AvatarAppearance appearance)
        {
            UUID defaultwearable = GetDefaultItem(type);
            if (!defaultwearable.IsZero())
            {
                UUID newInvItem = UUID.Random();
                InventoryItemBase itembase = new InventoryItemBase(newInvItem, userID)
                            {
                                AssetID = defaultwearable,
                                AssetType = (int)FolderType.BodyPart,
                                CreatorId = userID.ToString(),
                                //InvType = (int)InventoryType.Wearable,
                                Description = "Failed Wearable Replacement",
                                Folder = invService.GetFolderForType(userID, FolderType.BodyPart).ID,
                                Flags = (uint) type, Name = Enum.GetName(typeof (WearableType), type),
                                BasePermissions = (uint) PermissionMask.Copy,
                                CurrentPermissions = (uint) PermissionMask.Copy,
                                EveryOnePermissions = (uint) PermissionMask.Copy,
                                GroupPermissions = (uint) PermissionMask.Copy,
                                NextPermissions = (uint) PermissionMask.Copy
                            };
                invService.AddItem(itembase);
                UUID LinkInvItem = UUID.Random();
                itembase = new InventoryItemBase(LinkInvItem, userID)
                            {
                                AssetID = newInvItem,
                                AssetType = (int)AssetType.Link,
                                CreatorId = userID.ToString(),
                                InvType = (int) InventoryType.Wearable,
                                Description = "Failed Wearable Replacement",
                                Folder = invService.GetFolderForType(userID, FolderType.CurrentOutfit).ID,
                                Flags = (uint) type,
                                Name = Enum.GetName(typeof (WearableType), type),
                                BasePermissions = (uint) PermissionMask.Copy,
                                CurrentPermissions = (uint) PermissionMask.Copy,
                                EveryOnePermissions = (uint) PermissionMask.Copy,
                                GroupPermissions = (uint) PermissionMask.Copy,
                                NextPermissions = (uint) PermissionMask.Copy
                            };
                invService.AddItem(itembase);
                appearance.Wearables[(int)type] = new AvatarWearable(newInvItem, GetDefaultItem(type));
                ScenePresence presence = null;
                if (m_scene.TryGetScenePresence(userID, out presence))
                {
                    m_scene.SendInventoryUpdate(presence.ControllingClient,
                                invService.GetFolderForType(userID, FolderType.CurrentOutfit), false, true);
                }
            }
        }

        private UUID GetDefaultItem(WearableType wearable)
        {
            // These are ruth
            UUID ret = UUID.Zero;
            switch (wearable)
            {
                case WearableType.Eyes:
                    ret = new UUID("4bb6fa4d-1cd2-498a-a84c-95c1a0e745a7");
                    break;
                case WearableType.Hair:
                    ret = new UUID("d342e6c0-b9d2-11dc-95ff-0800200c9a66");
                    break;
                case WearableType.Pants:
                    ret = new UUID("00000000-38f9-1111-024e-222222111120");
                    break;
                case WearableType.Shape:
                    ret = new UUID("66c41e39-38f9-f75a-024e-585989bfab73");
                    break;
                case WearableType.Shirt:
                    ret = new UUID("00000000-38f9-1111-024e-222222111110");
                    break;
                case WearableType.Skin:
                    ret = new UUID("77c41e39-38f9-f75a-024e-585989bbabbb");
                    break;
                case WearableType.Undershirt:
                    ret = new UUID("16499ebb-3208-ec27-2def-481881728f47");
                    break;
                case WearableType.Underpants:
                    ret = new UUID("4ac2e9c7-3671-d229-316a-67717730841d");
                    break;
            }

            return ret;
        }
        #endregion

        #region Client Event Handlers
        /// <summary>
        /// Tell the client for this scene presence what items it should be wearing now
        /// </summary>
        /// <param name="client"></param>
        private void Client_OnRequestWearables(IClientAPI client)
        {
            Util.FireAndForget(delegate(object x)
            {
                Thread.Sleep(4000);

                // m_log.DebugFormat("[AVFACTORY]: Client_OnRequestWearables called for {0} ({1})", client.Name, client.AgentId);
                ScenePresence sp = m_scene.GetScenePresence(client.AgentId);
                if (sp != null)
                    client.SendWearables(sp.Appearance.Wearables, sp.Appearance.Serial);
                else
                    m_log.WarnFormat("[AVFACTORY]: Client_OnRequestWearables unable to find presence for {0}", client.AgentId);
            }, null, "AvatarFactoryModule.OnClientRequestWearables");
        }

        /// <summary>
        /// Set appearance data (texture asset IDs and slider settings) received from a client
        /// </summary>
        /// <param name="client"></param>
        /// <param name="texture"></param>
        /// <param name="visualParam"></param>
        private void Client_OnSetAppearance(IClientAPI client, Primitive.TextureEntry textureEntry, byte[] visualParams, Vector3 avSize, WearableCacheItem[] cacheItems)
        {
            // m_log.WarnFormat("[AVFACTORY]: Client_OnSetAppearance called for {0} ({1})", client.Name, client.AgentId);
            ScenePresence sp = m_scene.GetScenePresence(client.AgentId);
            if (sp != null)
                SetAppearance(sp, textureEntry, visualParams, avSize, cacheItems);
            else
                m_log.WarnFormat("[AVFACTORY]: Client_OnSetAppearance unable to find presence for {0}", client.AgentId);
        }

        /// <summary>
        /// Update what the avatar is wearing using an item from their inventory.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="e"></param>
        private void Client_OnAvatarNowWearing(IClientAPI client, AvatarWearingArgs e)
        {
            // m_log.WarnFormat("[AVFACTORY]: Client_OnAvatarNowWearing called for {0} ({1})", client.Name, client.AgentId);
            ScenePresence sp = m_scene.GetScenePresence(client.AgentId);
            if (sp == null)
            {
                m_log.WarnFormat("[AVFACTORY]: Client_OnAvatarNowWearing unable to find presence for {0}", client.AgentId);
                return;
            }

            // operate on a copy of the appearance so we don't have to lock anything yet
            AvatarAppearance avatAppearance = new AvatarAppearance(sp.Appearance, false);

            foreach (AvatarWearingArgs.Wearable wear in e.NowWearing)
            {
                // If the wearable type is larger than the current array, expand it
                if (avatAppearance.Wearables.Length <= wear.Type)
                {
                    int currentLength = avatAppearance.Wearables.Length;
                    AvatarWearable[] wears = avatAppearance.Wearables;
                    Array.Resize(ref wears, wear.Type + 1);
                    for (int i = currentLength ; i <= wear.Type ; i++)
                        wears[i] = new AvatarWearable();
                    avatAppearance.Wearables = wears;
                }
                avatAppearance.Wearables[wear.Type].Add(wear.ItemID, UUID.Zero);
            }

            avatAppearance.GetAssetsFrom(sp.Appearance);

            lock (m_setAppearanceLock)
            {
                // Update only those fields that we have changed. This is important because the viewer
                // often sends AvatarIsWearing and SetAppearance packets at once, and AvatarIsWearing
                // shouldn't overwrite the changes made in SetAppearance.
                sp.Appearance.Wearables = avatAppearance.Wearables;
                // We don't need to send the appearance here since the "iswearing" will trigger a new set
                // of visual param and baked texture changes. When those complete, the new appearance will be sent
                QueueAppearanceSave(client.AgentId);
            }
        }

/*
        /// <summary>
        /// Respond to the cached textures request from the client
        /// </summary>
        /// <param name="client"></param>
        /// <param name="serial"></param>
        /// <param name="cachedTextureRequest"></param>
        private void Client_OnCachedTextureRequest(IClientAPI client, int serial, List<CachedTextureRequestArg> cachedTextureRequest)
        {
            // m_log.WarnFormat("[AVFACTORY]: Client_OnCachedTextureRequest called for {0} ({1})", client.Name, client.AgentId);
            ScenePresence sp = m_scene.GetScenePresence(client.AgentId);

            List<CachedTextureResponseArg> cachedTextureResponse = new List<CachedTextureResponseArg>();
            foreach (CachedTextureRequestArg request in cachedTextureRequest)
            {
                UUID texture = UUID.Zero;
                int index = request.BakedTextureIndex;

                if (m_reusetextures)
                {
                    Primitive.TextureEntryFace face = sp.Appearance.Texture.FaceTextures[index];
                    if (face != null)
                        texture = face.TextureID;
                }

                CachedTextureResponseArg response = new CachedTextureResponseArg();
                response.BakedTextureIndex = index;
                response.BakedTextureID = texture;
                response.HostName = null;

                cachedTextureResponse.Add(response);
            }
            client.SendCachedTextureResponse(sp, serial, cachedTextureResponse);
        }
*/

        #endregion

        public void WriteBakedTexturesReport(IScenePresence sp, ReportOutputAction outputAction)
        {
            outputAction("For {0} in {1}", sp.Name, m_scene.RegionInfo.RegionName);
            outputAction(BAKED_TEXTURES_REPORT_FORMAT, "Bake Type", "UUID");

            Dictionary<BakeType, Primitive.TextureEntryFace> bakedTextures = GetBakedTextureFaces(sp.UUID);

            foreach (BakeType bt in bakedTextures.Keys)
            {
                string rawTextureID;

                if (bakedTextures[bt] == null)
                {
                    rawTextureID = "not set";
                }
                else
                {
                    if(bakedTextures[bt].TextureID == AppearanceManager.DEFAULT_AVATAR_TEXTURE)
                        rawTextureID = "not set";
                    else
                    {
                        rawTextureID = bakedTextures[bt].TextureID.ToString();

                        if (m_scene.AssetService.Get(rawTextureID) == null)
                            rawTextureID += " (not found)";
                        else
                            rawTextureID += " (uploaded)";
                    }
                }

                outputAction(BAKED_TEXTURES_REPORT_FORMAT, bt, rawTextureID);
            }

            bool bakedTextureValid = m_scene.AvatarFactory.ValidateBakedTextureCache(sp);
            outputAction("{0} baked appearance texture is {1}", sp.Name, bakedTextureValid ? "OK" : "incomplete");
        }

        public void SetPreferencesHoverZ(UUID agentId, float val)
        {
            ScenePresence sp = m_scene.GetScenePresence(agentId);
            if (sp == null || sp.IsDeleted || sp.IsNPC || sp.IsInTransit)
                return;
            float last = sp.Appearance.AvatarPreferencesHoverZ;
            if(val != last)
            {
                sp.Appearance.AvatarPreferencesHoverZ = val;
                //sp.SendAppearanceToAgentNF(sp);
                QueueAppearanceSend(agentId);
            }
        }
    }
}
