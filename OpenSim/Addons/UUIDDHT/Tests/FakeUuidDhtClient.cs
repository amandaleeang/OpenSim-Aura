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
using System.Threading;
using OpenMetaverse;
using OpenSim.Groups;

namespace OpenSim.Addons.UUIDDHT.Tests
{
    public class FakeUuidDhtClient : IUuidDhtClient, IUuidDhtHomeNameService, IUuidDhtHomeGroupService
    {
        private int m_groupLookups;
        private int m_gridUserLookups;
        private int m_nameLookups;
        private int m_homeGroupLookups;

        public int GroupLookups { get { return m_groupLookups; } }
        public int GridUserLookups { get { return m_gridUserLookups; } }
        public int NameLookups { get { return m_nameLookups; } }
        public int HomeGroupLookups { get { return m_homeGroupLookups; } }

        public ConcurrentDictionary<UUID, UuidDhtHome> GroupHomes { get; } = new ConcurrentDictionary<UUID, UuidDhtHome>();
        public ConcurrentDictionary<UUID, ExtendedGroupRecord> HomeGroups { get; } = new ConcurrentDictionary<UUID, ExtendedGroupRecord>();
        public ConcurrentDictionary<UUID, UuidDhtHome> Homes { get; } = new ConcurrentDictionary<UUID, UuidDhtHome>();
        public ConcurrentDictionary<UUID, UuidDhtHomeName> Names { get; } = new ConcurrentDictionary<UUID, UuidDhtHomeName>();

        public UuidDhtHome GetHomeByUuid(UUID userID)
        {
            Interlocked.Increment(ref m_gridUserLookups);
            Homes.TryGetValue(userID, out UuidDhtHome home);
            return home;
        }

        public UuidDhtHome GetGroupHomeByUuid(UUID groupID)
        {
            Interlocked.Increment(ref m_groupLookups);
            GroupHomes.TryGetValue(groupID, out UuidDhtHome home);
            return home;
        }

        public ExtendedGroupRecord GetGroup(UUID groupID, string homeUri)
        {
            Interlocked.Increment(ref m_homeGroupLookups);
            HomeGroups.TryGetValue(groupID, out ExtendedGroupRecord record);
            return record;
        }

        public UuidDhtHomeName GetName(UUID userID, string homeUri)
        {
            Interlocked.Increment(ref m_nameLookups);
            Names.TryGetValue(userID, out UuidDhtHomeName name);
            return name;
        }
    }
}
