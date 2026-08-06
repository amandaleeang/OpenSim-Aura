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

using OpenSim.Framework;
using OpenSim.Framework.Client;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Connectors.Hypergrid;
using OpenSim.Services.Interfaces;
using OpenSim.Server.Base;

using OpenMetaverse;
using log4net;
using Nini.Config;
using Mono.Addins;

using GridRegion = OpenSim.Services.Interfaces.GridRegion;


namespace OpenSim.Region.CoreModules.Framework.EntityTransfer
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "HGEntityTransferModule")]
    public class HGEntityTransferModule : EntityTransferModule, IUserAgentVerificationModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private int m_levelHGTeleport = 0;

        private GatekeeperServiceConnector m_GatekeeperConnector;
        private IUserAgentService m_UAS;

        protected bool m_RestrictAppearanceAbroad;
        protected string m_AccountName;
        protected List<AvatarAppearance> m_ExportedAppearances;
        protected List<AvatarAttachment> m_Attachs;

        protected List<AvatarAppearance> ExportedAppearance
        {
            get
            {
                if (m_ExportedAppearances != null)
                    return m_ExportedAppearances;

                m_ExportedAppearances = new List<AvatarAppearance>();
                m_Attachs = new List<AvatarAttachment>();

                string[] names = m_AccountName.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string name in names)
                {
                    string[] parts = name.Trim().Split();
                    if (parts.Length != 2)
                    {
                        m_log.WarnFormat("[HG ENTITY TRANSFER MODULE]: Wrong user account name format {0}. Specify 'First Last'", name);
                        return null;
                    }
                    UserAccount account = m_scene.UserAccountService.GetUserAccount(UUID.Zero, parts[0], parts[1]);
                    if (account == null)
                    {
                        m_log.WarnFormat("[HG ENTITY TRANSFER MODULE]: Unknown account {0}", m_AccountName);
                        return null;
                    }
                    AvatarAppearance a = m_scene.AvatarService.GetAppearance(account.PrincipalID);
                    if (a != null)
                        m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Successfully retrieved appearance for {0}", name);

                    foreach (AvatarAttachment att in a.GetAttachments())
                    {
                        InventoryItemBase item = m_scene.InventoryService.GetItem(account.PrincipalID, att.ItemID);
                        if (item != null)
                            a.SetAttachment(att.AttachPoint, att.ItemID, item.AssetID);
                        else
                            m_log.WarnFormat("[HG ENTITY TRANSFER MODULE]: Unable to retrieve item {0} from inventory {1}", att.ItemID, name);
                    }

                    m_ExportedAppearances.Add(a);
                    m_Attachs.AddRange(a.GetAttachments());
                }

                return m_ExportedAppearances;
            }
        }

        /// <summary>
        /// Used for processing analysis of incoming attachments in a controlled fashion.
        /// </summary>
        private JobEngine m_incomingSceneObjectEngine;

        /// <summary>Max concurrent HG asset GETs per gatherer (compatible single-asset protocol).</summary>
        private int m_hgAssetFetchConcurrency = 8;

        /// <summary>Per-asset fetch timeout in milliseconds.</summary>
        private int m_hgAssetFetchTimeoutMs = 8000;

        /// <summary>Process-wide cap on concurrent foreign asset GETs (all visitors).</summary>
        private int m_hgAssetFetchGlobalConcurrency = 16;

        /// <summary>Per home AssetServerURI concurrent foreign GET cap.</summary>
        private int m_hgAssetFetchPerHostConcurrency = 8;

        /// <summary>When true, assets fetched for HG login attachments are persisted to the local asset
        /// database. When false (default) they are cache-only: transient, re-fetched on a later visit.</summary>
        private bool m_hgAssetStoreLoginAttachments = false;

        #region ISharedRegionModule

        public override string Name
        {
            get { return "HGEntityTransferModule"; }
        }

        public override void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];

            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("EntityTransferModule", "");
                if (name == Name)
                {
                    IConfig transferConfig = source.Configs["EntityTransfer"];
                    if (transferConfig != null)
                    {
                        m_levelHGTeleport = transferConfig.GetInt("LevelHGTeleport", 0);

                        m_RestrictAppearanceAbroad = transferConfig.GetBoolean("RestrictAppearanceAbroad", false);
                        if (m_RestrictAppearanceAbroad)
                        {
                            m_AccountName = transferConfig.GetString("AccountForAppearance", string.Empty);
                            if (m_AccountName.Length == 0)
                                m_log.WarnFormat("[HG ENTITY TRANSFER MODULE]: RestrictAppearanceAbroad is on, but no account has been given for avatar appearance!");
                        }

                        m_hgAssetFetchConcurrency = transferConfig.GetInt("HGAssetFetchConcurrency", m_hgAssetFetchConcurrency);
                        if (m_hgAssetFetchConcurrency < 1)
                            m_hgAssetFetchConcurrency = 1;
                        if (m_hgAssetFetchConcurrency > 32)
                            m_hgAssetFetchConcurrency = 32;

                        m_hgAssetFetchTimeoutMs = transferConfig.GetInt("HGAssetFetchTimeoutMs", m_hgAssetFetchTimeoutMs);
                        if (m_hgAssetFetchTimeoutMs < 500)
                            m_hgAssetFetchTimeoutMs = 500;

                        // Compatible admission control: still stock GET /assets/{uuid}; limits multi-visitor storms
                        m_hgAssetFetchGlobalConcurrency = transferConfig.GetInt(
                            "HGAssetFetchGlobalConcurrency", m_hgAssetFetchGlobalConcurrency);
                        if (m_hgAssetFetchGlobalConcurrency < 1)
                            m_hgAssetFetchGlobalConcurrency = 1;
                        if (m_hgAssetFetchGlobalConcurrency > 64)
                            m_hgAssetFetchGlobalConcurrency = 64;

                        m_hgAssetFetchPerHostConcurrency = transferConfig.GetInt(
                            "HGAssetFetchPerHostConcurrency", m_hgAssetFetchPerHostConcurrency);
                        if (m_hgAssetFetchPerHostConcurrency < 1)
                            m_hgAssetFetchPerHostConcurrency = 1;
                        if (m_hgAssetFetchPerHostConcurrency > 32)
                            m_hgAssetFetchPerHostConcurrency = 32;

                        m_hgAssetStoreLoginAttachments = transferConfig.GetBoolean(
                            "HGAssetStoreLoginAttachments", m_hgAssetStoreLoginAttachments);
                    }

                    InitialiseCommon(source);
                    HGUuidGatherer.ConfigureForeignFetchLimits(
                        m_hgAssetFetchGlobalConcurrency, m_hgAssetFetchPerHostConcurrency);
                    m_log.DebugFormat(
                        "[HG ENTITY TRANSFER MODULE]: {0} enabled (HGAssetFetchConcurrency={1}, HGAssetFetchTimeoutMs={2}, Global={3}, PerHost={4}, StoreLoginAttachments={5}).",
                        Name, m_hgAssetFetchConcurrency, m_hgAssetFetchTimeoutMs,
                        m_hgAssetFetchGlobalConcurrency, m_hgAssetFetchPerHostConcurrency,
                        m_hgAssetStoreLoginAttachments);
                }
            }
        }

        private void ConfigureHgGatherer(HGUuidGatherer gatherer)
        {
            if (gatherer is null)
                return;
            gatherer.FetchConcurrency = m_hgAssetFetchConcurrency;
            gatherer.FetchTimeoutMs = m_hgAssetFetchTimeoutMs;
            gatherer.StoreLocalToDatabase = m_hgAssetStoreLoginAttachments;
        }

        public override void AddRegion(Scene scene)
        {
            base.AddRegion(scene);

            if (m_Enabled)
            {
                scene.RegisterModuleInterface<IUserAgentVerificationModule>(this);
                //scene.EventManager.OnIncomingSceneObject += OnIncomingSceneObject;

                m_incomingSceneObjectEngine
                    = new JobEngine(
                        string.Format("HG Incoming Scene Object Engine ({0})", scene.Name),
                        "HG INCOMING SCENE OBJECT ENGINE", 30000);

                StatsManager.RegisterStat(
                    new Stat(
                        "HGIncomingAttachmentsWaiting",
                        "Number of incoming attachments waiting for processing.",
                        "",
                        "",
                        "entitytransfer",
                        Name,
                        StatType.Pull,
                        MeasuresOfInterest.None,
                        stat => stat.Value = m_incomingSceneObjectEngine.JobsWaiting,
                        StatVerbosity.Debug));

                m_incomingSceneObjectEngine.Start();
            }
        }

        public override void RegionLoaded(Scene scene)
        {
            base.RegionLoaded(scene);

            if (m_Enabled)
            {
                m_GatekeeperConnector = new GatekeeperServiceConnector(scene.AssetService);
                m_UAS = scene.RequestModuleInterface<IUserAgentService>();
                if (m_UAS == null)
                    m_UAS = new UserAgentServiceConnector(m_thisGridInfo.HomeURL);

            }
        }

        public override void RemoveRegion(Scene scene)
        {
            base.RemoveRegion(scene);

            if (m_Enabled)
            {
                scene.UnregisterModuleInterface<IUserAgentVerificationModule>(this);
                m_incomingSceneObjectEngine.Stop();
            }
        }

        #endregion

        #region HG overrides of IEntityTransferModule

        protected override GridRegion GetFinalDestination(GridRegion region, UUID agentID, string agentHomeURI, out string message)
        {
            int flags = m_scene.GridService.GetRegionFlags(m_sceneRegionInfo.ScopeID, region.RegionID);
            m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: region {0} flags: {1}", region.RegionName, flags);
            message = null;

            if ((flags & (int)OpenSim.Framework.RegionFlags.Hyperlink) != 0)
            {
                m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Destination region is hyperlink");
                GridRegion real_destination = m_GatekeeperConnector.GetHyperlinkRegion(region, region.RegionID, agentID, agentHomeURI, out message);
                if (real_destination != null)
                    m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: GetFinalDestination: ServerURI={0}", real_destination.ServerURI);
                else
                    m_log.WarnFormat("[HG ENTITY TRANSFER MODULE]: GetHyperlinkRegion of region {0} from Gatekeeper {1} failed: {2}", region.RegionID, region.ServerURI, message);
                return real_destination;
            }

            return region;
        }

        protected override bool NeedsClosing(GridRegion reg, bool OutViewRange)
        {
            if (OutViewRange)
                return true;

            int flags = m_scene.GridService.GetRegionFlags(m_sceneRegionInfo.ScopeID, reg.RegionID);
            if (flags == -1 || (flags & (int)OpenSim.Framework.RegionFlags.Hyperlink) != 0)
                return true;

            return false;
        }

        protected override void AgentHasMovedAway(ScenePresence sp, bool logout)
        {
            base.AgentHasMovedAway(sp, logout);
            if (logout)
            {
                // Log them out of this grid
                m_scene.PresenceService.LogoutAgent(sp.ControllingClient.SessionId);
                string userId = m_scene.UserManagementModule.GetUserUUI(sp.UUID);
                m_scene.GridUserService.LoggedOut(userId, UUID.Zero, m_sceneRegionInfo.RegionID, sp.AbsolutePosition, sp.Lookat);
            }
        }

        protected override bool CreateAgent(ScenePresence sp, GridRegion reg, GridRegion finalDestination, AgentCircuitData agentCircuit, uint teleportFlags, EntityTransferContext ctx, out string reason, out bool logout)
        {
            m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: CreateAgent {0} {1}", reg.ServerURI, finalDestination.ServerURI);
            reason = string.Empty;
            logout = false;
            int flags = Scene.GridService.GetRegionFlags(m_sceneRegionInfo.ScopeID, reg.RegionID);
            if (flags == -1 /* no region in DB */ || (flags & (int)OpenSim.Framework.RegionFlags.Hyperlink) != 0)
            {
                // this user is going to another grid
                // for local users, check if HyperGrid teleport is allowed, based on user level
                bool isLocal = m_scene.UserManagementModule.IsLocalGridUser(sp.UUID);
                if (isLocal && sp.GodController.UserLevel < m_levelHGTeleport)
                {
                    m_log.WarnFormat("[HG ENTITY TRANSFER MODULE]: Unable to HG teleport agent due to insufficient UserLevel.");
                    reason = "Hypergrid teleport not allowed";
                    return false;
                }

                if (agentCircuit.ServiceURLs.ContainsKey("HomeURI"))
                {
                    string userAgentDriver = agentCircuit.ServiceURLs["HomeURI"].ToString();
                    IUserAgentService connector;

                    if (m_thisGridInfo.IsLocalHome(userAgentDriver) == 1 && m_UAS != null)
                        connector = m_UAS;
                    else
                        connector = new UserAgentServiceConnector(userAgentDriver);

                    // Export region ServerURI when set so destination can fetch bakes
                    GridRegion source = new GridRegion(m_sceneRegionInfo);
                    if (string.IsNullOrEmpty(source.RawServerURI) && !string.IsNullOrEmpty(m_thisGridInfo.GateKeeperURL))
                        source.RawServerURI = m_thisGridInfo.GateKeeperURL;

                    bool success = connector.LoginAgentToGrid(source, agentCircuit, reg, finalDestination, false, out reason);
                    //logout = success & !isLocal; // flag for later logout from this grid; this is an HG TP
                    logout = success; // flag for later logout from this grid; this is an HG TP

                    if (success)
                        m_scene.EventManager.TriggerTeleportStart(sp.ControllingClient, reg, finalDestination, teleportFlags, logout);

                    return success;
                }
                else
                {
                    m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Agent does not have a HomeURI address");
                    return false;
                }
            }

            return base.CreateAgent(sp, reg, finalDestination, agentCircuit, teleportFlags, ctx, out reason, out logout);
        }

        public override void TriggerTeleportHome(UUID id, IClientAPI client)
        {
            TeleportHome(id, client);
        }

        protected override bool ValidateGenericConditions(ScenePresence sp, GridRegion reg, GridRegion finalDestination, uint teleportFlags, out string reason)
        {
            reason = "Please wear your grid's allowed appearance before teleporting to another grid";
            if (!m_RestrictAppearanceAbroad)
                return true;

            // The rest is only needed for controlling appearance

            int flags = m_scene.GridService.GetRegionFlags(m_sceneRegionInfo.ScopeID, reg.RegionID);
            if (flags == -1 /* no region in DB */ || (flags & (int)OpenSim.Framework.RegionFlags.Hyperlink) != 0)
            {
                // this user is going to another grid
                if (m_scene.UserManagementModule.IsLocalGridUser(sp.UUID))
                {
                    m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: RestrictAppearanceAbroad is ON. Checking generic appearance");

                    // Check wearables
                    for (int i = 0; i < sp.Appearance.Wearables.Length ; i++)
                    {
                        for (int j = 0; j < sp.Appearance.Wearables[i].Count; j++)
                        {
                            if (sp.Appearance.Wearables[i] == null)
                                continue;

                            bool found = false;
                            foreach (AvatarAppearance a in ExportedAppearance)
                                if (i < a.Wearables.Length && a.Wearables[i] != null)
                                {
                                    found = true;
                                    break;
                                }

                            if (!found)
                            {
                               m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Wearable not allowed to go outside {0}", i);
                               return false;
                            }

                            found = false;
                            foreach (AvatarAppearance a in ExportedAppearance)
                                if (i < a.Wearables.Length && sp.Appearance.Wearables[i][j].AssetID == a.Wearables[i][j].AssetID)
                                {
                                    found = true;
                                    break;
                                }

                            if (!found)
                            {
                                m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Wearable not allowed to go outside {0}", i);
                                return false;
                            }
                        }
                    }

                    // Check attachments
                    foreach (AvatarAttachment att in sp.Appearance.GetAttachments())
                    {
                        bool found = false;
                        foreach (AvatarAttachment att2 in m_Attachs)
                        {
                            if (att2.AssetID == att.AssetID)
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        {
                            m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Attachment not allowed to go outside {0}", att.AttachPoint);
                            return false;
                        }
                    }
                }
            }

            reason = string.Empty;
            return true;
        }


        //protected override bool UpdateAgent(GridRegion reg, GridRegion finalDestination, AgentData agentData, ScenePresence sp)
        //{
        //    int flags = Scene.GridService.GetRegionFlags(Scene.RegionInfo.ScopeID, reg.RegionID);
        //    if (flags == -1 /* no region in DB */ || (flags & (int)OpenSim.Data.RegionFlags.Hyperlink) != 0)
        //    {
        //        // this user is going to another grid
        //        if (m_RestrictAppearanceAbroad && Scene.UserManagementModule.IsLocalGridUser(agentData.AgentID))
        //        {
        //            // We need to strip the agent off its appearance
        //            m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: RestrictAppearanceAbroad is ON. Sending generic appearance");

        //            // Delete existing npc attachments
        //            Scene.AttachmentsModule.DeleteAttachmentsFromScene(sp, false);

        //            // XXX: We can't just use IAvatarFactoryModule.SetAppearance() yet since it doesn't transfer attachments
        //            AvatarAppearance newAppearance = new AvatarAppearance(ExportedAppearance, true);
        //            sp.Appearance = newAppearance;

        //            // Rez needed npc attachments
        //            Scene.AttachmentsModule.RezAttachments(sp);


        //            IAvatarFactoryModule module = Scene.RequestModuleInterface<IAvatarFactoryModule>();
        //            //module.SendAppearance(sp.UUID);
        //            module.RequestRebake(sp, false);

        //            Scene.AttachmentsModule.CopyAttachments(sp, agentData);
        //            agentData.Appearance = sp.Appearance;
        //        }
        //    }

        //    foreach (AvatarAttachment a in agentData.Appearance.GetAttachments())
        //        m_log.DebugFormat("[XXX]: {0}-{1}", a.ItemID, a.AssetID);


        //    return base.UpdateAgent(reg, finalDestination, agentData, sp);
        //}


        public override bool TeleportHome(UUID id, IClientAPI client)
        {
            // Let's find out if this is a foreign user or a local user
            IUserManagement uMan = m_scene.RequestModuleInterface<IUserManagement>();
            if (uMan != null && uMan.IsLocalGridUser(id))
            {
                // local grid user
                return base.TeleportHome(id, client);
            }

            bool notsame = false;
            if (client == null)
            {
                m_log.DebugFormat(
                    "[HG ENTITY TRANSFER MODULE]: Request to teleport {0} home", id);
            }
            else
            {
                if (id == client.AgentId)
                {
                    m_log.DebugFormat(
                        "[HG ENTITY TRANSFER MODULE]: Request to teleport {0} {1} home", client.Name, id);
                }
                else
                {
                    notsame = true;
                    m_log.DebugFormat(
                        "[HG ENTITY TRANSFER MODULE]: Request to teleport {0} home by {1} {2}", id, client.Name, client.AgentId);
                }
            }

            ScenePresence sp = ((Scene)(client.Scene)).GetScenePresence(id);
            if (sp == null || sp.IsDeleted || sp.IsChildAgent || sp.ControllingClient == null || !sp.ControllingClient.IsActive)
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent not found in the scene");
                m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Agent not found in the scene");
                return false;
            }

            IClientAPI targetClient = sp.ControllingClient;

            if (sp.IsInTransit)
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent already processing a teleport");
                targetClient.SendTeleportFailed("Already processing a teleport");
                m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Agent still in teleport");
                return false;
            }

            // Foreign user wants to go home
            //
            AgentCircuitData aCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(targetClient.CircuitCode);
            if (aCircuit == null)
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent information not found");
                targetClient.SendTeleportFailed("Home information not found");
                m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Unable to locate agent's gateway information");
                return false;
            }
            if (!aCircuit.ServiceURLs.ContainsKey("HomeURI"))
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent home not set");
                targetClient.SendTeleportFailed("Home not set");
                m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Agent home not set");
                return false;
            }

            string homeURI = aCircuit.ServiceURLs["HomeURI"].ToString();

            IUserAgentService userAgentService = new UserAgentServiceConnector(homeURI);
            Vector3 position = Vector3.UnitY, lookAt = Vector3.UnitY;

            GridRegion finalDestination = null;
            try
            {
                finalDestination = userAgentService.GetHomeRegion(id, out position, out lookAt);
            }
            catch (Exception e)
            {
                m_log.Debug("[HG ENTITY TRANSFER MODULE]: GetHomeRegion call failed ", e);
            }

            if (finalDestination == null)
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent Home region not found");
                targetClient.SendTeleportFailed("Home region not found");
                m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Agent's home region not found");
                return false;
            }

            GridRegion homeGatekeeper = MakeGateKeeperRegion(homeURI);

            m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: teleporting user {0} {1} home to {2} via {3}:{4}",
                aCircuit.firstname, aCircuit.lastname, finalDestination.RegionName, homeGatekeeper.ServerURI, homeGatekeeper.RegionName);

            DoTeleport(sp, homeGatekeeper, finalDestination, position, lookAt, (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaHome));
            return true;
        }

        /// <summary>
        /// Tries to teleport agent to landmark.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionHandle"></param>
        /// <param name="position"></param>
        public override void RequestTeleportLandmark(IClientAPI remoteClient, AssetLandmark lm, Vector3 lookAt)
        {
            if (lm == null || lm.Data == null || lm.Data.Length == 0)
                return;

            m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Teleporting agent via landmark to {0} region {1} position {2}",
                (string.IsNullOrEmpty(lm.Gatekeeper)) ? "local" : lm.Gatekeeper, lm.RegionID, lm.Position);

            ScenePresence sp = m_scene.GetScenePresence(remoteClient.AgentId);
            if (sp == null || sp.IsDeleted || sp.IsInTransit || sp.IsChildAgent || sp.IsNPC)
                return;

            OSHHTPHost gatekeeperHost = new OSHHTPHost(lm.Gatekeeper);
            if (m_thisGridInfo.IsLocalGrid(gatekeeperHost) != 0)
            {
                // Local region?
                GridRegion info = m_scene.GridService.GetRegionByUUID(UUID.Zero, lm.RegionID);
                if (info == null)
                {
                    remoteClient.SendTeleportFailed("Landmark region not found");
                    return;
                }

                //check if region on same position and fix local offset
                if (Util.CompareRegionHandles(lm.RegionHandle, lm.Position, info.RegionLocX, info.RegionLocY, info.RegionSizeX, info.RegionSizeY, out Vector3 offset))
                {
                    m_scene.RequestTeleportLocation(remoteClient, info.RegionHandle, offset,
                        lookAt, (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaLandmark));
                }
                else //region may had move to other grid slot. assume the lm position is good
                    m_scene.RequestTeleportLocation(remoteClient, info.RegionHandle, lm.Position,
                        lookAt, (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaLandmark));
                return;
            }

            if (!gatekeeperHost.ResolveDNS())
            {
                remoteClient.SendTeleportFailed("Could not resolve Landmark grid gatekeeper");
                return;
            }

            // Foreign region
            GridRegion gatekeeper = new GridRegion()
                {
                    ExternalHostName = gatekeeperHost.Host,
                    HttpPort = (uint)gatekeeperHost.Port,
                    ServerURI = lm.Gatekeeper,
                    RegionName = string.Empty,
                    InternalEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("0.0.0.0"), (int)0),
                    RegionFlags = OpenSim.Framework.RegionFlags.Hyperlink
                };

            string homeURI = m_scene.GetAgentHomeURI(remoteClient.AgentId);

            GatekeeperServiceConnector gConn = new GatekeeperServiceConnector();
            GridRegion finalDestination = gConn.GetHyperlinkRegion(gatekeeper, lm.RegionID, remoteClient.AgentId, homeURI, out string message);
            if(finalDestination == null)
            {
                remoteClient.SendTeleportFailed(message);
                return;
            }

            // Validate assorted conditions
            if (!ValidateGenericConditions(sp, gatekeeper, finalDestination, 0, out string reason))
            {
                remoteClient.SendTeleportFailed(reason);
                return;
            }

            if (Util.CompareRegionHandles(lm.RegionHandle, lm.Position, finalDestination.RegionLocX, finalDestination.RegionLocY,
                    finalDestination.RegionSizeX, finalDestination.RegionSizeY, out Vector3 roffset))
            {
                DoTeleport(sp, gatekeeper, finalDestination, roffset, lookAt,
                    (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaLandmark));
                return;
            }
            remoteClient.SendTeleportFailed("landmark region not found");
        }

        private void RemoveIncomingSceneObjectJobs(string commonIdToRemove)
        {
            List<JobEngine.Job> jobsToReinsert = new List<JobEngine.Job>();
            int jobsRemoved = 0;

            JobEngine.Job job;
            while ((job = m_incomingSceneObjectEngine.RemoveNextJob()) != null)
            {
                if (job.CommonId != commonIdToRemove)
                    jobsToReinsert.Add(job);
                else
                    jobsRemoved++;
            }

            m_log.DebugFormat(
                "[HG ENTITY TRANSFER]: Removing {0} jobs with common ID {1} and reinserting {2} other jobs",
                jobsRemoved, commonIdToRemove, jobsToReinsert.Count);

            if (jobsToReinsert.Count > 0)
            {
                foreach (JobEngine.Job jobToReinsert in jobsToReinsert)
                    m_incomingSceneObjectEngine.QueueJob(jobToReinsert);
            }
        }

        public override bool HandleIncomingSceneObject(SceneObjectGroup so, Vector3 newPosition)
        {
            UUID OwnerID = so.OwnerID;
            if (OwnerID.IsZero())
            {
                m_log.DebugFormat(
                    "[HG TRANSFER MODULE]: Denied object {0}({1}) entry into {2} because ownerID is zero",
                        so.Name, so.UUID, m_sceneName);
                return false;
            }

            if (m_sceneRegionInfo.EstateSettings.IsBanned(OwnerID))
            {
                m_log.DebugFormat(
                    "[HG TRANSFER MODULE]: Denied prim crossing of {0} {1} into {2} for banned avatar {3}",
                    so.Name, so.UUID, m_sceneName, so.OwnerID);

                return false;
            }

            // FIXME: We must make it so that we can use SOG.IsAttachment here.  At the moment it is always null!
            if (!so.IsAttachmentCheckFull())
                return base.HandleIncomingSceneObject(so, newPosition);

            // Equally, we can't use so.AttachedAvatar here.
            if (m_scene.UserManagementModule.IsLocalGridUser(OwnerID))
                return base.HandleIncomingSceneObject(so, newPosition);

            if (m_scene.GetScenePresence(OwnerID) == null)
            {
                m_log.DebugFormat(
                "[HG TRANSFER MODULE]: Denied attachment {0}({1}) owner {2} not in region {3}",
                    so.Name, so.UUID, OwnerID, m_sceneName);
                return false;
            }

            if (!m_scene.AddSceneObject(so))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Problem adding scene object {0} {1} into {2} ",
                    so.Name, so.UUID, m_sceneName);
                return false;
            }

            // foreign user
            AgentCircuitData aCircuit = m_scene.AuthenticateHandler.GetAgentCircuitData(OwnerID);
            if (aCircuit != null)
            {
                if ((aCircuit.teleportFlags & (uint)Constants.TeleportFlags.ViaHGLogin) == 0)
                {
                    // Already AddSceneObject'd above (assets pulled by first local region).
                    // base.HandleIncomingSceneObject would try to add again; just start scripts if root.
                    TryStartScriptsOnIncomingAttachment(so);
                }
                else
                {
                    if (aCircuit.ServiceURLs != null && aCircuit.ServiceURLs.ContainsKey("AssetServerURI"))
                    {
                        // Object is already AddSceneObject'd. Fetch script+notecard assets first and
                        // start scripts; pull remaining appearance assets afterward (script-first).
                        SceneObjectGroup defso = so;
                        AgentCircuitData defCircuit = aCircuit;
                        m_incomingSceneObjectEngine.QueueJob(
                            string.Format("HG UUID Gather for attachment {0} for {1}", defso.Name, defCircuit.Name),
                            () =>
                            {
                                if (defso.IsDeleted)
                                    return;

                                string url = defCircuit.ServiceURLs["AssetServerURI"].ToString();
                                HGUuidGatherer assetFetcher = new HGUuidGatherer(m_scene.AssetService, url);
                                ConfigureHgGatherer(assetFetcher);

                                List<UUID> startupAssetIds = CollectAttachmentStartupAssetIds(defso);
                                m_log.DebugFormat(
                                    "[HG ENTITY TRANSFER]: Single attachment {0} for {1}: fetching {2} script/notecard asset(s) then starting scripts",
                                    defso.Name, defCircuit.Name, startupAssetIds.Count);

                                assetFetcher.FetchAssetsParallel(startupAssetIds, () => defso.IsDeleted);

                                if (!defso.IsDeleted)
                                    TryStartScriptsOnIncomingAttachment(defso);

                                // Appearance / remaining assets (textures, mesh, …)
                                if (defso.IsDeleted)
                                    return;

                                IDictionary<UUID, sbyte> ids = new Dictionary<UUID, sbyte>();
                                HGUuidGatherer appearanceGatherer = new HGUuidGatherer(m_scene.AssetService, url, ids);
                                ConfigureHgGatherer(appearanceGatherer);
                                appearanceGatherer.AddForInspection(defso);
                                appearanceGatherer.GatherAllParallel(() => defso.IsDeleted);

                                // The object may have been visible while the gather ran, so a
                                // viewer that requested a not-yet-local texture got ImageNotFound (grey).
                                // Re-send a full update so viewers re-request the now-local assets.
                                if (!defso.IsDeleted)
                                {
                                    defso.ForEachPart(part => part.SendFullUpdateToAllClients());
                                    m_log.DebugFormat(
                                        "[HG ENTITY TRANSFER]: Refreshed appearance of attachment {0} for {1} after asset gather",
                                        defso.Name, defCircuit.Name);
                                }
                            },
                            OwnerID.ToString());
                    }
                    else
                    {
                        // No home asset URI; try scripts with whatever is already local.
                        TryStartScriptsOnIncomingAttachment(so);
                    }
                }
            }

            return true;
        }

        public override bool HandleIncomingAttachments(ScenePresence sp, List<SceneObjectGroup> attachments)
        {
            UUID OwnerID = sp.UUID;
            if (m_sceneRegionInfo.EstateSettings.IsBanned(OwnerID))
            {
                m_log.DebugFormat(
                    "[HG TRANSFER MODULE]: Attachments of banned avatar {0} into {1}", sp.Name, m_sceneName);
                return false;
            }

            if (OwnerID.IsZero() || m_scene.UserManagementModule.IsLocalGridUser(OwnerID))
                return base.HandleIncomingAttachments(sp, attachments);

            // foreign user
            AgentCircuitData aCircuit = m_scene.AuthenticateHandler.GetAgentCircuitData(OwnerID);
            if (aCircuit is null)
            {
                m_log.WarnFormat(
                    "[HG ENTITY TRANSFER]: No agent circuit for foreign user {0}; attaching {1} object(s) without HG asset gather",
                    sp.Name, attachments?.Count ?? 0);
                return base.HandleIncomingAttachments(sp, attachments);
            }

            if ((aCircuit.teleportFlags & (uint)Constants.TeleportFlags.ViaHGLogin) == 0)
            {
                // first region in local grid did pull the necessary attachments from the source grid.
                return base.HandleIncomingAttachments(sp, attachments);
            }

            if (attachments is null || attachments.Count == 0)
            {
                m_log.DebugFormat(
                    "[HG ENTITY TRANSFER]: HG user {0} ViaHGLogin with zero attachments to gather in {1}",
                    sp.Name, m_sceneName);
                sp.GotAttachmentsData = true;
                return true;
            }

            if (aCircuit.ServiceURLs is null || !aCircuit.ServiceURLs.ContainsKey("AssetServerURI"))
            {
                // Cannot pull from home asset server; still attach so scripts can start if assets are already local.
                m_log.WarnFormat(
                    "[HG ENTITY TRANSFER]: HG user {0} has no AssetServerURI; attaching {1} object(s) without remote gather",
                    sp.Name, attachments.Count);
                return base.HandleIncomingAttachments(sp, attachments);
            }

            ScenePresence defsp = sp;
            List<SceneObjectGroup> deftatt = attachments;

            // Mark before queue so SetAppearance during CompleteMovement defers rebake
            IAvatarFactoryModule avFactoryEarly = m_scene.RequestModuleInterface<IAvatarFactoryModule>();
            avFactoryEarly?.MarkAppearanceGatherInProgress(defsp.UUID);

            m_incomingSceneObjectEngine.QueueJob(
                string.Format("HG UUID Gather attachments {0}", defsp.Name), () =>
                {
                    IAvatarFactoryModule avFactory = m_scene.RequestModuleInterface<IAvatarFactoryModule>()
                        ?? avFactoryEarly;

                    try
                    {
                        string url = aCircuit.ServiceURLs["AssetServerURI"].ToString();
                        HGUuidGatherer assetFetcher = new HGUuidGatherer(m_scene.AssetService, url);
                        ConfigureHgGatherer(assetFetcher);

                        // Script-first path + parallel asset GET (still stock /assets/{uuid}).
                        m_log.DebugFormat(
                            "[HG ENTITY TRANSFER]: HG attachment script-first path for {0} ({1} object(s)) from {2} in {3} (concurrency={4}, timeoutMs={5})",
                            defsp.Name, deftatt.Count, url, m_sceneName, m_hgAssetFetchConcurrency, m_hgAssetFetchTimeoutMs);

                        List<SceneObjectGroup> attached = new List<SceneObjectGroup>(deftatt.Count);

                        // Fetch script+notecard assets for ALL attachments in ONE parallel batch
                        // instead of one blocking per-attachment foreign fetch. The old loop serialised a
                        // ~0.3-1.5s HTTP round-trip per attachment (observed ~40s for 30 attachments);
                        // fetching them together collapses the round-trips into a few parallel waves.
                        List<UUID> allStartupAssetIds = new List<UUID>();
                        HashSet<UUID> startupSeen = new HashSet<UUID>();
                        foreach (SceneObjectGroup defso in deftatt)
                        {
                            if (defso.OwnerID.NotEqual(defsp.UUID))
                                continue;

                            foreach (UUID assetId in CollectAttachmentStartupAssetIds(defso))
                            {
                                if (!assetId.IsZero() && startupSeen.Add(assetId))
                                    allStartupAssetIds.Add(assetId);
                            }
                        }

                        int scriptTickStart = Util.EnvironmentTickCount();

                        m_log.DebugFormat(
                            "[HG ENTITY TRANSFER]: Fetching {0} script/notecard asset(s) for {1} attachment(s) of {2} in one parallel batch (concurrency={3})",
                            allStartupAssetIds.Count, deftatt.Count, defsp.Name, m_hgAssetFetchConcurrency);

                        assetFetcher.FetchAssetsParallel(allStartupAssetIds, () => defsp.IsDeleted);

                        if (defsp.IsDeleted)
                            return;

                        int scriptFetchMs = Util.EnvironmentTickCountSubtract(scriptTickStart);

                        // Collect appearance asset UUIDs BEFORE the attach loop (incoming
                        // SOGs are fully formed and not TemporaryInstance, so inspection only reads
                        // part data), then run the appearance gather on a background thread so it
                        // overlaps the serial attach loop below. Previously the gather ran after the
                        // loop, adding its full runtime (~10s) to the login path.
                        IDictionary<UUID, sbyte> ids = new Dictionary<UUID, sbyte>();
                        HGUuidGatherer appearanceGatherer = new HGUuidGatherer(m_scene.AssetService, url, ids);
                        ConfigureHgGatherer(appearanceGatherer);
                        int appearanceTickStart = Util.EnvironmentTickCount();

                        m_log.DebugFormat(
                            "[HG ENTITY TRANSFER]: Collecting appearance asset UUIDs for {0} attachment(s) of {1} in {2} (parallel concurrency={3}, two-phase visual then audio/anim, overlapped with attach)",
                            deftatt.Count, defsp.Name, m_sceneName, m_hgAssetFetchConcurrency);

                        foreach (SceneObjectGroup defso in deftatt)
                        {
                            if (defsp.IsDeleted)
                                break;

                            if (defso.OwnerID.NotEqual(defsp.UUID))
                                continue;

                            appearanceGatherer.AddForInspection(defso);
                        }

                        Task appearanceGatherTask = null;
                        if (!defsp.IsDeleted)
                        {
                            appearanceTickStart = Util.EnvironmentTickCount();
                            appearanceGatherTask = Task.Run(
                                () => appearanceGatherer.GatherAllParallel(() => defsp.IsDeleted));
                        }

                        foreach (SceneObjectGroup defso in deftatt)
                        {
                            if (defsp.IsDeleted)
                                break;

                            if (defso.OwnerID.NotEqual(defsp.UUID))
                            {
                                m_log.ErrorFormat(
                                    "[HG TRANSFER MODULE] attachment {0}({1}) owner {2} does not match HG avatarID {3}",
                                    defso.Name, defso.UUID, defso.OwnerID, defsp.UUID);
                                continue;
                            }

                            if (!m_scene.AddSceneObject(defso))
                            {
                                m_log.DebugFormat(
                                    "[HG ENTITY TRANSFER]: Problem adding attachment {0} {1} for {2} into {3}",
                                    defso.Name, defso.UUID, defsp.Name, m_sceneName);
                                continue;
                            }

                            attached.Add(defso);

                            m_log.DebugFormat(
                                "[HG ENTITY TRANSFER]: Attached {0} for {1} after {2} ms parallel script/notecard fetch; starting scripts now",
                                defso.Name, defsp.Name, scriptFetchMs);

                            // Start scripts as soon as this attachment is on the avatar.
                            TryStartScriptsOnIncomingAttachment(defso);
                        }

                        if (!defsp.IsDeleted)
                            defsp.GotAttachmentsData = true;

                        if (defsp.IsDeleted)
                        {
                            m_log.DebugFormat(
                                "[HG ENTITY TRANSFER]: HG user {0} left after attaching {1} object(s) (script-first); skipping appearance gather",
                                defsp.Name, attached.Count);
                            return;
                        }

                        // Join the concurrent appearance gather started before the attach loop.
                        // If the avatar left mid-gather, the task self-aborts via shouldAbort.
                        try
                        {
                            appearanceGatherTask?.Wait();
                        }
                        catch (AggregateException)
                        {
                        }

                        if (attached.Count == 0)
                        {
                            m_log.WarnFormat(
                                "[HG ENTITY TRANSFER]: No attachments attached for HG user {0} in {1} (source sent {2})",
                                defsp.Name, m_sceneName, deftatt.Count);
                            return;
                        }

                        if (!defsp.IsDeleted)
                        {
                            m_log.DebugFormat(
                                "[HG ENTITY TRANSFER]: Finished appearance asset gather for {0}: {1} uuid(s) in {2} ms (parallel, two-phase, overlapped with attach)",
                                defsp.Name, ids.Count, appearanceGatherTask is null ? 0 : Util.EnvironmentTickCountSubtract(appearanceTickStart));
                        }

                        // The attachments were shown to viewers while the appearance gather was
                        // still running, so a viewer that requested a not-yet-local texture got ImageNotFound
                        // (grey) and nothing triggers a fetch. All gather assets are now in the local store,
                        // so re-send full updates for each attachment to make every viewer re-request them.
                        if (!defsp.IsDeleted && attached.Count > 0)
                        {
                            int refreshed = 0;
                            foreach (SceneObjectGroup sog in attached)
                            {
                                if (sog.IsDeleted || !sog.IsAttachment)
                                    continue;

                                sog.ForEachPart(part => part.SendFullUpdateToAllClients());
                                ++refreshed;
                            }

                            if (refreshed > 0)
                            {
                                m_log.DebugFormat(
                                    "[HG ENTITY TRANSFER]: Refreshed appearance of {0} attachment(s) for {1} after asset gather in {2}",
                                    refreshed, defsp.Name, m_sceneName);
                            }
                        }
                    }
                    finally
                    {
                        // Flush deferred rebake (or no-op if none pending)
                        if (!defsp.IsDeleted)
                            avFactory?.NotifyAppearanceGatherComplete(defsp.UUID);
                    }
                },
                OwnerID.ToString());

            return true;
        }

        /// <summary>
        /// Collect asset UUIDs an attachment's scripts need CONTENT for at startup: LSL scripts
        /// themselves plus notecards. Notecard bytes are consumed synchronously by scripts at init
        /// (e.g. an AO reading its config), so they must be local before scripts start; animation
        /// and sound assets are NOT collected here because scripts only need the item metadata
        /// (name/UUID, already in the task inventory) to look them up, and their bytes are pulled
        /// by the background appearance gather (script/notecard-first).
        /// </summary>
        private static List<UUID> CollectAttachmentStartupAssetIds(SceneObjectGroup sog)
        {
            List<UUID> startupIds = new List<UUID>();
            if (sog is null || sog.IsDeleted)
                return startupIds;

            SceneObjectPart[] parts = sog.Parts;
            if (parts is null)
                return startupIds;

            HashSet<UUID> seen = new HashSet<UUID>();
            for (int i = 0; i < parts.Length; i++)
            {
                SceneObjectPart part = parts[i];
                if (part is null || part.TaskInventory is null)
                    continue;

                List<TaskInventoryItem> items = part.TaskInventory.GetItems();

                // Only collect notecards for parts that actually run scripts: LSL can only read a
                // notecard (llGetNotecard / llGetInventoryKey) from its own prim's inventory, so a
                // scriptless part's notecards are never consumed and need no early fetch.
                bool hasScript = false;
                for (int j = 0; j < items.Count; j++)
                {
                    TaskInventoryItem item = items[j];
                    if (item is null || item.AssetID.IsZero())
                        continue;

                    // InvType is the reliable script marker; Type is AssetType (LSLText / LSLBytecode).
                    if (item.InvType != (int)InventoryType.LSL
                        && item.Type != (int)AssetType.LSLText
                        && item.Type != (int)AssetType.LSLBytecode)
                        continue;

                    hasScript = true;
                    if (seen.Add(item.AssetID))
                        startupIds.Add(item.AssetID);
                }

                if (!hasScript)
                    continue;

                for (int j = 0; j < items.Count; j++)
                {
                    TaskInventoryItem item = items[j];
                    if (item is null || item.AssetID.IsZero())
                        continue;

                    if (item.InvType != (int)InventoryType.Notecard
                        && item.Type != (int)AssetType.Notecard)
                        continue;

                    if (seen.Add(item.AssetID))
                        startupIds.Add(item.AssetID);
                }
            }

            return startupIds;
        }

        #endregion

        #region IUserAgentVerificationModule

        public bool VerifyClient(AgentCircuitData aCircuit, string token)
        {
            if (aCircuit.ServiceURLs.ContainsKey("HomeURI"))
            {
                string url = aCircuit.ServiceURLs["HomeURI"].ToString();
                IUserAgentService security = new UserAgentServiceConnector(url);
                return security.VerifyClient(aCircuit.SessionID, token);
            }
            else
            {
                m_log.DebugFormat(
                    "[HG ENTITY TRANSFER MODULE]: Agent {0} {1} does not have a HomeURI OH NO!",
                    aCircuit.firstname, aCircuit.lastname);
            }

            return false;
        }

        public override void OnConnectionClosed(IClientAPI obj)
        {
            if (obj.SceneAgent.IsChildAgent)
                return;

            // Let's find out if this is a foreign user or a local user
            IUserManagement uMan = m_scene.RequestModuleInterface<IUserManagement>();
//          UserAccount account = Scene.UserAccountService.GetUserAccount(Scene.RegionInfo.ScopeID, obj.AgentId);

            if (uMan != null && uMan.IsLocalGridUser(obj.AgentId))
            {
                // local grid user
                m_UAS.LogoutAgent(obj.AgentId, obj.SessionId);
                return;
            }

            AgentCircuitData aCircuit = ((Scene)(obj.Scene)).AuthenticateHandler.GetAgentCircuitData(obj.CircuitCode);
            if (aCircuit != null && aCircuit.ServiceURLs != null && aCircuit.ServiceURLs.ContainsKey("HomeURI"))
            {
                string url = aCircuit.ServiceURLs["HomeURI"].ToString();
                IUserAgentService security = new UserAgentServiceConnector(url);
                security.LogoutAgent(obj.AgentId, obj.SessionId);
                //m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Sent logout call to UserAgentService @ {0}", url);
            }
            else
            {
                    m_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: HomeURI not found for agent {0} logout", obj.AgentId);
            }
            base.OnConnectionClosed(obj);
        }

        #endregion

        private GridRegion MakeGateKeeperRegion(string wantedURI)
        {
            if(!Uri.TryCreate(wantedURI, UriKind.Absolute, out Uri uri))
                return null;

            return new GridRegion()
            {
                ExternalHostName = uri.Host,
                HttpPort = (uint)uri.Port,
                ServerURI = wantedURI,  //uri.AbsoluteUri for some reason default ports are needed
                RegionName = string.Empty,
                InternalEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("0.0.0.0"), (int)0),
                RegionFlags = OpenSim.Framework.RegionFlags.Hyperlink
            };
        }
    }
}
