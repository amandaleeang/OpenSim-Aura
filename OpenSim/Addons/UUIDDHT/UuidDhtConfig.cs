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
using Nini.Config;
using OpenSim.Framework;

namespace OpenSim.Addons.UUIDDHT
{
    public sealed class UuidDhtConfig
    {
        public bool Enabled { get; init; }
        public string HomeURI { get; init; }
        public string IdentityPath { get; init; }
        public string StorePath { get; init; }
        public int Port { get; init; }
        public bool AllowPrivateHomeURI { get; init; }
        public int K { get; init; }
        public int SiblingSize { get; init; }
        public int RpcTimeoutMs { get; init; }
        public int MaxRpcBytes { get; init; }
        public int Alpha { get; init; }
        public int DisjointPaths { get; init; }
        public int LookupTimeoutMs { get; init; }
        public int ClientLookupCapMs { get; init; }
        public int PositiveCacheSec { get; init; }
        public int NegativeCacheSec { get; init; }
        public int CallbackTimeoutMs { get; init; }
        public int RepublishHours { get; init; }
        public int ExpireHours { get; init; }
        public int TombstoneHours { get; init; }
        public int BackfillIntervalSec { get; init; }
        public int MaxUuidsStoredPerOwner { get; init; }
        public int MaxPublishUuids { get; init; }
        public string[] Bootstrap { get; init; }
        /// <summary>
        /// Living seed list (default config-include/dht-seeds). Empty = do not persist.
        /// </summary>
        public string SeedsPath { get; init; }
        public int MaxBootstrapSeeds { get; init; }
        public int HomeHttpTimeoutSec { get; init; }

        /// <summary>WAN HTTP POST /dht/rpc. 300ms was LAN-only.</summary>
        public const int DefaultRpcTimeoutMs = 2000;
        /// <summary>One FIND (several RPC rounds).</summary>
        public const int DefaultLookupTimeoutMs = 8000;
        /// <summary>FIND uuid then FIND node.</summary>
        public const int DefaultClientLookupCapMs = 12000;
        /// <summary>GET /dht/node and GET /dht/owns.</summary>
        public const int DefaultCallbackTimeoutMs = 3000;
        public const int DefaultHomeHttpTimeoutSec = 5;
        public const int DefaultPositiveCacheSec = 600;
        public const int DefaultNegativeCacheSec = 60;
        public const int DefaultExpireHours = 36;
        public const int DefaultTombstoneHours = 168;
        public const int DefaultRepublishHours = 24;

        public static UuidDhtConfig From(IConfigSource config)
        {
            if (config == null)
            {
                return new UuidDhtConfig
                {
                    Enabled = false,
                    HomeURI = string.Empty,
                    IdentityPath = "uuiddht/identity-0.json",
                    StorePath = "uuiddht/store-0.db",
                    Port = 0,
                    AllowPrivateHomeURI = false,
                    K = 20,
                    SiblingSize = 20,
                    RpcTimeoutMs = DefaultRpcTimeoutMs,
                    MaxRpcBytes = 32768,
                    Alpha = 3,
                    DisjointPaths = 3,
                    LookupTimeoutMs = DefaultLookupTimeoutMs,
                    ClientLookupCapMs = DefaultClientLookupCapMs,
                    PositiveCacheSec = DefaultPositiveCacheSec,
                    NegativeCacheSec = DefaultNegativeCacheSec,
                    CallbackTimeoutMs = DefaultCallbackTimeoutMs,
                    RepublishHours = DefaultRepublishHours,
                    ExpireHours = DefaultExpireHours,
                    TombstoneHours = DefaultTombstoneHours,
                    BackfillIntervalSec = 60,
                    MaxUuidsStoredPerOwner = 100000,
                    MaxPublishUuids = 100000,
                    Bootstrap = System.Array.Empty<string>(),
                    SeedsPath = string.Empty,
                    MaxBootstrapSeeds = DhtSeedFile.DefaultMaxSeeds,
                    HomeHttpTimeoutSec = DefaultHomeHttpTimeoutSec
                };
            }

            IConfig dht = config.Configs["UuidDht"];
            bool enabled = IsEnabled(config);
            int port = ReadPort(config);

            string home = Util.GetConfigVarFromSections<string>(
                config, "HomeURI", new[] { "UuidDht", "Hypergrid", "Startup" }, string.Empty);

            string identity = dht != null
                ? dht.GetString("IdentityPath", "uuiddht/identity-{port}.json")
                : "uuiddht/identity-{port}.json";
            string store = dht != null
                ? dht.GetString("StorePath", "uuiddht/store-{port}.db")
                : "uuiddht/store-{port}.db";

            string portStr = port.ToString();
            bool allowPrivate = dht != null && dht.GetBoolean("AllowPrivateHomeURI", false);
            int maxSeeds = dht != null ? dht.GetInt("MaxBootstrapSeeds", DhtSeedFile.DefaultMaxSeeds) : DhtSeedFile.DefaultMaxSeeds;
            string seedsPath = dht != null ? dht.GetString("SeedsPath", DhtSeedFile.DefaultPath) : DhtSeedFile.DefaultPath;
            DhtSeedFile.EnsureFromExample(seedsPath);
            if (!string.IsNullOrWhiteSpace(home)
                && DhtLocator.TryNormalize(home, allowPrivate, out string normalizedHome))
                home = normalizedHome;

            string[] iniSeeds = ParseBootstrap(dht != null ? dht.GetString("Bootstrap", string.Empty) : string.Empty);
            string[] fileSeeds = DhtSeedFile.Read(seedsPath);
            string[] bootstrap = DhtSeedFile.Collect(home, iniSeeds, fileSeeds, allowPrivate, maxSeeds).ToArray();

            return new UuidDhtConfig
            {
                Enabled = enabled,
                HomeURI = home ?? string.Empty,
                IdentityPath = identity.Replace("{port}", portStr),
                StorePath = store.Replace("{port}", portStr),
                Port = port,
                AllowPrivateHomeURI = allowPrivate,
                K = dht != null ? dht.GetInt("k", 20) : 20,
                SiblingSize = dht != null ? dht.GetInt("SiblingSize", 20) : 20,
                RpcTimeoutMs = dht != null ? dht.GetInt("RpcTimeoutMs", DefaultRpcTimeoutMs) : DefaultRpcTimeoutMs,
                MaxRpcBytes = dht != null ? dht.GetInt("MaxRpcBytes", 32768) : 32768,
                Alpha = dht != null ? dht.GetInt("Alpha", 3) : 3,
                DisjointPaths = dht != null ? dht.GetInt("DisjointPaths", 3) : 3,
                LookupTimeoutMs = dht != null ? dht.GetInt("LookupTimeoutMs", DefaultLookupTimeoutMs) : DefaultLookupTimeoutMs,
                ClientLookupCapMs = dht != null ? dht.GetInt("ClientLookupCapMs", DefaultClientLookupCapMs) : DefaultClientLookupCapMs,
                PositiveCacheSec = dht != null ? dht.GetInt("PositiveCacheSec", DefaultPositiveCacheSec) : DefaultPositiveCacheSec,
                NegativeCacheSec = dht != null ? dht.GetInt("NegativeCacheSec", DefaultNegativeCacheSec) : DefaultNegativeCacheSec,
                CallbackTimeoutMs = dht != null ? dht.GetInt("CallbackTimeoutMs", DefaultCallbackTimeoutMs) : DefaultCallbackTimeoutMs,
                RepublishHours = dht != null ? dht.GetInt("RepublishHours", DefaultRepublishHours) : DefaultRepublishHours,
                ExpireHours = dht != null ? dht.GetInt("ExpireHours", DefaultExpireHours) : DefaultExpireHours,
                TombstoneHours = dht != null ? dht.GetInt("TombstoneHours", DefaultTombstoneHours) : DefaultTombstoneHours,
                BackfillIntervalSec = dht != null ? dht.GetInt("BackfillIntervalSec", 60) : 60,
                MaxUuidsStoredPerOwner = dht != null ? dht.GetInt("MaxUuidsStoredPerOwner", 100000) : 100000,
                MaxPublishUuids = dht != null ? dht.GetInt("MaxPublishUuids", 100000) : 100000,
                Bootstrap = bootstrap,
                SeedsPath = seedsPath,
                MaxBootstrapSeeds = maxSeeds,
                HomeHttpTimeoutSec = dht != null ? dht.GetInt("HomeHttpTimeoutSec", DefaultHomeHttpTimeoutSec) : DefaultHomeHttpTimeoutSec
            };
        }

        /// <summary>
        /// On if a DHT LocalServiceModule / UuidDhtServiceConnector is loaded,
        /// or [UuidDht] Enabled = true. Loading the modules is enough.
        /// </summary>
        public static bool IsEnabled(IConfigSource config)
        {
            if (config == null)
                return false;
            if (LocalModuleIsDht(config, "UserAccountService")
                || LocalModuleIsDht(config, "GridUserService")
                || LocalModuleIsDht(config, "Groups"))
                return true;
            IConfig list = config.Configs["ServiceList"];
            if (list != null)
            {
                string[] keys = list.GetKeys();
                for (int i = 0; i < keys.Length; i++)
                {
                    string v = list.GetString(keys[i], string.Empty);
                    if (v.IndexOf("UuidDhtServiceConnector", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            IConfig dht = config.Configs["UuidDht"];
            return dht != null && dht.GetBoolean("Enabled", false);
        }

        private static bool LocalModuleIsDht(IConfigSource config, string section)
        {
            IConfig c = config.Configs[section];
            if (c == null)
                return false;
            string m = c.GetString("LocalServiceModule", string.Empty);
            return m.IndexOf("UuidDht", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int ReadPort(IConfigSource config)
        {
            if (config == null)
                return 0;

            IConfig cst = config.Configs["Const"];
            if (cst != null)
            {
                int pub = cst.GetInt("PublicPort", 0);
                if (pub > 0)
                    return pub;
            }

            IConfig net = config.Configs["Network"];
            if (net != null)
                return net.GetInt("http_listener_port", 0);

            return 0;
        }

        public static string[] ParseBootstrap(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return System.Array.Empty<string>();
            string[] parts = raw.Split(',');
            System.Collections.Generic.List<string> list = new System.Collections.Generic.List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim().Trim('"');
                if (s.Length > 0)
                    list.Add(s);
            }
            return list.ToArray();
        }
    }
}
