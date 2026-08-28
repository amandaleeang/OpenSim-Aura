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
using System.Data;
using System.Reflection;
using log4net;

namespace OpenSim.Data
{
    /// <summary>
    /// Per-connection SQLite pragmas: WAL, busy timeout, NORMAL sync, memory temp store.
    /// Large page cache is applied only for the default standalone OpenSim.db / Asset.db files.
    /// </summary>
    public static class SQLiteConnectionHelper
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static void Configure(IDbConnection conn)
        {
            if (conn == null)
                return;

            Configure(conn, conn.ConnectionString);
        }

        public static void Configure(IDbConnection conn, string connectionString)
        {
            if (conn == null || conn.State != ConnectionState.Open)
                return;

            // Wait on SQLITE_BUSY instead of failing immediately (writer vs writer still contends in WAL).
            Exec(conn, "PRAGMA busy_timeout=30000");

            object mode = Exec(conn, "PRAGMA journal_mode=WAL");
            if (mode != null && !string.Equals(Convert.ToString(mode), "wal", StringComparison.OrdinalIgnoreCase))
            {
                m_log.WarnFormat(
                    "[SQLITE]: journal_mode=WAL was not applied (got {0}); filesystem may not support WAL",
                    mode);
            }

            // With WAL, NORMAL is durable across process crash; last txn can be lost on OS/power failure.
            Exec(conn, "PRAGMA synchronous=NORMAL");
            Exec(conn, "PRAGMA temp_store=MEMORY");

            if (IsLargeDatabase(connectionString))
                Exec(conn, "PRAGMA cache_size=-65536");
        }

        public static void CheckpointTruncate(IDbConnection conn)
        {
            if (conn == null || conn.State != ConnectionState.Open)
                return;

            try
            {
                Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE)");
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[SQLITE]: wal_checkpoint(TRUNCATE) skipped: {0}", e.Message);
            }
        }

        private static bool IsLargeDatabase(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
                return false;

            return connectionString.IndexOf("OpenSim.db", StringComparison.OrdinalIgnoreCase) >= 0
                || connectionString.IndexOf("Asset.db", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object Exec(IDbConnection conn, string sql)
        {
            using (IDbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                return cmd.ExecuteScalar();
            }
        }
    }
}
