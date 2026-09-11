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

using System.Collections.Concurrent;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBake
{
    /// <summary>
    /// Fetch an asset: memory cache, local asset service, then HG foreign service.
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
    }
}
