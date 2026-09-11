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

namespace OpenSim.Region.Framework.Interfaces
{
    public interface IServerSideBakeModule
    {
        /// <summary>
        /// Composite wearable textures for this presence and publish the bakes
        /// on its appearance. Returns true if at least one bake was produced
        /// or reused from cache.
        /// </summary>
        bool TryBake(IScenePresence sp);
        bool TryBake(IScenePresence sp, bool force);

        /// <summary>
        /// True when this presence already has bake JPEGs in this region's
        /// asset cache. Incoming avatars should not be announced to others
        /// until this is true, or SSA viewers fetch missing UUIDs and wait
        /// on the asset service.
        /// </summary>
        bool HasCachedBakes(IScenePresence sp);

        /// <summary>
        /// AgentCachedTexture from viewers that are not using SSA bit 0.
        /// Bake if needed and reply with server bake IDs. Returns true if this
        /// module will send the response.
        /// </summary>
        bool HandleCachedTextureRequest(IClientAPI client, int serial, List<CachedTextureRequestArg> request);

        /// <summary>
        /// Incoming attachment objects finished attaching after CompleteMovement.
        /// SSA viewers already received AvatarAppearance with an empty
        /// AttachmentBlock; republish so they rebuild mSimAttachments.
        /// </summary>
        void NotifyAttachmentsArrived(IScenePresence sp);

        /// <summary>
        /// Rewrite the cloned appearance used for AgentData so a destination
        /// without server-side bake (OSGrid, stock OpenSim) gets a classic
        /// packed appearance: vp 11000 cleared, attachment list matching live
        /// attachments. Bake JPEGs stay in cache/XBakes (not the asset DB).
        /// </summary>
        void PrepareAppearanceForTransfer(IScenePresence sp, AvatarAppearance outbound);

        /// <summary>
        /// Packet-only visual params for AvatarAppearance: copy grown so
        /// index 251 is 1. Does not mutate live appearance. cofVersion is
        /// the value chosen at publish (viewer COF, or slam+1 after Rebake).
        /// </summary>
        void PrepareAppearancePacket(UUID agentId, byte[] visualParams,
            out byte[] visualParamsForSend, out int cofVersion);

        /// <summary>
        /// Write AvatarAppearance AttachmentBlock at data[pos]. Returns new pos.
        /// </summary>
        int WriteAppearanceAttachmentBlock(UUID agentId, UUID toAgentId, byte[] data, int pos);
    }
}
