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

using log4net;
using Mono.Addins;
using Nini.Config;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Timers;

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;


namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Asset
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionAssetConnector")]
    public class RegionAssetConnector : ISharedRegionModule, IAssetService, IForeignOnlyAssetGetter
    {
        private static readonly ILog m_log = LogManager.GetLogger( MethodBase.GetCurrentMethod().DeclaringType);

        private bool m_Enabled = false;

        private Scene m_aScene;

        private IAssetCache m_Cache;
        private IAssetService m_localConnector;
        private IAssetService m_HGConnector;
        private AssetPermissions m_AssetPerms;

        //const int MAXSENDRETRIESLEN = 30;
        //private List<AssetBase>[] m_sendRetries;
        //private List<string>[] m_sendCachedRetries;
        //private System.Timers.Timer m_retryTimer;

        //private int m_retryCounter;
        //private bool m_inRetries;

        private Dictionary<string, List<SimpleAssetRetrieved>> m_AssetHandlers = new Dictionary<string, List<SimpleAssetRetrieved>>();

        private ObjectJobEngine m_localRequestsQueue;
        private ObjectJobEngine m_remoteRequestsQueue;

        // Foreign import → local DB: batch StoreLocal to cut MySQL write storms (compatible path).
        // Assets are always cached immediately so Get/AssetsExist remain correct before flush.
        private bool m_localStoreBatchEnabled = true;
        private int m_localStoreBatchSize = 16;
        private int m_localStoreFlushMs = 250;
        private readonly ConcurrentDictionary<string, AssetBase> m_pendingLocalStores = new();
        private int m_pendingLocalStoreCount;
        private readonly object m_localStoreFlushLock = new();
        private System.Threading.Timer m_localStoreFlushTimer;
        private long m_localStoreFlushedCount;
        private long m_localStoreBatchCount;

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public string Name
        {
            get { return "RegionAssetConnector"; }
        }

        public RegionAssetConnector() {}

        public RegionAssetConnector(IConfigSource config)
        {
            Initialise(config);
        }

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("AssetServices", "");
                if (name == Name)
                {
                    IConfig assetConfig = source.Configs["AssetService"];
                    if (assetConfig == null)
                    {
                        m_log.Error("[REGIONASSETCONNECTOR]: AssetService missing from configuration files");
                        throw new Exception("Region asset connector init error");
                    }

                    string localGridConnector = assetConfig.GetString("LocalGridAssetService", string.Empty);
                    if(string.IsNullOrEmpty(localGridConnector))
                    {
                        m_log.Error("[REGIONASSETCONNECTOR]: LocalGridAssetService missing from configuration files");
                        throw new Exception("Region asset connector init error");
                    }

                    object[] args = new object[] { source };

                    m_localConnector = ServerUtils.LoadPlugin<IAssetService>(localGridConnector, args);
                    if (m_localConnector == null)
                    {
                        m_log.Error("[REGIONASSETCONNECTOR]: Fail to load local asset service " + localGridConnector);
                        throw new Exception("Region asset connector init error");
                    }

                    string HGConnector = assetConfig.GetString("HypergridAssetService", string.Empty);
                    if(!string.IsNullOrEmpty(HGConnector))
                    {
                        m_HGConnector = ServerUtils.LoadPlugin<IAssetService>(HGConnector, args);
                        if (m_HGConnector == null)
                        {
                            m_log.Error("[REGIONASSETCONNECTOR]: Fail to load HG asset service " + HGConnector);
                            throw new Exception("Region asset connector init error");
                        }
                        IConfig hgConfig = source.Configs["HGAssetService"];
                        if (hgConfig != null)
                            m_AssetPerms = new AssetPermissions(hgConfig);
                    }

                    // Async CAP / callback path worker pools. Gather uses sync Get with its own limiter.
                    // Defaults raised so multi-visitor HG CAP fetches are not stuck at 2 remote workers.
                    int localWorkers = assetConfig.GetInt("LocalAssetRequestWorkers", 4);
                    if (localWorkers < 1)
                        localWorkers = 1;
                    if (localWorkers > 16)
                        localWorkers = 16;

                    int remoteWorkers = assetConfig.GetInt("RemoteAssetRequestWorkers", 8);
                    if (remoteWorkers < 1)
                        remoteWorkers = 1;
                    if (remoteWorkers > 32)
                        remoteWorkers = 32;

                    m_localRequestsQueue = new ObjectJobEngine(AssetRequestProcessor, "GetAssetsWorkers", 2000, localWorkers);
                    m_remoteRequestsQueue = new ObjectJobEngine(AssetRequestProcessor, "GetRemoteAssetsWorkers", 2000, remoteWorkers);

                    m_localStoreBatchEnabled = assetConfig.GetBoolean("LocalStoreBatchEnabled", m_localStoreBatchEnabled);
                    m_localStoreBatchSize = assetConfig.GetInt("LocalStoreBatchSize", m_localStoreBatchSize);
                    if (m_localStoreBatchSize < 1)
                        m_localStoreBatchSize = 1;
                    if (m_localStoreBatchSize > 128)
                        m_localStoreBatchSize = 128;
                    m_localStoreFlushMs = assetConfig.GetInt("LocalStoreFlushMs", m_localStoreFlushMs);
                    if (m_localStoreFlushMs < 50)
                        m_localStoreFlushMs = 50;
                    if (m_localStoreFlushMs > 5000)
                        m_localStoreFlushMs = 5000;

                    if (m_localStoreBatchEnabled)
                    {
                        m_localStoreFlushTimer = new System.Threading.Timer(
                            _ =>
                            {
                                try { FlushPendingLocalStores(forceAll: true); }
                                catch (Exception e)
                                {
                                    m_log.DebugFormat("[REGIONASSETCONNECTOR]: Local store flush timer error: {0}", e.Message);
                                }
                            },
                            null,
                            m_localStoreFlushMs,
                            m_localStoreFlushMs);
                    }

                    m_Enabled = true;
                    m_log.InfoFormat(
                        "[REGIONASSETCONNECTOR]: enabled (LocalAssetRequestWorkers={0}, RemoteAssetRequestWorkers={1}, LocalStoreBatch={2} size={3} flushMs={4})",
                        localWorkers, remoteWorkers, m_localStoreBatchEnabled, m_localStoreBatchSize, m_localStoreFlushMs);
                }
            }
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
            if (!m_Enabled)
                return;

            try
            {
                m_localStoreFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                m_localStoreFlushTimer?.Dispose();
                m_localStoreFlushTimer = null;
            }
            catch { /* ignore */ }

            try
            {
                FlushPendingLocalStores(forceAll: true);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[REGIONASSETCONNECTOR]: Final local store flush failed: {0}", e.Message);
            }

            m_localRequestsQueue.Dispose();
            m_localRequestsQueue = null;
            m_remoteRequestsQueue.Dispose();
            m_remoteRequestsQueue = null;
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_aScene = scene;
            m_aScene.RegisterModuleInterface<IAssetService>(this);
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            if (m_Cache == null)
            {
                m_Cache = scene.RequestModuleInterface<IAssetCache>();

                if (!(m_Cache is ISharedRegionModule))
                    m_Cache = null;
            }

            if(m_HGConnector == null)
            {
                if (m_Cache != null)
                    m_log.InfoFormat("[REGIONASSETCONNECTOR]: active with cache for region {0}", scene.RegionInfo.RegionName);
                else
                    m_log.InfoFormat("[REGIONASSETCONNECTOR]: active  without cache for region {0}", scene.RegionInfo.RegionName);
            }
            else
            {
                if (m_Cache != null)
                    m_log.InfoFormat("[REGIONASSETCONNECTOR]: active with HG and cache for region {0}", scene.RegionInfo.RegionName);
                else
                    m_log.InfoFormat("[REGIONASSETCONNECTOR]: active with HG and without cache for region {0}", scene.RegionInfo.RegionName);
            }
        }


        private bool IsHG(string id)
        {
            return id.Length > 0 && (id[0] == 'h' || id[0] == 'H');
        }

        public AssetBase GetCached(string id)
        {
            AssetBase asset = null;
            if (m_Cache != null)
                m_Cache.Get(id, out asset);
            if (asset is null && m_pendingLocalStores.TryGetValue(id, out asset))
                return asset;
            return asset;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private AssetBase GetFromLocal(string id)
        {
            return m_localConnector.Get(id);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private AssetBase GetFromForeign(string id, string ForeignAssetService)
        {
            if (m_HGConnector == null || string.IsNullOrEmpty(ForeignAssetService))
                return null;
            return m_HGConnector.Get(id , ForeignAssetService, true);
        }

        public AssetBase GetForeign(string id)
        {
            int type = Util.ParseForeignAssetID(id, out string uri, out string uuidstr);
            if (type < 0)
                return null;

            AssetBase asset = null;
            if (m_Cache != null)
            {
                 asset = m_Cache.GetCached(uuidstr);
                if(asset != null)
                    return asset;
            }

            asset = GetFromLocal(uuidstr);
            if (asset != null || type == 0)
                return asset;
            return GetFromForeign(uuidstr, uri);
        }

        public AssetBase Get(string id)
        {
            //m_log.DebugFormat("[HG ASSET CONNECTOR]: Get {0}", id);
            AssetBase asset = null;
            if (IsHG(id))
            {
                asset = GetForeign(id);
                if (asset != null)
                {
                    // Now store it locally, if allowed
                    if (m_AssetPerms != null && !m_AssetPerms.AllowedImport(asset.Type))
                        return null;
                    Store(asset);
                }
            }
            else
            {
                if (m_Cache != null)
                {
                    if(!m_Cache.Get(id, out asset))
                        return null;
                    if (asset != null)
                        return asset;
                }
                if (m_pendingLocalStores.TryGetValue(id, out asset) && asset != null)
                    return asset;
                asset = GetFromLocal(id);
                if(m_Cache != null)
                {
                    if(asset == null)
                        m_Cache.CacheNegative(id);
                    else
                        m_Cache.Cache(asset);
                }
            }
            return asset;
        }

        public AssetBase Get(string id, string ForeignAssetService, bool StoreOnLocalGrid)
        {
            // assumes id and ForeignAssetService are valid and resolved
            AssetBase asset = null;
            if (m_Cache != null)
            {
                asset = m_Cache.GetCached(id);
                if (asset != null)
                    return asset;
            }

            // Pending batched local store (not yet in DB) — still visible
            if (m_pendingLocalStores.TryGetValue(id, out asset) && asset != null)
                return asset;

            asset = GetFromLocal(id);
            if (asset == null)
            {
                asset = GetFromForeign(id, ForeignAssetService);
                if (asset != null)
                {
                    if (m_AssetPerms != null && !m_AssetPerms.AllowedImport(asset.Type))
                    {
                        if (m_Cache != null)
                            m_Cache.CacheNegative(id);
                        return null;
                    }
                    // Cache first so concurrent gatherers / CAP see the asset before DB flush
                    if (m_Cache != null)
                        m_Cache.Cache(asset);
                    if (StoreOnLocalGrid)
                        EnqueueOrStoreLocal(asset);
                }
                else if (m_Cache != null)
                    m_Cache.CacheNegative(id);
            }
            else if (m_Cache != null)
                m_Cache.Cache(asset);

            return asset;
        }

        /// <summary>
        /// Foreign-only fetch for callers that have already established the asset is absent
        /// locally (e.g. HGUuidGatherer after its batched AssetsExist prefilter). Same as Get(id, url, bool)
        /// but skips the guaranteed-miss GetFromLocal round trip. Cache and pending-store checks are
        /// kept so assets that became available in the meantime still hit.
        /// </summary>
        public AssetBase GetForeignOnly(string id, string ForeignAssetService, bool StoreOnLocalGrid)
        {
            AssetBase asset = null;
            if (m_Cache != null)
            {
                asset = m_Cache.GetCached(id);
                if (asset != null)
                    return asset;
            }

            // Pending batched local store (not yet in DB) — still visible
            if (m_pendingLocalStores.TryGetValue(id, out asset) && asset != null)
                return asset;

            asset = GetFromForeign(id, ForeignAssetService);
            if (asset != null)
            {
                if (m_AssetPerms != null && !m_AssetPerms.AllowedImport(asset.Type))
                {
                    if (m_Cache != null)
                        m_Cache.CacheNegative(id);
                    return null;
                }
                // Cache first so concurrent gatherers / CAP see the asset before DB flush
                if (m_Cache != null)
                    m_Cache.Cache(asset);
                if (StoreOnLocalGrid)
                    EnqueueOrStoreLocal(asset);
            }
            else if (m_Cache != null)
                m_Cache.CacheNegative(id);

            return asset;
        }

        public AssetMetadata GetMetadata(string id)
        {
            AssetBase asset = Get(id);
            if (asset != null)
                return asset.Metadata;
            return null;
        }

        public byte[] GetData(string id)
        {
            AssetBase asset = Get(id);
            if (asset != null)
                return asset.Data;
            return null;
        }

        public virtual bool Get(string id, object sender, AssetRetrieved callBack)
        {
            AssetBase asset = null;
            if (m_Cache != null)
            {
                if (!m_Cache.GetFromMemory(id, out asset))
                {
                    callBack(id, sender, null);
                    return false;
                }
            }

            if (asset == null)
            {
                if (id.Equals(Util.UUIDZeroString))
                {
                    callBack(id, sender, null);
                    return false;
                }

                lock (m_AssetHandlers)
                {
                    SimpleAssetRetrieved handlerEx = new SimpleAssetRetrieved(delegate (AssetBase _asset) { callBack(id, sender, _asset); _asset = null;});

                    List<SimpleAssetRetrieved> handlers;
                    if (m_AssetHandlers.TryGetValue(id, out handlers))
                    {
                        // Someone else is already loading this asset. It will notify our handler when done.
                        handlers.Add(handlerEx);
                        return true;
                    }

                    handlers = new List<SimpleAssetRetrieved>();
                    handlers.Add(handlerEx);

                    m_AssetHandlers.Add(id, handlers);
                    m_localRequestsQueue.Enqueue(id);
                }
            }
            else
            {
                if (asset != null && (asset.Data == null || asset.Data.Length == 0))
                    asset = null;
                callBack(id, sender, asset);
            }
            return true;
        }

        public struct ForeignAssetServiceGetData
        {
            public string id;
            public string ForeignAssetService;
            public bool StoreOnLocalGrid;
        }

        public void Get(string id, string ForeignAssetService, bool StoreOnLocalGrid, SimpleAssetRetrieved callBack)
        {
            AssetBase asset = null;
            if (m_Cache != null)
            {
                if (!m_Cache.GetFromMemory(id, out asset))
                {
                    callBack(null);
                    return;
                }
            }

            if (asset == null)
            {
                if (id.Equals(Util.UUIDZeroString))
                {
                    callBack(null);
                    return;
                }

                lock (m_AssetHandlers)
                {
                    SimpleAssetRetrieved handlerEx = new SimpleAssetRetrieved(delegate (AssetBase _asset) { callBack(_asset); _asset = null;});

                    List<SimpleAssetRetrieved> handlers;
                    if (m_AssetHandlers.TryGetValue(id, out handlers))
                    {
                        // Someone else is already loading this asset. It will notify our handler when done.
                        handlers.Add(handlerEx);
                        return;
                    }

                    handlers = new List<SimpleAssetRetrieved>();
                    handlers.Add(handlerEx);

                    m_AssetHandlers.Add(id, handlers);
                    if(string.IsNullOrEmpty(ForeignAssetService))
                        m_localRequestsQueue.Enqueue(id);
                    else
                    {
                        ForeignAssetServiceGetData fasgd = new ForeignAssetServiceGetData
                        {
                            id = id,
                            ForeignAssetService = ForeignAssetService,
                            StoreOnLocalGrid = StoreOnLocalGrid
                        };
                        m_remoteRequestsQueue.Enqueue(fasgd);
                    }
                }
            }
            else
            {
                if (asset != null && (asset.Data == null || asset.Data.Length == 0))
                    asset = null;
                callBack(asset);
            }
        }

        private void AssetRequestProcessor(object o)
        {
            if( o == null)
                return;

            try
            {
                AssetBase a;
                string id;
                if (o is ForeignAssetServiceGetData)
                {
                    var fasgd = (ForeignAssetServiceGetData)o;
                    id = fasgd.id;
                    a = Get(id, fasgd.ForeignAssetService, fasgd.StoreOnLocalGrid);
                }
                else
                {
                    id = (string)o;
                    a = Get(id);
                }

                List<SimpleAssetRetrieved> handlers;
                lock (m_AssetHandlers)
                {
                    handlers = m_AssetHandlers[id];
                    m_AssetHandlers.Remove(id);
                }

                if (handlers != null)
                {
                    Util.FireAndForget(x =>
                    {
                        foreach (SimpleAssetRetrieved h in handlers)
                        {
                            try
                            {
                                h.Invoke(a);
                            }
                            catch { }
                        }
                        handlers.Clear();
                        a = null;
                    });
                }
            }
            catch { }
        }

        public bool[] AssetsExist(string[] ids)
        {
            int numHG = 0;
            foreach (string id in ids)
            {
                if (IsHG(id))
                    ++numHG;
            }
            if (numHG == 0)
            {
                bool[] exist = m_localConnector.AssetsExist(ids);
                // Treat in-flight batched imports as present (cache + pending queue)
                if (exist != null && exist.Length == ids.Length && !m_pendingLocalStores.IsEmpty)
                {
                    for (int i = 0; i < ids.Length; i++)
                    {
                        if (!exist[i] && m_pendingLocalStores.ContainsKey(ids[i]))
                            exist[i] = true;
                    }
                }
                return exist;
            }
            else if (m_HGConnector != null)
                return m_HGConnector.AssetsExist(ids);
            return [];
        }

        public string Store(AssetBase asset)
        {
            string id;
            if (IsHG(asset.ID))
            {
                if (asset.Local || asset.Temporary)
                    return null;

                id = StoreForeign(asset);
                if (!string.IsNullOrEmpty(id) && !id.Equals(UUID.ZeroString))
                { 
                    m_Cache?.Cache(asset);
                    return id;
                }
                else
                    return string.Empty;
            }

            if (m_Cache != null)
            {
                 m_Cache.Cache(asset);
                if (asset.Local || asset.Temporary)
                    return asset.ID;
            }

            id = StoreLocal(asset);

            return string.IsNullOrEmpty(id) || id.Equals(UUID.ZeroString) ? string.Empty : id;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private string StoreForeign(AssetBase asset)
        {
            if (m_HGConnector == null)
                return string.Empty;
            if (m_AssetPerms != null && !m_AssetPerms.AllowedExport(asset.Type))
                return string.Empty;
            return m_HGConnector.Store(asset);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private string StoreLocal(AssetBase asset)
        {
            return m_localConnector.Store(asset);
        }

        /// <summary>
        /// Queue foreign-imported assets for batched local DB write, or store immediately if batching is off.
        /// Caller must cache the asset first. Durability is eventual (size threshold + flush timer + Close).
        /// </summary>
        private void EnqueueOrStoreLocal(AssetBase asset)
        {
            if (asset is null)
                return;

            if (!m_localStoreBatchEnabled)
            {
                StoreLocal(asset);
                return;
            }

            if (!m_pendingLocalStores.TryAdd(asset.ID, asset))
                return; // already queued for this id

            int pending = Interlocked.Increment(ref m_pendingLocalStoreCount);
            if (pending >= m_localStoreBatchSize)
                FlushPendingLocalStores(forceAll: false);
        }

        /// <summary>
        /// Flush pending foreign-import stores to local DB.
        /// When <paramref name="forceAll"/> is false, flushes up to one batch size; when true, drains the queue.
        /// </summary>
        private void FlushPendingLocalStores(bool forceAll)
        {
            if (m_pendingLocalStores.IsEmpty)
                return;

            // Single flusher at a time; gather threads only enqueue
            if (forceAll)
                Monitor.Enter(m_localStoreFlushLock);
            else if (!Monitor.TryEnter(m_localStoreFlushLock))
                return;

            try
            {
                do
                {
                    List<AssetBase> batch = new(forceAll ? 64 : m_localStoreBatchSize);
                    int limit = forceAll ? int.MaxValue : m_localStoreBatchSize;

                    foreach (KeyValuePair<string, AssetBase> kv in m_pendingLocalStores)
                    {
                        if (batch.Count >= limit)
                            break;
                        if (m_pendingLocalStores.TryRemove(kv.Key, out AssetBase asset) && asset != null)
                        {
                            batch.Add(asset);
                            Interlocked.Decrement(ref m_pendingLocalStoreCount);
                        }
                    }

                    if (batch.Count == 0)
                        break;

                    int start = Environment.TickCount;
                    int ok = 0;
                    foreach (AssetBase asset in batch)
                    {
                        try
                        {
                            string id = StoreLocal(asset);
                            if (!string.IsNullOrEmpty(id) && !id.Equals(UUID.ZeroString))
                                ok++;
                        }
                        catch (Exception e)
                        {
                            m_log.WarnFormat(
                                "[REGIONASSETCONNECTOR]: Batched StoreLocal failed for {0}: {1}",
                                asset.ID, e.Message);
                        }
                    }

                    Interlocked.Add(ref m_localStoreFlushedCount, ok);
                    Interlocked.Increment(ref m_localStoreBatchCount);

                    int ms = Environment.TickCount - start;
                    if (ms < 0)
                        ms = 0;

                    if (batch.Count >= m_localStoreBatchSize || ms >= 100 || forceAll)
                    {
                        m_log.DebugFormat(
                            "[REGIONASSETCONNECTOR]: Flushed {0}/{1} local asset store(s) in {2}ms (batches={3} totalFlushed={4} pending~{5})",
                            ok, batch.Count, ms, m_localStoreBatchCount, m_localStoreFlushedCount, m_pendingLocalStoreCount);
                    }

                    // forceAll: keep draining if more arrived while we stored
                } while (forceAll && !m_pendingLocalStores.IsEmpty);
            }
            finally
            {
                Monitor.Exit(m_localStoreFlushLock);
            }
        }

        public bool UpdateContent(string id, byte[] data)
        {
            if (IsHG(id))
                return false;
            return m_localConnector.UpdateContent(id, data);
        }

        public bool Delete(string id)
        {
            if (IsHG(id))
                return false;

            return m_localConnector.Delete(id);
        }
    }
}
