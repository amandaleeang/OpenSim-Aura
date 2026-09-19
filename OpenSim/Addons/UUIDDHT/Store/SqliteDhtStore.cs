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
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using log4net;
using OpenMetaverse;

namespace OpenSim.Addons.UUIDDHT
{
    public sealed class SqliteDhtStore : IDhtStore
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly object s_nativeLock = new object();
        private static IntPtr s_nativeHandle;
        private static bool s_nativeTried;

        private readonly object m_dbLock = new object();
        private readonly SQLiteConnection m_conn;
        private readonly bool m_allowPrivate;
        private readonly int m_maxUuids;
        private readonly int m_expireHours;
        private readonly int m_tombstoneHours;
        private bool m_disposed;

        /// <summary>
        /// Test clock. 0 means DateTimeOffset.UtcNow. Used to prove 36h expire
        /// without sleeping.
        /// </summary>
        internal long NowUnixOverride;

        static SqliteDhtStore()
        {
            EnsureSqliteNative();
        }

        public SqliteDhtStore(string path, bool allowPrivate = false, int maxUuids = 100000,
            int expireHours = 36, int tombstoneHours = 168)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("store path is required", nameof(path));

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            m_allowPrivate = allowPrivate;
            m_maxUuids = maxUuids > 0 ? maxUuids : 100000;
            m_expireHours = expireHours > 0 ? expireHours : 36;
            m_tombstoneHours = tombstoneHours > 0 ? tombstoneHours : 168;

            if (!EnsureSqliteNative())
            {
                throw new DllNotFoundException(
                    "UUID-DHT overlay store is always SQLite (uuiddht/store-*.db) even when UserAccount/Groups use MySQL. "
                    + "Could not load e_sqlite3 from " + AppContext.BaseDirectory
                    + " or lib64/libe_sqlite3.so next to OpenSim.");
            }

            m_conn = new SQLiteConnection("Data Source=" + path + ";Version=3;Pooling=False;");
            m_conn.Open();
            Exec("PRAGMA busy_timeout=1000");
            Exec("PRAGMA journal_mode=WAL");
            Exec("PRAGMA synchronous=NORMAL");
            Exec(@"CREATE TABLE IF NOT EXISTS records (
  dht_key     TEXT PRIMARY KEY,
  rec_key     TEXT NOT NULL,
  kind        TEXT NOT NULL,
  node_id     TEXT NOT NULL,
  seq         INTEGER NOT NULL,
  tombstone   INTEGER NOT NULL,
  json        TEXT NOT NULL,
  stored_unix INTEGER NOT NULL,
  expire_unix INTEGER NOT NULL
)");
            Exec("CREATE INDEX IF NOT EXISTS idx_kind_owner ON records(kind, node_id)");
            Exec("CREATE INDEX IF NOT EXISTS idx_expire ON records(expire_unix)");
            Exec("CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL)");
        }

        public bool TryGetSelfNodeId(out DhtKey nodeId)
        {
            nodeId = default;
            lock (m_dbLock)
            {
                using SQLiteCommand cmd = m_conn.CreateCommand();
                cmd.CommandText = "SELECT v FROM meta WHERE k='self_node_id'";
                object v = cmd.ExecuteScalar();
                if (v == null || v == DBNull.Value)
                    return false;
                return DhtKey.TryParseHex(Convert.ToString(v), out nodeId);
            }
        }

        public void SetSelfNodeId(DhtKey nodeId)
        {
            lock (m_dbLock)
            {
                using SQLiteCommand cmd = m_conn.CreateCommand();
                cmd.CommandText = "INSERT INTO meta(k,v) VALUES('self_node_id',@v) ON CONFLICT(k) DO UPDATE SET v=@v";
                cmd.Parameters.AddWithValue("@v", nodeId.ToHex());
                cmd.ExecuteNonQuery();
            }
        }

        public bool TryGet(DhtKey dhtKey, out DhtRecord record)
        {
            record = null;
            lock (m_dbLock)
            {
                long now = Now();
                using SQLiteCommand cmd = m_conn.CreateCommand();
                cmd.CommandText = "SELECT json FROM records WHERE dht_key=@k AND expire_unix > @now";
                cmd.Parameters.AddWithValue("@k", dhtKey.ToHex());
                cmd.Parameters.AddWithValue("@now", now);
                object v = cmd.ExecuteScalar();
                if (v == null || v == DBNull.Value)
                    return false;
                record = JsonSerializer.Deserialize<DhtRecord>((string)v, DhtJson.WireOptions);
                return record != null;
            }
        }

        public int CountLiveUuidByOwner(DhtKey nodeId)
        {
            lock (m_dbLock)
                return CountLiveUuidUnlocked(nodeId.ToHex());
        }

        public IEnumerable<DhtRecord> AllLiveOwnedBy(DhtKey nodeId)
        {
            List<DhtRecord> list = new List<DhtRecord>();
            lock (m_dbLock)
            {
                using SQLiteCommand cmd = m_conn.CreateCommand();
                cmd.CommandText = "SELECT json FROM records WHERE node_id=@n AND tombstone=0 AND expire_unix > @now";
                cmd.Parameters.AddWithValue("@n", nodeId.ToHex());
                cmd.Parameters.AddWithValue("@now", Now());
                using SQLiteDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    DhtRecord rec = JsonSerializer.Deserialize<DhtRecord>(r.GetString(0), DhtJson.WireOptions);
                    if (rec != null)
                        list.Add(rec);
                }
            }
            return list;
        }

        public void Expire()
        {
            lock (m_dbLock)
            {
                using SQLiteCommand cmd = m_conn.CreateCommand();
                cmd.CommandText = "DELETE FROM records WHERE expire_unix <= @now";
                cmd.Parameters.AddWithValue("@now", Now());
                cmd.ExecuteNonQuery();
            }
        }

        public DhtStorePutResult Put(DhtRecord record, DhtRecord attachedNode, IDhtStoreValidator validator)
        {
            if (record == null)
                return DhtStorePutResult.Fail("malformed");
            if (record.KindEnum == DhtRecordKind.Uuid && !record.Tombstone && attachedNode == null)
                return DhtStorePutResult.Fail("malformed");
            if (record.Seq < 1)
                return DhtStorePutResult.Fail("stale_seq");
            if (!record.TryParseDhtKey(out DhtKey dhtKey) || !record.TryParseNodeId(out DhtKey owner) || !record.MatchesComputedDhtKey())
                return DhtStorePutResult.Fail("malformed");

            byte[] ownerPub = OwnerPubkey(record, attachedNode);
            bool uuidTombstone = record.KindEnum == DhtRecordKind.Uuid && record.Tombstone;
            if (!uuidTombstone && (ownerPub == null || !record.Verify(ownerPub)))
                return DhtStorePutResult.Fail("bad_sig");

            PutPlan plan;
            lock (m_dbLock)
            {
                DhtStorePutResult planned = PlanPutUnlocked(record, attachedNode, ownerPub, dhtKey, owner, out plan);
                if (!planned.Ok)
                    return planned;
            }

            if (plan.NeedProof)
            {
                if (validator == null || !validator.ProofMatches(plan.Home, owner))
                    return DhtStorePutResult.Fail("owns_failed");
            }
            if (plan.NeedOwns)
            {
                if (validator == null || !validator.OwnsUuid(plan.OwnsHome, plan.OwnsUuid, owner))
                    return DhtStorePutResult.Fail("owns_failed");
            }
            if (plan.NeedOccupantProof)
            {
                if (validator != null && validator.ProofMatches(plan.OccupantHome, plan.OccupantId))
                    return DhtStorePutResult.Fail("home_taken");
            }

            lock (m_dbLock)
                return CommitPutUnlocked(record, dhtKey, owner, plan);
        }

        public void Dispose()
        {
            if (m_disposed)
                return;
            m_disposed = true;
            lock (m_dbLock)
            {
                m_conn.Close();
                m_conn.Dispose();
            }
        }

        private DhtStorePutResult PlanPutUnlocked(DhtRecord record, DhtRecord attachedNode, byte[] ownerPub,
            DhtKey dhtKey, DhtKey owner, out PutPlan plan)
        {
            plan = new PutPlan();
            DeleteIfExpiredUnlocked(dhtKey.ToHex());
            TryGetRowUnlocked(dhtKey.ToHex(), out StoredRow existing);
            switch (record.KindEnum)
            {
                case DhtRecordKind.Uuid:
                    return PlanUuidUnlocked(record, attachedNode, ownerPub, owner, existing, plan);
                case DhtRecordKind.Node:
                    return PlanNodeUnlocked(record, owner, existing, plan);
                default:
                    return PlanLocUnlocked(record, owner, existing, plan);
            }
        }

        private DhtStorePutResult PlanUuidUnlocked(DhtRecord record, DhtRecord attachedNode, byte[] ownerPub,
            DhtKey owner, StoredRow existing, PutPlan plan)
        {
            DhtStorePutResult seq = CheckGlobalSeq(existing, owner, record.Seq);
            if (!seq.Ok)
                return seq;

            if (ownerPub == null)
                ownerPub = PubkeyFromStoredNodeUnlocked(owner);
            if (ownerPub == null || !record.Verify(ownerPub))
                return DhtStorePutResult.Fail("bad_sig");

            if (!record.Tombstone)
            {
                if (attachedNode == null)
                    return DhtStorePutResult.Fail("malformed");
                if (attachedNode.KindEnum != DhtRecordKind.Node || attachedNode.NodeId != record.NodeId)
                    return DhtStorePutResult.Fail("malformed");
                if (!attachedNode.Verify(ownerPub))
                    return DhtStorePutResult.Fail("bad_sig");
                if (!DhtLocator.TryNormalize(attachedNode.HomeURI, m_allowPrivate, out string home))
                    return DhtStorePutResult.Fail("bad_locator");

                string ownsHome = home;
                if (TryGetRowUnlocked(owner.ToHex(), out StoredRow storedNode)
                    && storedNode.Kind == "node" && !storedNode.Tombstone
                    && storedNode.Seq >= attachedNode.Seq)
                {
                    DhtRecord stored = JsonSerializer.Deserialize<DhtRecord>(storedNode.Json, DhtJson.WireOptions);
                    if (stored != null && DhtLocator.TryNormalize(stored.HomeURI, m_allowPrivate, out string storedHome))
                        ownsHome = storedHome;
                }

                if (!UUID.TryParse(record.Uuid, out UUID uuid))
                    return DhtStorePutResult.Fail("malformed");
                plan.NeedOwns = true;
                plan.OwnsHome = ownsHome;
                plan.OwnsUuid = uuid;
            }
            return DhtStorePutResult.OkResult();
        }

        private DhtStorePutResult PlanNodeUnlocked(DhtRecord record, DhtKey owner, StoredRow existing, PutPlan plan)
        {
            DhtStorePutResult seq = CheckGlobalSeq(existing, owner, record.Seq);
            if (!seq.Ok)
                return seq;

            byte[] pub;
            try
            {
                pub = Convert.FromHexString(record.Pubkey ?? string.Empty);
            }
            catch (FormatException)
            {
                return DhtStorePutResult.Fail("malformed");
            }
            if (DhtSigner.NodeIdFromPubkey(pub).ToHex() != record.NodeId)
                return DhtStorePutResult.Fail("malformed");
            if (!DhtLocator.TryNormalize(record.HomeURI, m_allowPrivate, out string home))
                return DhtStorePutResult.Fail("bad_locator");
            record.HomeURI = home;
            plan.NeedProof = true;
            plan.Home = home;

            DhtKey locKey = DhtKey.ForLoc(home);
            if (TryGetRowUnlocked(locKey.ToHex(), out StoredRow loc) && !loc.Tombstone && loc.NodeId != owner.ToHex())
            {
                DhtRecord occupant = JsonSerializer.Deserialize<DhtRecord>(loc.Json, DhtJson.WireOptions);
                if (!DhtKey.TryParseHex(loc.NodeId, out DhtKey occId))
                    return DhtStorePutResult.Fail("malformed");
                plan.NeedOccupantProof = true;
                plan.OccupantHome = occupant?.HomeURI ?? home;
                plan.OccupantId = occId;
                plan.OccupantIdHex = loc.NodeId;
            }
            return DhtStorePutResult.OkResult();
        }

        private DhtStorePutResult PlanLocUnlocked(DhtRecord record, DhtKey owner, StoredRow existing, PutPlan plan)
        {
            if (!DhtLocator.TryNormalize(record.HomeURI, m_allowPrivate, out string home))
                return DhtStorePutResult.Fail("bad_locator");
            record.HomeURI = home;
            if (!record.MatchesComputedDhtKey())
                return DhtStorePutResult.Fail("malformed");

            if (existing != null && !existing.Tombstone && existing.NodeId != record.NodeId)
            {
                DhtRecord occupant = JsonSerializer.Deserialize<DhtRecord>(existing.Json, DhtJson.WireOptions);
                if (!DhtKey.TryParseHex(existing.NodeId, out DhtKey occId))
                    return DhtStorePutResult.Fail("malformed");
                plan.NeedOccupantProof = true;
                plan.OccupantHome = occupant?.HomeURI ?? home;
                plan.OccupantId = occId;
                plan.OccupantIdHex = existing.NodeId;
                return DhtStorePutResult.OkResult();
            }

            return CheckGlobalSeq(existing, owner, record.Seq);
        }

        private DhtStorePutResult CommitPutUnlocked(DhtRecord record, DhtKey dhtKey, DhtKey owner, PutPlan plan)
        {
            DeleteIfExpiredUnlocked(dhtKey.ToHex());
            TryGetRowUnlocked(dhtKey.ToHex(), out StoredRow existing);

            switch (record.KindEnum)
            {
                case DhtRecordKind.Uuid:
                    {
                        DhtStorePutResult seq = CheckGlobalSeq(existing, owner, record.Seq);
                        if (!seq.Ok)
                            return seq;
                        if (!record.Tombstone
                            && CountLiveUuidUnlocked(owner.ToHex()) >= m_maxUuids
                            && (existing == null || existing.Tombstone))
                            return DhtStorePutResult.Fail("cap");
                        UpsertUnlocked(record, dhtKey, owner);
                        return DhtStorePutResult.OkResult();
                    }
                case DhtRecordKind.Node:
                    {
                        DhtStorePutResult seq = CheckGlobalSeq(existing, owner, record.Seq);
                        if (!seq.Ok)
                            return seq;
                        DhtKey locKey = DhtKey.ForLoc(record.HomeURI);
                        if (TryGetRowUnlocked(locKey.ToHex(), out StoredRow loc) && !loc.Tombstone
                            && loc.NodeId != owner.ToHex())
                        {
                            if (!plan.NeedOccupantProof || loc.NodeId != plan.OccupantIdHex)
                                return DhtStorePutResult.Fail("home_taken");
                        }
                        UpsertUnlocked(record, dhtKey, owner);
                        return DhtStorePutResult.OkResult();
                    }
                default:
                    if (existing != null && !existing.Tombstone && existing.NodeId != record.NodeId)
                    {
                        if (!plan.NeedOccupantProof || existing.NodeId != plan.OccupantIdHex)
                            return DhtStorePutResult.Fail("home_taken");
                        UpsertUnlocked(record, dhtKey, owner);
                        return DhtStorePutResult.OkResult();
                    }
                    DhtStorePutResult locSeq = CheckGlobalSeq(existing, owner, record.Seq);
                    if (!locSeq.Ok)
                        return locSeq;
                    UpsertUnlocked(record, dhtKey, owner);
                    return DhtStorePutResult.OkResult();
            }
        }

        private static DhtStorePutResult CheckGlobalSeq(StoredRow existing, DhtKey owner, long seq)
        {
            if (existing == null)
                return DhtStorePutResult.OkResult();
            if (existing.NodeId != owner.ToHex())
                return DhtStorePutResult.Fail("taken");
            if (seq <= existing.Seq)
                return DhtStorePutResult.Fail("stale_seq");
            return DhtStorePutResult.OkResult();
        }

        private byte[] PubkeyFromStoredNodeUnlocked(DhtKey owner)
        {
            if (!TryGetRowUnlocked(owner.ToHex(), out StoredRow row) || row.Kind != "node")
                return null;
            DhtRecord stored = JsonSerializer.Deserialize<DhtRecord>(row.Json, DhtJson.WireOptions);
            if (stored == null || string.IsNullOrEmpty(stored.Pubkey))
                return null;
            try
            {
                return Convert.FromHexString(stored.Pubkey);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static byte[] OwnerPubkey(DhtRecord record, DhtRecord attachedNode)
        {
            string hex = record.KindEnum == DhtRecordKind.Node ? record.Pubkey : attachedNode?.Pubkey;
            if (string.IsNullOrEmpty(hex))
                return null;
            try
            {
                return Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private void UpsertUnlocked(DhtRecord record, DhtKey dhtKey, DhtKey owner)
        {
            long now = Now();
            long ttlHours = record.Tombstone ? m_tombstoneHours : m_expireHours;
            string json = JsonSerializer.Serialize(record, DhtJson.WireOptions);
            using SQLiteCommand cmd = m_conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO records(dht_key, rec_key, kind, node_id, seq, tombstone, json, stored_unix, expire_unix)
VALUES(@k,@rk,@kind,@nid,@seq,@tomb,@json,@now,@exp)
ON CONFLICT(dht_key) DO UPDATE SET rec_key=@rk, kind=@kind, node_id=@nid, seq=@seq, tombstone=@tomb, json=@json, stored_unix=@now, expire_unix=@exp";
            cmd.Parameters.AddWithValue("@k", dhtKey.ToHex());
            cmd.Parameters.AddWithValue("@rk", record.Key);
            cmd.Parameters.AddWithValue("@kind", record.Kind);
            cmd.Parameters.AddWithValue("@nid", owner.ToHex());
            cmd.Parameters.AddWithValue("@seq", record.Seq);
            cmd.Parameters.AddWithValue("@tomb", record.Tombstone ? 1 : 0);
            cmd.Parameters.AddWithValue("@json", json);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@exp", now + ttlHours * 3600L);
            cmd.ExecuteNonQuery();
        }

        private bool TryGetRowUnlocked(string dhtKeyHex, out StoredRow row)
        {
            row = null;
            using SQLiteCommand cmd = m_conn.CreateCommand();
            cmd.CommandText = "SELECT rec_key, kind, node_id, seq, tombstone, json, expire_unix FROM records WHERE dht_key=@k";
            cmd.Parameters.AddWithValue("@k", dhtKeyHex);
            using SQLiteDataReader r = cmd.ExecuteReader();
            if (!r.Read())
                return false;
            row = new StoredRow
            {
                Kind = r.GetString(1),
                NodeId = r.GetString(2),
                Seq = r.GetInt64(3),
                Tombstone = r.GetInt32(4) != 0,
                Json = r.GetString(5),
                ExpireUnix = r.GetInt64(6)
            };
            return true;
        }

        private void DeleteIfExpiredUnlocked(string dhtKeyHex)
        {
            using SQLiteCommand cmd = m_conn.CreateCommand();
            cmd.CommandText = "DELETE FROM records WHERE dht_key=@k AND expire_unix <= @now";
            cmd.Parameters.AddWithValue("@k", dhtKeyHex);
            cmd.Parameters.AddWithValue("@now", Now());
            cmd.ExecuteNonQuery();
        }

        private int CountLiveUuidUnlocked(string nodeIdHex)
        {
            using SQLiteCommand cmd = m_conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM records WHERE kind='uuid' AND tombstone=0 AND node_id=@n AND expire_unix > @now";
            cmd.Parameters.AddWithValue("@n", nodeIdHex);
            cmd.Parameters.AddWithValue("@now", Now());
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private void Exec(string sql)
        {
            using SQLiteCommand cmd = m_conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private long Now()
        {
            if (NowUnixOverride > 0)
                return NowUnixOverride;
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        /// <summary>
        /// DHT replica records are always SQLite. Grid MySQL/PG does not load
        /// OpenSim.Data.SQLite, so System.Data.SQLite never sees lib64/e_sqlite3.
        /// </summary>
        internal static bool EnsureSqliteNative()
        {
            lock (s_nativeLock)
            {
                if (s_nativeTried)
                    return s_nativeHandle != IntPtr.Zero;
                s_nativeTried = true;

                foreach (string path in NativeCandidates(AppContext.BaseDirectory))
                {
                    if (!File.Exists(path))
                        continue;
                    if (!NativeLibrary.TryLoad(path, out s_nativeHandle))
                        continue;
                    m_log.Info("[UUID-DHT]: loaded SQLite native " + path);
                    break;
                }

                try
                {
                    NativeLibrary.SetDllImportResolver(typeof(SQLiteConnection).Assembly, ResolveSqliteNative);
                }
                catch (InvalidOperationException)
                {
                    // First P/Invoke already happened (OpenSim.Data.SQLite on a SQLite grid).
                }

                return s_nativeHandle != IntPtr.Zero;
            }
        }

        internal static IEnumerable<string> NativeCandidates(string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir))
                yield break;

            string lib64 = Path.Combine(baseDir, "lib64");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                yield return Path.Combine(baseDir, "e_sqlite3.dll");
                yield return Path.Combine(lib64, "e_sqlite3.dll");
                yield return Path.Combine(lib64, "sqlite3.dll");
                yield break;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
                yield return Path.Combine(baseDir, "libe_sqlite3.dylib");
                yield return Path.Combine(lib64, arm ? "libe_sqlite3_OSX_arm64.dylib" : "libe_sqlite3_OSX_x64.dylib");
                yield break;
            }

            yield return Path.Combine(baseDir, "libe_sqlite3.so");
            yield return Path.Combine(baseDir, "e_sqlite3.so");
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                yield return Path.Combine(lib64, "libe_sqlite3-arm64.so");
            yield return Path.Combine(lib64, "libe_sqlite3.so");
        }

        private static IntPtr ResolveSqliteNative(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != "e_sqlite3" && libraryName != "libe_sqlite3")
                return IntPtr.Zero;
            return s_nativeHandle;
        }

        private sealed class StoredRow
        {
            public string Kind;
            public string NodeId;
            public long Seq;
            public bool Tombstone;
            public string Json;
            public long ExpireUnix;
        }

        private sealed class PutPlan
        {
            public bool NeedProof;
            public bool NeedOwns;
            public bool NeedOccupantProof;
            public string Home;
            public string OwnsHome;
            public UUID OwnsUuid;
            public string OccupantHome;
            public DhtKey OccupantId;
            public string OccupantIdHex;
        }
    }
}
