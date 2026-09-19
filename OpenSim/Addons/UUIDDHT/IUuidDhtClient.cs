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

using OpenMetaverse;
using OpenSim.Groups;

namespace OpenSim.Addons.UUIDDHT
{
    /// <summary>
    /// DHT locator record: uuid found plus full home URI (http:// or https://).
    /// The caller builds a UUI from this; nothing is written to GridUser.
    /// </summary>
    public class UuidDhtHome
    {
        public UUID UserID;
        public string HomeURI;
    }

    /// <summary>
    /// Name data from home GetUserInfo. DisplayName is empty when the home
    /// uses the default First.Last @host form. It is not part of the UUI.
    /// </summary>
    public class UuidDhtHomeName
    {
        public string FirstName;
        public string LastName;
        public string DisplayName;
    }

    /// <summary>
    /// UUID-keyed lookup used when the UUID is not a local UserAccount.
    /// </summary>
    public interface IUuidDhtClient
    {
        UuidDhtHome GetHomeByUuid(UUID userID);
        UuidDhtHome GetGroupHomeByUuid(UUID groupID);
    }

    /// <summary>
    /// Fetches First/Last (and optional display name) from the home grid.
    /// </summary>
    public interface IUuidDhtHomeNameService
    {
        UuidDhtHomeName GetName(UUID userID, string homeUri);
    }

    /// <summary>
    /// Fetches group identity from the home groups service.
    /// </summary>
    public interface IUuidDhtHomeGroupService
    {
        ExtendedGroupRecord GetGroup(UUID groupID, string homeUri);
    }
}
