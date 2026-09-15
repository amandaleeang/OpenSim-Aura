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

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Groups
{
    /// <summary>
    /// In-process groups service, loaded via [Groups] LocalServiceModule
    /// the same way IUserAccountService is loaded.
    /// </summary>
    public interface IGroupsService
    {
        UUID CreateGroup(string RequestingAgentID, string name, string charter, bool showInList, UUID insigniaID,
            int membershipFee, bool openEnrollment, bool allowPublish, bool maturePublish, UUID founderID, out string reason);
        void UpdateGroup(string RequestingAgentID, UUID groupID, string charter, bool showInList, UUID insigniaID,
            int membershipFee, bool openEnrollment, bool allowPublish, bool maturePublish);

        ExtendedGroupRecord GetGroupRecord(string RequestingAgentID, UUID GroupID);
        ExtendedGroupRecord GetGroupRecord(string RequestingAgentID, string GroupName);
        List<DirGroupsReplyData> FindGroups(string RequestingAgentID, string search);
        List<ExtendedGroupMembersData> GetGroupMembers(string RequestingAgentID, UUID GroupID);

        bool AddGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers, out string reason);
        bool UpdateGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers);
        void RemoveGroupRole(string RequestingAgentID, UUID groupID, UUID roleID);
        List<GroupRolesData> GetGroupRoles(string RequestingAgentID, UUID GroupID);
        List<ExtendedGroupRoleMembersData> GetGroupRoleMembers(string RequestingAgentID, UUID GroupID);

        bool AddAgentToGroup(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID, string token, out string reason);
        bool RemoveAgentFromGroup(string RequestingAgentID, string AgentID, UUID GroupID);

        bool AddAgentToGroupInvite(string RequestingAgentID, UUID inviteID, UUID groupID, UUID roleID, string agentID);
        GroupInviteInfo GetAgentToGroupInvite(string RequestingAgentID, UUID inviteID);
        void RemoveAgentToGroupInvite(string RequestingAgentID, UUID inviteID);

        bool AddAgentToGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID);
        bool RemoveAgentFromGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID);
        List<GroupRolesData> GetAgentGroupRoles(string RequestingAgentID, string AgentID, UUID GroupID);

        ExtendedGroupMembershipData SetAgentActiveGroup(string RequestingAgentID, string AgentID, UUID GroupID);
        ExtendedGroupMembershipData GetAgentActiveMembership(string RequestingAgentID, string AgentID);
        ExtendedGroupMembershipData GetAgentGroupMembership(string RequestingAgentID, string AgentID, UUID GroupID);
        List<GroupMembershipData> GetAgentGroupMemberships(string RequestingAgentID, string AgentID);

        void SetAgentActiveGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID);
        void UpdateMembership(string RequestingAgentID, string AgentID, UUID GroupID, bool AcceptNotices, bool ListInProfile);

        bool AddGroupNotice(string RequestingAgentID, UUID groupID, UUID noticeID, string fromName, string subject, string message,
            bool hasAttachment, byte attType, string attName, UUID attItemID, string attOwnerID);
        GroupNoticeInfo GetGroupNotice(string RequestingAgentID, UUID noticeID);
        List<ExtendedGroupNoticeData> GetGroupNotices(string RequestingAgentID, UUID groupID);
    }
}
