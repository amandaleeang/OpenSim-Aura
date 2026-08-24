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
using log4net;
using Nini.Config;
using Nwc.XmlRpc;
using Mono.Addins;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using PresenceInfo = OpenSim.Services.Interfaces.PresenceInfo;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Connectors.InstantMessage;
using OpenSim.Services.Connectors.Hypergrid;
using OpenSim.Server.Handlers.Hypergrid;

namespace OpenSim.Region.CoreModules.Avatar.InstantMessage
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "HGMessageTransferModule")]
    public class HGMessageTransferModule : ISharedRegionModule, IMessageTransferModule, IInstantMessageSimConnector
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected bool m_Enabled = false;
        protected List<Scene> m_Scenes = [];

        protected IInstantMessage m_IMService;
        protected Dictionary<UUID, object> m_UserLocationMap = new();

        public event UndeliveredMessage OnUndeliveredMessage;

        IUserManagement m_uMan;
        IUserManagement UserManagementModule
        {
            get
            {
                m_uMan ??= m_Scenes[0].RequestModuleInterface<IUserManagement>();
                return m_uMan;
            }
        }

        public virtual void Initialise(IConfigSource config)
        {
            IConfig cnf = config.Configs["Messaging"];
            if (cnf != null && cnf.GetString("MessageTransferModule", "MessageTransferModule") != Name)
            {
                m_log.Debug("[HG MESSAGE TRANSFER]: Disabled by configuration");
                return;
            }

            InstantMessageServerConnector imServer = new(config, MainServer.Instance, this);
            m_IMService = imServer.GetService();
            m_Enabled = true;
        }

        public virtual void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            lock (m_Scenes)
            {
                m_log.Debug($"[HG MESSAGE TRANSFER]: Message transfer module {Name} active");
                scene.RegisterModuleInterface<IMessageTransferModule>(this);
                m_Scenes.Add(scene);
            }
        }

        public virtual void PostInitialise()
        {
        }

        public virtual void RegionLoaded(Scene scene)
        {
        }

        public virtual void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            lock (m_Scenes)
            {
                m_Scenes.Remove(scene);
            }
        }

        public virtual void Close()
        {
        }

        public virtual string Name
        {
            get { return "HGMessageTransferModule"; }
        }

        public virtual Type ReplaceableInterface
        {
            get { return null; }
        }

        public void SendInstantMessage(GridInstantMessage im, MessageResultNotification result, bool ToRootOnly = false)
        {
            UUID toAgentID = new(im.toAgentID);
            if (toAgentID.IsZero())
                return;

            StampSenderUUI(im);
            RememberIMContactFromMessage(im);

            ScenePresence achildsp = null;
            // Try root avatar first
            foreach (Scene scene in m_Scenes)
            {
                ScenePresence sp = scene.GetScenePresence(toAgentID);
                if (sp != null && !sp.IsDeleted)
                {
                    if(sp.IsChildAgent)
                        achildsp = sp;
                    else
                    {
                        sp.ControllingClient.SendInstantMessage(im);
                        result(true);
                        return;
                    }
                }
            }
            if (!ToRootOnly && achildsp != null)
            {
                achildsp.ControllingClient.SendInstantMessage(im);
                result(true);
                return;
            }

            // Identity is UUI everywhere. Location is separate:
            // local account → presence / LocateUser; foreign → IMServerURI / HomeURL / get_uui.
            string url = string.Empty;
            bool foreigner = false;
            if (UserManagementModule != null)
            {
                foreigner = !UserManagementModule.IsLocalGridUser(toAgentID);
                if(foreigner)
                {
                    url = UserManagementModule.GetUserServerURL(toAgentID, "IMServerURI");
                    if (string.IsNullOrEmpty(url))
                        url = UserManagementModule.GetUserHomeURL(toAgentID);
                }
            }
            Util.FireAndForget(delegate
                {
                    UUID toDelAgentID = new UUID(im.toAgentID);
                    bool success = false;
                    if (foreigner && url.Length == 0)
                    {
                        string recipientUUI = TryGetRecipientUUI(new UUID(im.fromAgentID), toDelAgentID);
                        if (recipientUUI.Length > 0
                                && Util.ParseFullUniversalUserIdentifier(recipientUUI, out _, out string tourl,
                                    out string first, out string last) && !string.IsNullOrEmpty(tourl))
                        {
                            RememberContact(toDelAgentID, first, last, tourl);
                            success = m_IMService.OutgoingInstantMessage(im, tourl, true);
                        }
                        if (!success)
                            m_log.DebugFormat(
                                "[HG MESSAGE TRANSFER]: Cannot locate user {0} for IM (no UUI / HomeURL)",
                                toDelAgentID);
                    }
                    else
                    {
                        if (foreigner && !string.IsNullOrEmpty(url))
                            RememberForeignDestination(toDelAgentID, url);
                        success = m_IMService.OutgoingInstantMessage(im, url, foreigner);
                    }

                    if (!success && !foreigner)
                        HandleUndeliverableMessage(im, result);
                    else
                        result(success);
                }, null, "HGMessageTransferModule.SendInstantMessage");
        }

        protected bool SendIMToScene(GridInstantMessage gim, UUID toAgentID)
        {
            Scene childScene = null;
            foreach (Scene scene in m_Scenes)
            {
                ScenePresence sp = scene.GetScenePresence(toAgentID);
                if(sp != null && !sp.IsDeleted)
                {
                    if(sp.IsChildAgent)
                        childScene = scene;
                    else
                    {
                        scene.EventManager.TriggerIncomingInstantMessage(gim);
                        return true;
                    }
                }
            }

            if(childScene != null)
            {
                childScene.EventManager.TriggerIncomingInstantMessage(gim);
                return true;
            }

            // If the message can't be delivered to an agent, it
            // is likely to be a group IM. On a group IM, the
            // imSessionID = toAgentID = group id. Raise the
            // unhandled IM event to give the groups module
            // a chance to pick it up. We raise that in a random
            // scene, since the groups module is shared.
            //
            m_Scenes[0].EventManager.TriggerUnhandledInstantMessage(gim);
            return false;
        }

        public void HandleUndeliverableMessage(GridInstantMessage im, MessageResultNotification result)
        {
            UndeliveredMessage handlerUndeliveredMessage = OnUndeliveredMessage;

            // If this event has handlers, then an IM from an agent will be
            // considered delivered. This will suppress the error message.
            //
            if (handlerUndeliveredMessage != null)
            {
                handlerUndeliveredMessage(im);
                if (im.dialog == (byte)InstantMessageDialog.MessageFromAgent)
                    result(true);
                else
                    result(false);
                return;
            }

            //m_log.DebugFormat("[INSTANT MESSAGE]: Undeliverable");
            result(false);
        }

        private string TryGetRecipientUUI(UUID fromAgent, UUID toAgent)
        {
            // Sender home get_uui: local accounts, friends, and GridUser IM contacts.
            string uasURL = null;
            AgentCircuitData circuit = m_Scenes[0].AuthenticateHandler.GetAgentCircuitData(fromAgent);
            if (circuit?.ServiceURLs is not null && circuit.ServiceURLs.ContainsKey("HomeURI"))
                uasURL = circuit.ServiceURLs["HomeURI"]?.ToString();
            if (string.IsNullOrWhiteSpace(uasURL) && UserManagementModule is not null)
                uasURL = UserManagementModule.GetUserHomeURL(fromAgent);
            if (string.IsNullOrWhiteSpace(uasURL) && m_Scenes.Count > 0)
                uasURL = m_Scenes[0].SceneGridInfo?.GateKeeperURL;
            if (string.IsNullOrWhiteSpace(uasURL))
                return string.Empty;

            m_log.DebugFormat("[HG MESSAGE TRANSFER]: getting UUI of user {0} from {1}", toAgent, uasURL);
            try
            {
                UserAgentServiceConnector uasConn = new(uasURL);
                return uasConn.GetUUI(fromAgent, toAgent) ?? string.Empty;
            }
            catch (Exception e)
            {
                m_log.Debug("[HG MESSAGE TRANSFER]: GetUUI call failed ", e);
                return string.Empty;
            }
        }

        /// <summary>
        /// Find the root client for a ID
        /// </summary>
        public IClientAPI LocateClientObject(UUID agentID)
        {
            lock (m_Scenes)
            {
                foreach (Scene scene in m_Scenes)
                {
                    ScenePresence presence = scene.GetScenePresence(agentID);
                    if (presence != null && !presence.IsChildAgent && !presence.IsDeleted)
                        return presence.ControllingClient;
                }
            }

            return null;
        }

        #region IInstantMessageSimConnector
        public bool SendInstantMessage(GridInstantMessage im)
        {
            RememberIMContactFromMessage(im);
            UUID agentID = new UUID(im.toAgentID);
            return SendIMToScene(im, agentID);
        }
        #endregion

        /// <summary>
        /// Always stamp the sender as a UUI (uuid;home;name), local or HG.
        /// Extra XML-RPC fields are ignored by stock grids.
        /// </summary>
        void StampSenderUUI(GridInstantMessage im)
        {
            if (im == null || im.fromGroup)
                return;

            UUID fromId = new(im.fromAgentID);
            if (fromId.IsZero() || m_Scenes.Count == 0)
                return;

            if (string.IsNullOrWhiteSpace(im.fromAgentHomeURI))
            {
                try
                {
                    AgentCircuitData circuit = m_Scenes[0].AuthenticateHandler?.GetAgentCircuitData(fromId);
                    if (circuit?.ServiceURLs is not null
                            && circuit.ServiceURLs.TryGetValue("HomeURI", out object hobj) && hobj != null
                            && !string.IsNullOrWhiteSpace(hobj.ToString()))
                        im.fromAgentHomeURI = hobj.ToString();
                }
                catch (Exception e)
                {
                    m_log.Debug($"[HG MESSAGE TRANSFER]: StampSenderUUI circuit lookup failed: {e.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(im.fromAgentHomeURI) && UserManagementModule is not null)
                im.fromAgentHomeURI = UserManagementModule.GetUserHomeURL(fromId);

            if (string.IsNullOrWhiteSpace(im.fromAgentHomeURI)
                    && UserManagementModule is not null && UserManagementModule.IsLocalGridUser(fromId))
                im.fromAgentHomeURI = m_Scenes[0].SceneGridInfo?.GateKeeperURL ?? string.Empty;
        }

        void RememberIMContactFromMessage(GridInstantMessage im)
        {
            if (im == null || im.fromGroup || UserManagementModule is null || m_Scenes.Count == 0)
                return;

            switch (im.dialog)
            {
                case (byte)InstantMessageDialog.MessageFromAgent:
                case (byte)InstantMessageDialog.InventoryOffered:
                    break;
                default:
                    return;
            }

            UUID senderId = new(im.fromAgentID);
            if (senderId.IsZero() || UserManagementModule.IsLocalGridUser(senderId))
                return;

            string home = im.fromAgentHomeURI;
            if (string.IsNullOrWhiteSpace(home))
                home = GridInstantMessage.ResolveSenderHomeURI(null, null, im.fromAgentName);
            if (string.IsNullOrWhiteSpace(home))
                return;

            GridInstantMessage.SplitDisplayName(im.fromAgentName, out string first, out string last);
            RememberContact(senderId, first, last, home);
        }

        void RememberForeignDestination(UUID userId, string homeOrImUri)
        {
            if (userId.IsZero() || string.IsNullOrWhiteSpace(homeOrImUri) || UserManagementModule is null)
                return;
            if (UserManagementModule.IsLocalGridUser(userId))
                return;

            string first = "Unknown";
            string last = "User";
            if (UserManagementModule.GetUserName(userId, out string fn, out string ln))
            {
                GridInstantMessage.SplitDisplayName(fn + " " + ln, out first, out last);
            }
            RememberContact(userId, first, last, homeOrImUri);
        }

        /// <summary>
        /// Seed UserManagement and GridUser from a UUI. Same store get_uui reads
        /// for friends, IM contacts, and visitors.
        /// </summary>
        void RememberContact(UUID userId, string first, string last, string home)
        {
            if (userId.IsZero() || string.IsNullOrWhiteSpace(home) || UserManagementModule is null || m_Scenes.Count == 0)
                return;

            OSHHTPHost host = new(home);
            if (!host.IsValidHost)
                return;
            home = host.URI;

            if (string.IsNullOrWhiteSpace(first))
                first = "Unknown";
            if (string.IsNullOrWhiteSpace(last) || last.StartsWith('@'))
                last = "User";

            UserManagementModule.AddUser(userId, first, last, home);

            try
            {
                IGridUserService gridUser = m_Scenes[0].GridUserService;
                if (gridUser is null)
                    return;
                GridUserInfo existing = gridUser.GetGridUserInfo(userId.ToString());
                if (existing is not null && !string.IsNullOrEmpty(existing.UserID) && existing.UserID.Length > 36)
                    return;

                string uui = GridInstantMessage.BuildUUI(userId, first + " " + last, home);
                if (string.IsNullOrEmpty(uui))
                    return;
                gridUser.SetLastPosition(uui, UUID.Zero, UUID.Zero, Vector3.Zero, Vector3.Zero);
                m_log.DebugFormat("[HG MESSAGE TRANSFER]: Remembered IM contact {0} {1} @ {2}", first, last, home);
            }
            catch (Exception e)
            {
                m_log.Debug($"[HG MESSAGE TRANSFER]: Failed to persist IM contact {userId}: {e.Message}");
            }
        }

    }
}
