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
using System.Threading.Tasks;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;

namespace OpenSim.Addons.UUIDDHT
{
    public class UuidDhtGridUserService : GridUserService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const int MaxDhtLookupsPerBatch = 4;

        private readonly bool m_Enabled;
        private readonly bool m_InjectedClient;
        private readonly IConfigSource m_Config;
        private readonly IUuidDhtClient m_Client;
        private readonly IUuidDhtHomeNameService m_Names;
        private readonly IUserAccountService m_LocalUsers;
        private readonly string m_HomeURI;
        private readonly bool m_AllowPrivate;

        public UuidDhtGridUserService(IConfigSource config)
            : this(config, null, null, null)
        {
        }

        public UuidDhtGridUserService(IConfigSource config, IUuidDhtClient client)
            : this(config, client, null, null)
        {
        }

        public UuidDhtGridUserService(IConfigSource config, IUuidDhtClient client, IUuidDhtHomeNameService names)
            : this(config, client, names, null)
        {
        }

        public UuidDhtGridUserService(IConfigSource config, IUuidDhtClient client, IUuidDhtHomeNameService names,
            IUserAccountService localUsers)
            : base(config)
        {
            m_Config = config;
            m_Enabled = UuidDhtConfig.IsEnabled(config);
            m_InjectedClient = client != null;
            m_Client = client ?? (m_Enabled ? new UuidDhtClient(config) : null);
            m_LocalUsers = localUsers;
            UuidDhtConfig cfg = UuidDhtConfig.From(config);
            m_HomeURI = cfg.HomeURI;
            m_AllowPrivate = cfg.AllowPrivateHomeURI;
            m_Names = names ?? (m_Enabled
                ? new UuidDhtHomeNameService(cfg.HomeHttpTimeoutSec, cfg.PositiveCacheSec, cfg.NegativeCacheSec)
                : null);

            if (m_Enabled)
                m_log.Info("[UUID-DHT]: UUID DHT fallback enabled for GridUser lookup (SQL miss only; does not persist)");
        }

        public override GridUserInfo GetGridUserInfo(string userID)
        {
            GridUserInfo local = base.GetGridUserInfo(userID);
            if (local != null || !m_Enabled)
                return local;

            return TryGetGridUserFromDht(userID);
        }

        public override GridUserInfo[] GetGridUserInfo(string[] userIDs)
        {
            GridUserInfo[] result = new GridUserInfo[userIDs.Length];
            List<int> misses = new List<int>();

            for (int i = 0; i < userIDs.Length; i++)
            {
                result[i] = base.GetGridUserInfo(userIDs[i]);
                if (result[i] == null && m_Enabled)
                    misses.Add(i);
            }

            int n = misses.Count;
            if (n > MaxDhtLookupsPerBatch)
                n = MaxDhtLookupsPerBatch;
            if (n == 1)
                result[misses[0]] = TryGetGridUserFromDht(userIDs[misses[0]]);
            else if (n > 1)
            {
                Task[] tasks = new Task[n];
                for (int t = 0; t < n; t++)
                {
                    int i = misses[t];
                    tasks[t] = Task.Run(() => result[i] = TryGetGridUserFromDht(userIDs[i]));
                }
                Task.WaitAll(tasks);
            }

            return result;
        }

        private GridUserInfo TryGetGridUserFromDht(string userID)
        {
            if (m_Client == null)
                return null;

            string id = userID.Length > 36 ? userID.Substring(0, 36) : userID;
            if (!UUID.TryParse(id, out UUID uuid) || uuid.IsZero())
                return null;

            try
            {
                m_log.DebugFormat("[UUID-DHT]: SQL miss, querying DHT for {0}", uuid);
                UuidDhtHome home = m_Client.GetHomeByUuid(uuid);
                if (home == null || home.UserID.IsZero() || string.IsNullOrEmpty(home.HomeURI))
                {
                    m_log.DebugFormat("[UUID-DHT]: DHT miss for {0}", uuid);
                    return null;
                }

                string homeUri = home.HomeURI.Trim();
                if (!homeUri.EndsWith("/"))
                    homeUri += "/";

                m_log.DebugFormat("[UUID-DHT]: DHT hit uuid={0} home={1}", home.UserID, homeUri);
                UuidDhtHomeName name = ResolveName(home.UserID, homeUri);
                if (name == null || string.IsNullOrEmpty(name.FirstName) || string.IsNullOrEmpty(name.LastName))
                {
                    m_log.DebugFormat("[UUID-DHT]: GetUserInfo miss for {0} at {1}", home.UserID, homeUri);
                    return null;
                }

                string uui = home.UserID.ToString() + ";" + homeUri + ";" + name.FirstName + " " + name.LastName;
                m_log.DebugFormat(
                    "[UUID-DHT]: built UUI {0} display_name={1}",
                    uui, string.IsNullOrEmpty(name.DisplayName) ? "(default)" : name.DisplayName);
                return new GridUserInfo { UserID = uui };
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[UUID-DHT]: DHT lookup failed for {0}: {1}", uuid, e.Message);
                return null;
            }
        }

        private UuidDhtHomeName ResolveName(UUID userID, string homeUri)
        {
            if (DhtLocator.SameHome(homeUri, m_HomeURI, m_AllowPrivate))
            {
                IUserAccountService users = m_LocalUsers;
                if (users == null && !m_InjectedClient)
                    users = UuidDhtNode.GetOrCreate(m_Config)?.Users;
                if (users != null)
                {
                    UserAccount acct = users.GetUserAccount(UUID.Zero, userID);
                    if (acct != null && !string.IsNullOrEmpty(acct.FirstName) && !string.IsNullOrEmpty(acct.LastName))
                    {
                        return new UuidDhtHomeName
                        {
                            FirstName = acct.FirstName,
                            LastName = acct.LastName
                        };
                    }
                }
                m_log.DebugFormat("[UUID-DHT]: own HomeURI, skip GetUserInfo HTTP for {0}", userID);
                return null;
            }

            return m_Names != null ? m_Names.GetName(userID, homeUri) : null;
        }
    }
}
