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
using System.Threading;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using TeleportFlags = OpenSim.Framework.Constants.TeleportFlags;

namespace OpenSim.Region.OptionalModules.Aura
{
    /// <summary>
    /// Sends a local-chat line only to the arriving avatar on grid login
    /// and Hypergrid arrival. Other avatars do not see it; this is not a dialog.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "AuraLoginChatModule")]
    public class AuraLoginChatModule : INonSharedRegionModule
    {
        private const string LoginChatMessage = "This Sim is running on OpenSim-Aura. https://github.com/amandaleeang/OpenSim-Aura";
        private const string LoginChatFromName = "OpenSim-Aura";
        private const TeleportFlags LoginArrivalFlags = TeleportFlags.ViaLogin | TeleportFlags.ViaHGLogin;

        // Firestorm treats UUID.Zero as the region and shows "RegionName (FromName)".
        private static readonly UUID LoginChatSourceId = new UUID("a01a0000-0000-4000-8000-00000000a01a");

        public string Name { get { return "AuraLoginChatModule"; } }

        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource config)
        {
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
        }

        public void RemoveRegion(Scene scene)
        {
            scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        private void OnMakeRootAgent(ScenePresence sp)
        {
            if (sp == null || sp.IsNPC || sp.IsChildAgent || sp.ControllingClient == null)
                return;

            if ((sp.TeleportFlags & LoginArrivalFlags) == 0)
                return;

            // OnMakeRootAgent runs inside CompleteMovement, before RegionHandshake
            // and AgentMovementComplete. Sending chat here makes the viewer still
            // attribute the line to the previous region. Wait for the handshake
            // reply, then a short delay so the viewer has finished the switch.
            IClientAPI client = sp.ControllingClient;
            UUID agentId = sp.UUID;
            Scene scene = sp.Scene;

            Action<IClientAPI> handler = null;
            handler = (c) =>
            {
                c.OnRegionHandShakeReply -= handler;
                Util.FireAndForget(_ => SendLoginChat(scene, agentId));
            };
            client.OnRegionHandShakeReply += handler;
        }

        private static void SendLoginChat(Scene scene, UUID agentId)
        {
            Thread.Sleep(1000);

            ScenePresence sp = scene.GetScenePresence(agentId);
            if (sp == null || sp.IsDeleted || sp.IsNPC || sp.IsChildAgent || sp.ControllingClient == null)
                return;

            sp.ControllingClient.SendChatMessage(
                LoginChatMessage,
                (byte)ChatTypeEnum.Say,
                sp.AbsolutePosition,
                LoginChatFromName,
                LoginChatSourceId,
                sp.UUID,
                (byte)ChatSourceType.Object,
                (byte)ChatAudibleLevel.Fully);
        }
    }
}
