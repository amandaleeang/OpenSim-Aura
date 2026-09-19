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
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Groups;

namespace OpenSim.Addons.UUIDDHT
{
    public class UuidDhtGroupsService : GroupsService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly bool m_Enabled;
        private readonly IUuidDhtClient m_Client;
        private readonly IUuidDhtHomeGroupService m_HomeGroups;
        private readonly DhtPublisher m_Publisher;
        private readonly string m_HomeURI;
        private readonly bool m_AllowPrivate;

        public UuidDhtGroupsService(IConfigSource config)
            : this(config, null, null)
        {
        }

        public UuidDhtGroupsService(IConfigSource config, IUuidDhtClient client)
            : this(config, client, null)
        {
        }

        public UuidDhtGroupsService(IConfigSource config, IUuidDhtClient client, IUuidDhtHomeGroupService homeGroups)
            : base(config)
        {
            UuidDhtConfig cfg = UuidDhtConfig.From(config);
            m_Enabled = UuidDhtConfig.IsEnabled(config);
            m_Client = client ?? (m_Enabled ? new UuidDhtClient(config) : null);
            m_HomeURI = cfg.HomeURI;
            m_AllowPrivate = cfg.AllowPrivateHomeURI;
            m_HomeGroups = homeGroups ?? (m_Enabled
                ? new UuidDhtHomeGroupService(cfg.HomeHttpTimeoutSec, cfg.PositiveCacheSec, cfg.NegativeCacheSec)
                : null);

            if (m_Enabled && client == null)
            {
                UuidDhtNode node = UuidDhtNode.GetOrCreate(config);
                if (node != null)
                {
                    node.SetGroupsService(this);
                    m_Publisher = node.Publisher;
                }
            }

            if (m_Enabled)
                m_log.Info("[UUID-DHT]: UUID DHT fallback enabled for group lookup (SQL miss only; does not persist)");
        }

        public override UUID CreateGroup(string RequestingAgentID, string name, string charter, bool showInList,
            UUID insigniaID, int membershipFee, bool openEnrollment, bool allowPublish, bool maturePublish,
            UUID founderID, out string reason)
        {
            UUID id = base.CreateGroup(RequestingAgentID, name, charter, showInList, insigniaID, membershipFee,
                openEnrollment, allowPublish, maturePublish, founderID, out reason);
            if (m_Enabled && !id.IsZero() && m_Publisher != null)
                m_Publisher.PublishUuid(id);
            return id;
        }

        public override ExtendedGroupRecord GetGroupRecord(string RequestingAgentID, UUID GroupID)
        {
            ExtendedGroupRecord record = base.GetGroupRecord(RequestingAgentID, GroupID);
            if (record != null || !m_Enabled || GroupID.IsZero())
                return record;

            return TryGetGroupFromDht(GroupID);
        }

        /// <summary>
        /// Local SQL only. STORE owns must not FIND on a miss.
        /// </summary>
        internal ExtendedGroupRecord GetLocalGroupRecord(string RequestingAgentID, UUID GroupID)
        {
            return base.GetGroupRecord(RequestingAgentID, GroupID);
        }

        private ExtendedGroupRecord TryGetGroupFromDht(UUID groupID)
        {
            if (m_Client == null)
                return null;

            try
            {
                m_log.DebugFormat("[UUID-DHT]: SQL miss, querying DHT for group {0}", groupID);
                UuidDhtHome home = m_Client.GetGroupHomeByUuid(groupID);
                if (home == null || home.UserID.IsZero() || string.IsNullOrEmpty(home.HomeURI))
                {
                    m_log.DebugFormat("[UUID-DHT]: DHT miss for group {0}", groupID);
                    return null;
                }

                string homeUri = home.HomeURI.Trim();
                if (!homeUri.EndsWith("/"))
                    homeUri += "/";

                m_log.DebugFormat("[UUID-DHT]: DHT group hit uuid={0} home={1}", home.UserID, homeUri);
                ExtendedGroupRecord record;
                if (DhtLocator.SameHome(homeUri, m_HomeURI, m_AllowPrivate))
                    record = GetLocalGroupRecord(UUID.Zero.ToString(), home.UserID);
                else
                    record = m_HomeGroups != null ? m_HomeGroups.GetGroup(home.UserID, homeUri) : null;
                if (record == null)
                {
                    m_log.DebugFormat("[UUID-DHT]: GETGROUP miss for {0} at {1}", home.UserID, homeUri);
                    return null;
                }

                if (string.IsNullOrEmpty(record.ServiceLocation))
                    record.ServiceLocation = homeUri;

                m_log.DebugFormat("[UUID-DHT]: resolved group {0} name={1} home={2}", record.GroupID, record.GroupName, record.ServiceLocation);
                return record;
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[UUID-DHT]: DHT group lookup failed for {0}: {1}", groupID, e.Message);
                return null;
            }
        }
    }
}
