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
using System.Threading;

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
        private readonly bool m_concurrent;
        private readonly int m_waveSize;
        private readonly int m_timeoutMs;

        #endregion

        #region Constructor

        public HGAssetMapper(Scene scene, string homeURL, bool concurrent = true, int waveSize = 8, int timeoutSec = 30)
        {
            m_scene = scene;
            m_HomeURI = homeURL;
            m_concurrent = concurrent;
            m_waveSize = waveSize < 1 ? 1 : waveSize;
            m_timeoutMs = timeoutSec < 1 ? 1000 : timeoutSec * 1000;
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

        private AssetBase FetchAsset(string url, UUID assetID)
        {
            return m_scene.AssetService.Get(assetID.ToString(), url, true);
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
            Get(new[] { assetID }, ownerID, userAssetURL);
        }

        public void Get(IEnumerable<UUID> assetIDs, UUID ownerID, string userAssetURL)
        {
            if (string.IsNullOrEmpty(userAssetURL))
            {
                m_log.Debug("[HG ASSET MAPPER]: Problems getting item assets. Asset server unknown");
                return;
            }

            HGUuidGatherer uuidGatherer = new(m_scene.AssetService, userAssetURL);
            UUID first = UUID.Zero;
            int added = 0;
            foreach (UUID id in assetIDs)
            {
                if (id.IsZero())
                    continue;
                if (added == 0)
                    first = id;
                uuidGatherer.AddForInspection(id);
                added++;
            }
            if (added == 0)
                return;

            if (m_concurrent)
                uuidGatherer.GatherAllConcurrent(m_waveSize, m_timeoutMs);
            else
            {
                uuidGatherer.GatherAll();
                foreach (UUID uuid in uuidGatherer.GatheredUuids.Keys)
                    FetchAsset(userAssetURL, uuid);
            }

            bool success = uuidGatherer.FailedUUIDs.Count == 0;
            if (!success)
                m_log.Debug($"[HG ASSET MAPPER]: Problems getting {added} item asset(s) (first {first}) from asset server {userAssetURL}");
            else
                m_log.Debug($"[HG ASSET MAPPER]: Successfully got {added} item asset(s) (first {first}, gathered {uuidGatherer.GatheredUuids.Count}) from asset server {userAssetURL}");
        }

        public void Post(UUID assetID, UUID ownerID, string userAssetURL)
        {
            Post(new[] { assetID }, ownerID, userAssetURL);
        }

        public void Post(IEnumerable<UUID> assetIDs, UUID ownerID, string userAssetURL)
        {
            if (string.IsNullOrEmpty(userAssetURL))
                return;

            HGUuidGatherer uuidGatherer = new HGUuidGatherer(m_scene.AssetService, string.Empty);
            UUID first = UUID.Zero;
            int added = 0;
            foreach (UUID id in assetIDs)
            {
                if (id.IsZero())
                    continue;
                if (added == 0)
                    first = id;
                uuidGatherer.AddForInspection(id);
                added++;
            }
            if (added == 0)
                return;

            m_log.DebugFormat("[HG ASSET MAPPER  POST]: Starting to send {0} asset(s) (first {1}) to asset server {2}", added, first, userAssetURL);

            if (m_concurrent)
                uuidGatherer.GatherAllConcurrent(m_waveSize, m_timeoutMs);
            else
                uuidGatherer.GatherAll(true);

            if (uuidGatherer.GatheredUuids.Count == 0)
            {
                m_log.DebugFormat("[HG ASSET MAPPER POST]: Something wrong with asset {0}, it could not be found", first);
                return;
            }

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
                m_log.DebugFormat("[HG ASSET MAPPER POST]: Problems sending asset {0} to asset server {1}", first, userAssetURL);
                return;
            }

            var existSet = new HashSet<string>();
            i = 0;
            foreach (UUID id in uuidGatherer.GatheredUuids.Keys)
            {
                if (exist != null && i < exist.Length && exist[i])
                    existSet.Add(id.ToString());
                ++i;
            }

            var notFound = new List<string>();
            var toPost = new List<AssetBase>();

            foreach (UUID uuid in uuidGatherer.GatheredUuids.Keys)
            {
                string idstr = uuid.ToString();
                if (existSet.Contains(idstr))
                    continue;

                AssetBase asset = m_scene.AssetService.Get(idstr);
                if (asset == null)
                {
                    notFound.Add(idstr);
                    continue;
                }
                toPost.Add(asset);
            }

            var posted = new List<string>();
            bool success = true;

            if (toPost.Count > 0)
            {
                if (m_concurrent)
                    success &= PostAssetWave(userAssetURL, toPost, first, posted);
                else
                {
                    foreach (AssetBase asset in toPost)
                    {
                        try
                        {
                            bool b = PostAsset(userAssetURL, asset, false);
                            if (b)
                                posted.Add(asset.ID);
                            success &= b;
                        }
                        catch (Exception e)
                        {
                            m_log.Error(
                                string.Format(
                                    "[HG ASSET MAPPER POST]: Failed to post asset {0} (type {1}, length {2}) referenced from {3} to {4} with exception  ",
                                    asset.ID, asset.Type, asset.Data != null ? asset.Data.Length : 0, first, userAssetURL),
                                e);
                            throw;
                        }
                    }
                }
            }

            StringBuilder sb = null;
            if (notFound.Count > 0)
            {
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
                sb ??= new StringBuilder(512);
                i = existSet.Count;
                sb.Append("[HG ASSET MAPPER POST]: embedded assets already at destination server:\n\t");
                foreach (string id in existSet)
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
                sb ??= new StringBuilder(512);
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
                m_log.DebugFormat("[HG ASSET MAPPER POST]: Problems sending asset {0} to asset server {1}", first, userAssetURL);
            else
                m_log.DebugFormat("[HG ASSET MAPPER POST]: Successfully sent asset {0} to asset server {1}", first, userAssetURL);
        }

        /// <summary>
        /// Post missing assets in waves of at most m_waveSize concurrent Stores.
        /// Each worker copies the asset (PostAsset already copies) so instances are not shared across threads.
        /// </summary>
        private bool PostAssetWave(string userAssetURL, List<AssetBase> assets, UUID referencedFrom, List<string> posted)
        {
            bool success = true;
            int offset = 0;
            while (offset < assets.Count)
            {
                int n = Math.Min(m_waveSize, assets.Count - offset);
                bool[] ok = new bool[n];
                Exception[] errors = new Exception[n];
                ManualResetEventSlim[] done = new ManualResetEventSlim[n];

                for (int i = 0; i < n; i++)
                {
                    done[i] = new ManualResetEventSlim(false);
                    int idx = i;
                    AssetBase asset = assets[offset + i];
                    Util.FireAndForget(_ =>
                    {
                        try { ok[idx] = PostAsset(userAssetURL, asset, false); }
                        catch (Exception e) { errors[idx] = e; }
                        finally { try { done[idx].Set(); } catch (ObjectDisposedException) { } }
                    }, null, "HGAssetMapper.PostAsset");
                }

                WaitHandle[] handles = new WaitHandle[n];
                for (int i = 0; i < n; i++)
                    handles[i] = done[i].WaitHandle;
                WaitHandle.WaitAll(handles, m_timeoutMs);

                try
                {
                    for (int i = 0; i < n; i++)
                    {
                        AssetBase asset = assets[offset + i];
                        if (!done[i].IsSet)
                        {
                            m_log.Debug($"[HG ASSET MAPPER POST]: Post of asset {asset.ID} timed out after {m_timeoutMs} ms");
                            success = false;
                        }
                        else if (errors[i] != null)
                        {
                            m_log.Error(
                                string.Format(
                                    "[HG ASSET MAPPER POST]: Failed to post asset {0} (type {1}, length {2}) referenced from {3} to {4} with exception  ",
                                    asset.ID, asset.Type, asset.Data != null ? asset.Data.Length : 0, referencedFrom, userAssetURL),
                                errors[i]);
                            throw errors[i];
                        }
                        else if (ok[i])
                            posted.Add(asset.ID);
                        else
                            success = false;
                    }
                }
                finally
                {
                    for (int i = 0; i < n; i++)
                        done[i].Dispose();
                }

                offset += n;
            }
            return success;
        }

        #endregion

    }
}
