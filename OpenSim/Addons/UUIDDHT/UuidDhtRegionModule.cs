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
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Addons.UUIDDHT
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "UuidDhtRegionModule")]
    public class UuidDhtRegionModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IConfigSource m_Config;
        private bool m_Enabled;
        private bool m_Registered;

        public string Name
        {
            get { return "UuidDhtRegionModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
            m_Config = source;
            m_Enabled = UuidDhtConfig.IsEnabled(source);
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled || m_Registered)
                return;

            m_Registered = true;
            try
            {
                UuidDhtNode node = UuidDhtNode.GetOrCreate(m_Config);
                if (node == null)
                    return;

                MainServer.Instance.AddSimpleStreamHandler(
                    new DhtNodeDocumentHandler(node.Identity, node.Config.HomeURI));
                MainServer.Instance.AddStreamHandler(new DhtOwnsHandler(node));
                MainServer.Instance.AddStreamHandler(new DhtRpcHandler(node));
                IUserAccountService users = scene.RequestModuleInterface<IUserAccountService>();
                if (users != null)
                    node.SetUserAccountService(users);
                node.Start();
                m_log.Info("[UUID-DHT]: /dht/node /dht/owns /dht/rpc registered on standalone HTTP port");
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[UUID-DHT]: failed to start DHT node: {0}", e.Message);
            }
        }
    }
}
