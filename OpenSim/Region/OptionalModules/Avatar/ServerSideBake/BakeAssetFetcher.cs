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
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBake
{
    /// <summary>
    /// Wearable textures: cache, local assets, HG /assets/.
    /// Bake JPEGs: GetBake uses Flotsam, XBakes, then origin /appearance.
    /// </summary>
    public class BakeAssetFetcher
    {
        private readonly ILog m_log;
        private readonly IAssetService m_assets;
        private readonly IAssetCache m_cache;
        private readonly ConcurrentDictionary<string, byte> m_negative = new ConcurrentDictionary<string, byte>();

        public BakeAssetFetcher(ILog log, IAssetService assets, IAssetCache cache)
        {
            m_log = log;
            m_assets = assets;
            m_cache = cache;
        }

        public AssetBase Get(UUID id, string foreignAssetService, out string source)
        {
            source = "missing";
            if (BakeLayerMap.IsUnsetTexture(id))
                return null;

            string key = id.ToString();
            if (m_negative.ContainsKey(key))
            {
                source = "negative-cache";
                return null;
            }

            AssetBase asset = null;
            if (m_cache != null)
            {
                asset = m_cache.GetCached(key);
                if (asset != null && asset.Data != null && asset.Data.Length > 0)
                {
                    source = "cache";
                    return asset;
                }
            }

            if (m_assets != null)
            {
                asset = m_assets.Get(key);
                if (asset != null && asset.Data != null && asset.Data.Length > 0)
                {
                    source = "local";
                    m_cache?.Cache(asset);
                    return asset;
                }

                if (!string.IsNullOrEmpty(foreignAssetService))
                {
                    asset = m_assets.Get(key, foreignAssetService, true);
                    if (asset != null && asset.Data != null && asset.Data.Length > 0)
                    {
                        source = "hg:" + foreignAssetService;
                        m_cache?.Cache(asset);
                        return asset;
                    }
                }
            }

            m_negative[key] = 1;
            m_log.DebugFormat("[SSBAKE]: asset {0} not found (foreign={1})", key,
                string.IsNullOrEmpty(foreignAssetService) ? "-" : foreignAssetService);
            return null;
        }

        /// <summary>
        /// Published bake JPEG: local Flotsam, then local XBakes, then origin
        /// GET /appearance/texture/{agent}/{slot}/{id}. Never HG /assets/.
        /// </summary>
        public AssetBase GetBake(UUID id, UUID agentId, int slot, string appearanceService,
            WearableCacheItem[] xbakesStored, out string source)
        {
            source = "missing";
            if (BakeLayerMap.IsUnsetTexture(id))
                return null;

            string key = id.ToString();
            if (m_cache != null)
            {
                AssetBase cached = m_cache.GetCached(key);
                if (cached != null && cached.Data != null && cached.Data.Length > 0)
                {
                    source = "cache";
                    return cached;
                }
            }

            AssetBase fromXBakes = FromXBakes(id, agentId, xbakesStored);
            if (fromXBakes != null)
            {
                source = "xbakes";
                return fromXBakes;
            }

            if (string.IsNullOrEmpty(appearanceService))
                return null;

            byte[] data = FetchAppearanceJpeg(appearanceService, agentId, slot, id);
            if (data == null || data.Length == 0)
            {
                m_log.DebugFormat("[SSBAKE]: bake {0} not found (appearance={1})", key, appearanceService);
                return null;
            }

            source = "appearance:" + appearanceService;
            return CacheLocal(id, agentId, data);
        }

        private AssetBase FromXBakes(UUID id, UUID agentId, WearableCacheItem[] xbakesStored)
        {
            if (xbakesStored == null)
                return null;

            for (int i = 0; i < xbakesStored.Length; i++)
            {
                WearableCacheItem item = xbakesStored[i];
                if (item?.TextureAsset?.Data == null || item.TextureAsset.Data.Length == 0)
                    continue;
                if (item.TextureID.NotEqual(id) && item.CacheId.NotEqual(id))
                    continue;
                return CacheLocal(id, agentId, item.TextureAsset.Data);
            }
            return null;
        }

        private AssetBase CacheLocal(UUID id, UUID agentId, byte[] data)
        {
            AssetBase baked = new AssetBase(id, "SSBake", (sbyte)AssetType.Texture, agentId.ToString());
            baked.Data = data;
            baked.Temporary = true;
            baked.Local = true;
            m_cache?.Cache(baked, true);
            return baked;
        }

        private byte[] FetchAppearanceJpeg(string appearanceBase, UUID agentId, int slot, UUID textureId)
        {
            string baseUrl = appearanceBase.TrimEnd('/') + "/";
            string url = string.Format("{0}texture/{1}/{2}/{3}", baseUrl, agentId, slot, textureId);
            try
            {
                using HttpClient client = WebUtil.GetNewGlobalHttpClient(8000);
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
                using HttpResponseMessage response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                    return null;
                using Stream stream = response.Content.ReadAsStream();
                using MemoryStream ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.Length > 0 ? ms.ToArray() : null;
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[SSBAKE]: GET {0} failed: {1}", url, e.Message);
                return null;
            }
        }
    }
}
