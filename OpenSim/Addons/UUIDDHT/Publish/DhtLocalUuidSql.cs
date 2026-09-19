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
using System.Reflection;
using log4net;
using MySql.Data.MySqlClient;
using Nini.Config;
using Npgsql;
using OpenMetaverse;

namespace OpenSim.Addons.UUIDDHT
{
    /// <summary>
    /// Local user and group UUIDs for DHT backfill. Reads the existing
    /// UserAccounts / Groups connection strings; does not extend OpenSim.Data.
    /// </summary>
    public interface IDhtLocalUuidSource
    {
        IList<UUID> ListUserIds();
        IList<UUID> ListGroupIds();
    }

    public sealed class DhtLocalUuidSql : IDhtLocalUuidSource
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly UUID UuidZero = UUID.Zero;

        private enum SqlKind
        {
            Sqlite,
            MySql,
            PgSql
        }

        private readonly SqlKind m_kind;
        private readonly string m_userConn;
        private readonly string m_userTable;
        private readonly string m_groupConn;
        private readonly string m_groupTable;
        private readonly bool m_ready;

        public DhtLocalUuidSql(IConfigSource config)
        {
            if (config == null)
                return;

            string userProvider = Read(config, "UserAccountService", "StorageProvider",
                Read(config, "DatabaseService", "StorageProvider", string.Empty));
            m_userConn = Read(config, "UserAccountService", "ConnectionString",
                Read(config, "DatabaseService", "ConnectionString", string.Empty));
            m_userTable = SanitizeIdent(Read(config, "UserAccountService", "Realm", "UserAccounts"));

            string groupProvider = Read(config, "Groups", "StorageProvider", userProvider);
            m_groupConn = Read(config, "Groups", "ConnectionString", m_userConn);
            string groupRealm = SanitizeIdent(Read(config, "Groups", "Realm", "os_groups"));
            m_groupTable = string.IsNullOrEmpty(groupRealm) ? string.Empty : groupRealm + "_groups";

            m_kind = Detect(userProvider);
            m_ready = !string.IsNullOrEmpty(m_userConn) && !string.IsNullOrEmpty(m_userTable);
        }

        public IList<UUID> ListUserIds()
        {
            if (!m_ready)
                return Array.Empty<UUID>();
            string table = Quote(m_userTable);
            string scope = Quote("ScopeID");
            string principal = Quote("PrincipalID");
            string sql = "SELECT " + principal + " FROM " + table
                + " WHERE (" + scope + "=" + Param("s") + " OR " + scope + "='" + UuidZero + "')";
            return Query(m_userConn, sql, "s", UuidZero.ToString());
        }

        public IList<UUID> ListGroupIds()
        {
            if (!m_ready || string.IsNullOrEmpty(m_groupTable) || string.IsNullOrEmpty(m_groupConn))
                return Array.Empty<UUID>();
            string table = Quote(m_groupTable);
            string groupId = Quote("GroupID");
            string loc = Quote("Location");
            string sql = "SELECT " + groupId + " FROM " + table
                + " WHERE (" + loc + " IS NULL OR " + loc + "='')";
            return Query(m_groupConn, sql, null, null);
        }

        private IList<UUID> Query(string connString, string sql, string paramName, string paramValue)
        {
            List<UUID> ids = new List<UUID>();
            try
            {
                using IDbConnection conn = Open(connString);
                conn.Open();
                using IDbCommand cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                if (!string.IsNullOrEmpty(paramName))
                    AddParam(cmd, paramName, paramValue);
                using IDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string s = r.IsDBNull(0) ? null : Convert.ToString(r.GetValue(0));
                    if (UUID.TryParse(s, out UUID id) && !id.IsZero())
                        ids.Add(id);
                }
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[UUID-DHT]: local UUID SQL failed: {0}", e.Message);
            }
            return ids;
        }

        private IDbConnection Open(string connString)
        {
            switch (m_kind)
            {
                case SqlKind.MySql:
                    return new MySqlConnection(connString);
                case SqlKind.PgSql:
                    return new NpgsqlConnection(connString);
                default:
                    return new SQLiteConnection(connString);
            }
        }

        private void AddParam(IDbCommand cmd, string name, string value)
        {
            IDbDataParameter p = cmd.CreateParameter();
            p.ParameterName = m_kind == SqlKind.PgSql ? name : (m_kind == SqlKind.MySql ? "?" + name : "@" + name);
            p.Value = value ?? (object)DBNull.Value;
            cmd.Parameters.Add(p);
        }

        private string Param(string name)
        {
            if (m_kind == SqlKind.PgSql)
                return ":" + name;
            if (m_kind == SqlKind.MySql)
                return "?" + name;
            return "@" + name;
        }

        private string Quote(string ident)
        {
            if (m_kind == SqlKind.PgSql)
                return "\"" + ident + "\"";
            return ident;
        }

        private static SqlKind Detect(string storageProvider)
        {
            string s = storageProvider != null ? storageProvider.ToLowerInvariant() : string.Empty;
            if (s.Contains("pgsql") || s.Contains("postgres") || s.Contains("npgsql"))
                return SqlKind.PgSql;
            if (s.Contains("mysql"))
                return SqlKind.MySql;
            return SqlKind.Sqlite;
        }

        private static string Read(IConfigSource config, string section, string key, string fallback)
        {
            IConfig c = config.Configs[section];
            if (c == null)
                return fallback ?? string.Empty;
            return c.GetString(key, fallback ?? string.Empty) ?? string.Empty;
        }

        private static string SanitizeIdent(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                bool ok = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_';
                if (!ok)
                    return string.Empty;
            }
            return name;
        }
    }
}
