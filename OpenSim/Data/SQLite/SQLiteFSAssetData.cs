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
using System.Reflection;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using System.Data.SQLite;

namespace OpenSim.Data.SQLite
{
    public class SQLiteFSAssetData : IFSAssetDataPlugin
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const string UnixNow = "CAST(strftime('%s','now') AS INTEGER)";

        private SQLiteConnection m_conn;
        private readonly object m_lock = new object();
        private string m_Table = "fsassets";
        private int DaysBetweenAccessTimeUpdates;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public string Version { get { return "1.0.0.0"; } }

        public string Name
        {
            get { return "SQLite FSAsset storage engine"; }
        }

        public void Initialise()
        {
            throw new NotImplementedException();
        }

        public void Initialise(string connect, string realm, int UpdateAccessTime)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            DaysBetweenAccessTimeUpdates = UpdateAccessTime;
            if (!string.IsNullOrEmpty(realm))
                m_Table = realm;

            m_conn = new SQLiteConnection(connect);
            m_conn.Open();
            SQLiteConnectionHelper.Configure(m_conn, connect);

            Migration m = new Migration(m_conn, Assembly, "FSAssetStore");
            m.Update();
        }

        public void Dispose()
        {
            lock (m_lock)
            {
                if (m_conn != null)
                {
                    SQLiteConnectionHelper.CheckpointTruncate(m_conn);
                    m_conn.Close();
                    m_conn.Dispose();
                    m_conn = null;
                }
            }
        }

        public AssetMetadata Get(string id, out string hash)
        {
            hash = string.Empty;
            AssetMetadata meta;
            int accessTime;

            lock (m_lock)
            {
                if (m_conn == null)
                    return null;

                using (SQLiteCommand cmd = new SQLiteCommand(m_conn))
                {
                    cmd.CommandText = string.Format(
                        "select id, name, description, type, hash, create_time, asset_flags, access_time from {0} where id = :id",
                        m_Table);
                    cmd.Parameters.Add(new SQLiteParameter(":id", id));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return null;

                        hash = ReaderString(reader, "hash");

                        meta = new AssetMetadata();
                        meta.ID = id;
                        meta.FullID = new UUID(id);
                        meta.Name = ReaderString(reader, "name");
                        meta.Description = ReaderString(reader, "description");
                        meta.Type = (sbyte)Convert.ToInt32(reader["type"]);
                        meta.ContentType = SLUtil.SLAssetTypeToContentType(meta.Type);
                        meta.CreationDate = Util.ToDateTime(Convert.ToInt32(reader["create_time"]));
                        meta.Flags = (AssetFlags)Convert.ToInt32(reader["asset_flags"]);
                        accessTime = Convert.ToInt32(reader["access_time"]);
                    }
                }
            }

            UpdateAccessTime(id, accessTime);
            return meta;
        }

        private void UpdateAccessTime(string assetId, int accessTime)
        {
            if (DaysBetweenAccessTimeUpdates > 0 &&
                (DateTime.UtcNow - Utils.UnixTimeToDateTime(accessTime)).TotalDays < DaysBetweenAccessTimeUpdates)
                return;

            lock (m_lock)
            {
                if (m_conn == null)
                    return;

                using (SQLiteCommand cmd = new SQLiteCommand(m_conn))
                {
                    cmd.CommandText = string.Format(
                        "UPDATE {0} SET access_time = {1} WHERE id = :id",
                        m_Table, UnixNow);
                    cmd.Parameters.Add(new SQLiteParameter(":id", assetId));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public bool Store(AssetMetadata meta, string hash)
        {
            try
            {
                string oldhash;
                AssetMetadata existingAsset = Get(meta.ID, out oldhash);

                if (existingAsset != null)
                    return true;

                lock (m_lock)
                {
                    if (m_conn == null)
                        return false;

                    using (SQLiteCommand cmd = new SQLiteCommand(m_conn))
                    {
                        cmd.CommandText = string.Format(
                            "insert into {0} (id, name, description, type, hash, asset_flags, create_time, access_time) values (:id, :name, :description, :type, :hash, :asset_flags, {1}, {1})",
                            m_Table, UnixNow);
                        cmd.Parameters.Add(new SQLiteParameter(":id", meta.ID));
                        cmd.Parameters.Add(new SQLiteParameter(":name", meta.Name ?? string.Empty));
                        cmd.Parameters.Add(new SQLiteParameter(":description", meta.Description ?? string.Empty));
                        cmd.Parameters.Add(new SQLiteParameter(":type", meta.Type));
                        cmd.Parameters.Add(new SQLiteParameter(":hash", hash));
                        cmd.Parameters.Add(new SQLiteParameter(":asset_flags", (int)meta.Flags));
                        cmd.ExecuteNonQuery();
                    }
                }

                return true;
            }
            catch (SQLiteException e)
            {
                // Concurrent insert of the same id: treat as already stored so regions do not retry.
                if (e.ResultCode == SQLiteErrorCode.Constraint)
                    return true;

                m_log.Error("[FSAssets] Failed to store asset with ID " + meta.ID);
                m_log.Error(e.ToString());
                return false;
            }
            catch (Exception e)
            {
                m_log.Error("[FSAssets] Failed to store asset with ID " + meta.ID);
                m_log.Error(e.ToString());
                return false;
            }
        }

        public bool[] AssetsExist(UUID[] uuids)
        {
            if (uuids.Length == 0)
                return Array.Empty<bool>();

            bool[] results = new bool[uuids.Length];
            HashSet<UUID> exists = new HashSet<UUID>();

            string ids = "'" + string.Join("','", uuids) + "'";
            string sql = string.Format("select id from {1} where id in ({0})", ids, m_Table);

            lock (m_lock)
            {
                if (m_conn == null)
                    return results;

                using (SQLiteCommand cmd = new SQLiteCommand(sql, m_conn))
                {
                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            exists.Add(new UUID(ReaderString(reader, "id")));
                        }
                    }
                }
            }

            for (int i = 0; i < uuids.Length; i++)
                results[i] = exists.Contains(uuids[i]);
            return results;
        }

        public int Count()
        {
            lock (m_lock)
            {
                if (m_conn == null)
                    return 0;

                using (SQLiteCommand cmd = new SQLiteCommand(m_conn))
                {
                    cmd.CommandText = string.Format("select count(*) as count from {0}", m_Table);
                    object result = cmd.ExecuteScalar();
                    return Convert.ToInt32(result);
                }
            }
        }

        public bool Delete(string id)
        {
            lock (m_lock)
            {
                if (m_conn == null)
                    return false;

                using (SQLiteCommand cmd = new SQLiteCommand(m_conn))
                {
                    cmd.CommandText = string.Format("delete from {0} where id = :id", m_Table);
                    cmd.Parameters.Add(new SQLiteParameter(":id", id));
                    cmd.ExecuteNonQuery();
                }
            }

            return true;
        }

        public void Import(string conn, string table, int start, int count, bool force, FSStoreDelegate store)
        {
            int imported = 0;

            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            using (SQLiteConnection importConn = new SQLiteConnection(conn))
            {
                try
                {
                    importConn.Open();
                    SQLiteConnectionHelper.Configure(importConn, conn);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[FSASSETS]: Can't connect to database: {0}", e.Message);
                    return;
                }

                string limit = string.Empty;
                if (count != -1)
                    limit = string.Format(" limit {0} offset {1}", count, start);

                using (SQLiteCommand cmd = new SQLiteCommand(string.Format("select * from {0}{1}", table, limit), importConn))
                {
                    Output("Querying database");
                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        Output("Reading data");

                        bool sqliteSchema = HasColumn(reader, "UUID");
                        bool hasCreateTime = HasColumn(reader, "create_time");
                        bool hasFlags = HasColumn(reader, "asset_flags");

                        while (reader.Read())
                        {
                            if ((imported % 100) == 0)
                                Output(string.Format("{0} assets imported so far", imported));

                            AssetBase asset = new AssetBase();
                            AssetMetadata meta = new AssetMetadata();

                            if (sqliteSchema)
                            {
                                meta.ID = ReaderString(reader, "UUID");
                                meta.Name = ReaderString(reader, "Name");
                                meta.Description = ReaderString(reader, "Description");
                                meta.Type = (sbyte)Convert.ToInt32(reader["Type"]);
                                asset.Data = reader["Data"] as byte[] ?? Array.Empty<byte>();
                            }
                            else
                            {
                                meta.ID = ReaderString(reader, "id");
                                meta.Name = ReaderString(reader, "name");
                                meta.Description = ReaderString(reader, "description");
                                string typeCol = HasColumn(reader, "assetType") ? "assetType" : "type";
                                meta.Type = (sbyte)Convert.ToInt32(reader[typeCol]);
                                object data = HasColumn(reader, "data") ? reader["data"] : reader["Data"];
                                asset.Data = data as byte[] ?? Array.Empty<byte>();
                            }

                            meta.FullID = new UUID(meta.ID);
                            meta.ContentType = SLUtil.SLAssetTypeToContentType(meta.Type);
                            if (hasCreateTime)
                                meta.CreationDate = Util.ToDateTime(Convert.ToInt32(reader["create_time"]));
                            else
                                meta.CreationDate = DateTime.UtcNow;
                            if (hasFlags)
                                meta.Flags = (AssetFlags)Convert.ToInt32(reader["asset_flags"]);

                            asset.Metadata = meta;
                            store(asset, force);
                            imported++;
                        }
                    }
                }
            }

            Output(string.Format("Import done, {0} assets imported", imported));
        }

        private static void Output(string message)
        {
            if (MainConsole.Instance != null)
                MainConsole.Instance.Output(message);
            else
                m_log.Info("[FSASSETS]: " + message);
        }

        private static string ReaderString(IDataReader reader, string column)
        {
            object value = reader[column];
            if (value == null || value is DBNull)
                return string.Empty;
            return value.ToString();
        }

        private static bool HasColumn(IDataReader reader, string name)
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
