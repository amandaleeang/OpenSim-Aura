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
using System.Reflection;
using System.Text;

using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Serialization.External;

using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.Framework.InventoryAccess
{
    public class HGAssetMapper
    {
        #region Fields
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // This maps between inventory server urls and inventory server clients
//        private Dictionary<string, InventoryClient> m_inventoryServers = new Dictionary<string, InventoryClient>();

        private Scene m_scene;
        private string m_HomeURI;

        /// <summary>Configured HG fetch concurrency / timeout (ISSUE-009), mirrors [EntityTransfer] HGAssetFetchConcurrency / HGAssetFetchTimeoutMs.</summary>
        private static int s_FetchConcurrency = 8;
        private static int s_FetchTimeoutMs = 8000;

        #endregion

        #region Constructor

        public HGAssetMapper(Scene scene, string homeURL)
        {
            m_scene = scene;
            m_HomeURI = homeURL;
        }

        /// <summary>
        /// Process-wide defaults for the asset mapper's gatherer (ISSUE-009).
        /// Set from the same [EntityTransfer] keys the appearance gather uses.
        /// </summary>
        public static void ConfigureFetch(int concurrency, int timeoutMs)
        {
            if (concurrency < 1)
                concurrency = 1;
            if (concurrency > 32)
                concurrency = 32;
            if (timeoutMs < 500)
                timeoutMs = 500;

            s_FetchConcurrency = concurrency;
            s_FetchTimeoutMs = timeoutMs;
        }

        #endregion

        #region Internal functions

        private AssetMetadata FetchMetadata(string url, UUID assetID)
        {
            if (string.IsNullOrEmpty(url))
                return null;

            string assetIDstr = assetID.ToString();
            // Test if it's already here
            AssetMetadata meta = m_scene.AssetService.GetMetadata(assetIDstr);
            if (meta == null)
            {
                if (!url.EndsWith("/") && !url.EndsWith("="))
                    url = url + "/";

                meta = m_scene.AssetService.GetMetadata(url + assetIDstr);

                if (meta != null)
                    m_log.DebugFormat("[HG ASSET MAPPER]: Fetched metadata for asset {0} of type {1} from {2} ", assetIDstr, meta.Type, url);
                else
                    m_log.DebugFormat("[HG ASSET MAPPER]: Unable to fetched metadata for asset {0} from {1} ", assetIDstr, url);
            }
            return meta;
        }

        public bool PostAsset(string url, AssetBase asset, bool verbose = true)
        {
            if (asset == null)
            {
                m_log.Warn("[HG ASSET MAPPER]: Tried to post asset to remote server, but asset not in local cache.");
                return false;
            }

            if (string.IsNullOrEmpty(url))
                return false;

            if (!url.EndsWith("/") && !url.EndsWith("="))
                url = url + "/";

            // See long comment in AssetCache.AddAsset
            if (asset.Temporary || asset.Local)
                return true;

            // We need to copy the asset into a new asset, because
            // we need to set its ID to be URL+UUID, so that the
            // HGAssetService dispatches it to the remote grid.
            // It's not pretty, but the best that can be done while
            // not having a global naming infrastructure
            AssetBase asset1 = new AssetBase(asset.FullID, asset.Name, asset.Type, asset.Metadata.CreatorID);
            Copy(asset, asset1);
            asset1.ID = url + asset.ID;

            AdjustIdentifiers(asset1.Metadata);
            if (asset1.Metadata.Type == (sbyte)AssetType.Object)
                asset1.Data = AdjustIdentifiers(asset.Data);
            else
                asset1.Data = asset.Data;

            string id = m_scene.AssetService.Store(asset1);
            if (string.IsNullOrEmpty(id))
            {
                if (verbose)
                    m_log.DebugFormat("[HG ASSET MAPPER]: Asset server {0} did not accept {1}", url, asset.ID);
                return false;
            }

            if (verbose)
                m_log.DebugFormat("[HG ASSET MAPPER]: Posted copy of asset {0} from local asset server to {1}", asset1.ID, url);
            return true;
        }

        private void Copy(AssetBase from, AssetBase to)
        {
            //to.Data        = from.Data; // don't copy this, it's copied elsewhere
            to.Description = from.Description;
            to.FullID      = from.FullID;
            to.ID          = from.ID;
            to.Local       = from.Local;
            to.Name        = from.Name;
            to.Temporary   = from.Temporary;
            to.Type        = from.Type;
        }

        private void AdjustIdentifiers(AssetMetadata meta)
        {
            if (!string.IsNullOrEmpty(meta.CreatorID))
            {
                if(UUID.TryParse(meta.CreatorID, out UUID uuid))
                {
                    UserAccount creator = m_scene.UserAccountService.GetUserAccount(m_scene.RegionInfo.ScopeID, uuid);
                    if (creator != null)
                        meta.CreatorID = m_HomeURI + ";" + creator.FirstName + " " + creator.LastName;
                }
            }
        }

        protected byte[] AdjustIdentifiers(byte[] data)
        {
            string xml = Utils.BytesToString(data);
            return Utils.StringToBytes(RewriteSOP(xml));
        }

        protected string RewriteSOP(string xmlData)
        {
//            Console.WriteLine("Input XML [{0}]", xmlData);
            return ExternalRepresentationUtils.RewriteSOP(xmlData, m_scene.Name, m_HomeURI, m_scene.UserAccountService, m_scene.RegionInfo.ScopeID);

        }

        // TODO: unused
        // private void Dump(Dictionary<UUID, bool> lst)
        // {
        //     m_log.Debug("XXX -------- UUID DUMP ------- XXX");
        //     foreach (KeyValuePair<UUID, bool> kvp in lst)
        //         m_log.Debug(" >> " + kvp.Key + " (texture? " + kvp.Value + ")");
        //     m_log.Debug("XXX -------- UUID DUMP ------- XXX");
        // }

        #endregion


        #region Public interface

        public void Get(UUID assetID, UUID ownerID, string userAssetURL)
        {
            // The act of gathering UUIDs downloads some assets from the remote server
            // but not all...
            if(string.IsNullOrEmpty(userAssetURL))
            {
                m_log.Debug($"[HG ASSET MAPPER]: Problems getting item asset {assetID}. Asset server unknown");
                return;
            }

            HGUuidGatherer uuidGatherer = new(m_scene.AssetService, userAssetURL);
            uuidGatherer.FetchConcurrency = s_FetchConcurrency;
            uuidGatherer.FetchTimeoutMs = s_FetchTimeoutMs;
            uuidGatherer.AddForInspection(assetID);
            // ISSUE-009: wave-parallel gather (nested UUIDs fetched concurrently, same as the
            // avatar-login appearance gather) instead of the old serial one-GET-at-a-time GatherAll.
            uuidGatherer.GatherAllParallel();

            // ISSUE-011: this object is becoming a persistent scene object (rez / drop-in), so any
            // asset that previously arrived only as a transient HG login-attachment fetch (cache-only,
            // never written to the local DB) must be promoted to the local asset database now.
            // Otherwise it would live only in the file cache and grey out after the cache expires.
            PromoteCacheOnlyAssetsToDatabase(assetID, uuidGatherer);

            m_log.Debug($"[HG ASSET MAPPER]: Preparing to get {uuidGatherer.GatheredUuids.Count} assets");
            bool success = true;
            if (uuidGatherer.FailedUUIDs.Count > 0)
                success = false;

            // maybe all pieces got here...
            if (!success)
                m_log.Debug($"[HG ASSET MAPPER]: Problems getting item asset {assetID} from asset server {userAssetURL}");
            else
                m_log.Debug($"[HG ASSET MAPPER]: Successfully got item asset {assetID} from asset server {userAssetURL}");
        }

        /// <summary>
        /// Writes cache-only assets referenced by a now-persistent scene object to the local asset
        /// database (ISSUE-011). Assets fetched transiently for HG login attachments live only in the
        /// file cache; promote them so a rezzed/dropped object survives cache expiry.
        /// </summary>
        private void PromoteCacheOnlyAssetsToDatabase(UUID rootAssetID, HGUuidGatherer uuidGatherer)
        {
            if (uuidGatherer.GatheredUuids.Count == 0)
                return;

            string[] ids = new string[uuidGatherer.GatheredUuids.Count];
            int idx = 0;
            foreach (UUID id in uuidGatherer.GatheredUuids.Keys)
                ids[idx++] = id.ToString();

            bool[] exist;
            try
            {
                exist = m_scene.AssetService.AssetsExist(ids);
            }
            catch (Exception e)
            {
                m_log.Debug($"[HG ASSET MAPPER]: Asset existence check failed for {rootAssetID}: {e.Message}");
                return;
            }

            if (exist is null)
                return;

            int promoted = 0;
            idx = 0;
            foreach (UUID id in uuidGatherer.GatheredUuids.Keys)
            {
                if (exist[idx])
                {
                    idx++;
                    continue;
                }

                try
                {
                    AssetBase cached = m_scene.AssetService.GetCached(id.ToString());
                    if (cached != null)
                    {
                        string stored = m_scene.AssetService.Store(cached);
                        if (!string.IsNullOrEmpty(stored))
                            promoted++;
                    }
                }
                catch (Exception e)
                {
                    m_log.Debug($"[HG ASSET MAPPER]: Promote failed for {id}: {e.Message}");
                }
                idx++;
            }

            if (promoted > 0)
                m_log.DebugFormat(
                    "[HG ASSET MAPPER]: Promoted {0} cache-only asset(s) to persistent local store for item {1}",
                    promoted, rootAssetID);
        }

        public void Post(UUID assetID, UUID ownerID, string userAssetURL)
        {
            AssetBase asset = m_scene.AssetService.Get(assetID.ToString());
            if (asset == null)
            {
                m_log.DebugFormat("[HG ASSET MAPPER POST]: Something wrong with asset {0}, it could not be found", assetID);
                return;
            }
            m_log.DebugFormat("[HG ASSET MAPPER  POST]: Starting to send asset {0} to asset server {1}", assetID, userAssetURL);

            // Find all the embedded assets
            HGUuidGatherer uuidGatherer = new HGUuidGatherer(m_scene.AssetService, string.Empty);
            uuidGatherer.AddForInspection(asset.FullID);
            uuidGatherer.GatherAll(true);

            // Check which assets already exist in the destination server

            string url = userAssetURL;
            if (!url.EndsWith('/') && !url.EndsWith('='))
                url = url + "/";

            string[] remoteAssetIDs = new string[uuidGatherer.GatheredUuids.Count];
            int i = 0;
            foreach (UUID id in uuidGatherer.GatheredUuids.Keys)
                remoteAssetIDs[i++] = url + id.ToString();

            bool[] exist;
            try
            {
                exist = m_scene.AssetService.AssetsExist(remoteAssetIDs);
            }
            catch
            {
                m_log.DebugFormat("[HG ASSET MAPPER POST]: Problems sending asset {0} to asset server {1}", assetID, userAssetURL);
                return;
            }

            var existSet = new HashSet<string>();
            i = 0;
            foreach (UUID id in uuidGatherer.GatheredUuids.Keys)
            {
                if (exist[i])
                    existSet.Add(id.ToString());
                ++i;
            }

            // Send only those assets which don't already exist in the destination server

            bool success = true;
            var notFound = new List<string>();
            var posted = new List<string>();

            List<UUID> toPost = new(uuidGatherer.GatheredUuids.Count);
            foreach (UUID uuid in uuidGatherer.GatheredUuids.Keys)
            {
                if (!existSet.Contains(uuid.ToString()))
                    toPost.Add(uuid);
            }

            if (toPost.Count > 0)
            {
                // ISSUE-009: bounded-parallel push to the foreign asset server (was one POST at a time).
                // A single bad asset no longer aborts the whole push (logs instead).
                int concurrency = Math.Min(8, Math.Max(1, toPost.Count));
                System.Threading.Tasks.Parallel.ForEach(toPost,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = concurrency },
                    uuid =>
                    {
                        string idstr = uuid.ToString();
                        AssetBase localAsset = m_scene.AssetService.Get(idstr);
                        if (localAsset == null)
                        {
                            lock (notFound)
                                notFound.Add(idstr);
                            return;
                        }

                        try
                        {
                            bool b = PostAsset(userAssetURL, localAsset, false);
                            if (b)
                            {
                                lock (posted)
                                    posted.Add(idstr);
                            }
                            else
                                success = false;
                        }
                        catch (Exception e)
                        {
                            m_log.Error(
                                string.Format(
                                    "[HG ASSET MAPPER POST]: Failed to post asset {0} (type {1}, length {2}) referenced from {3} to {4} with exception  ",
                                    localAsset.ID, localAsset.Type, localAsset.Data.Length, assetID, userAssetURL),
                                e);
                            success = false;
                        }
                    });
            }
            StringBuilder sb = null;
            if (notFound.Count > 0)
            {
                if (sb == null)
                    sb = new StringBuilder(512);
                i = notFound.Count - 1;
                sb.Append("[HG ASSET MAPPER POST]: did not find embedded UUIDs as assets:\n\t");
                for (int j = 0; j < notFound.Count; ++j)
                {
                    sb.Append(notFound[j]);
                    if (j < i)
                        sb.Append(',');
                }
                m_log.Debug(sb.ToString());
                sb.Clear();
            }
            if (existSet.Count > 0)
            {
                if (sb == null) 
                    sb = new StringBuilder(512);
                i = existSet.Count;
                sb.Append("[HG ASSET MAPPER POST]: embedded assets already at destination server:\n\t");
                foreach (UUID id in existSet)
                {
                    sb.Append(id);
                    if (--i > 0)
                        sb.Append(',');
                }
                m_log.Debug(sb.ToString());
                sb.Clear();
            }
            if (posted.Count > 0)
            {
                if (sb == null) 
                    sb = new StringBuilder(512);
                i = posted.Count - 1;
                sb.Append("[HG ASSET MAPPER POST]: Posted assets:\n\t");
                for (int j = 0; j < posted.Count; ++j)
                {
                    sb.Append(posted[j]);
                    if (j < i)
                        sb.Append(',');
                }
                m_log.Debug(sb.ToString());
            }

            if (!success)
                m_log.DebugFormat("[HG ASSET MAPPER POST]: Problems sending asset {0} to asset server {1}", assetID, userAssetURL);
            else
                m_log.DebugFormat("[HG ASSET MAPPER POST]: Successfully sent asset {0} to asset server {1}", assetID, userAssetURL);
        }

        #endregion

    }
}
