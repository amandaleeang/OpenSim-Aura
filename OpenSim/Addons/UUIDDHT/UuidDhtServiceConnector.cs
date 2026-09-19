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
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Addons.UUIDDHT
{
    public class UuidDhtServiceConnector : ServiceConnector
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public UuidDhtServiceConnector(IConfigSource config, IHttpServer server, string configName)
            : base(config, server, configName)
        {
            UuidDhtConfig cfg = UuidDhtConfig.From(config);
            if (!cfg.Enabled)
                return;

            try
            {
                UuidDhtNode node = UuidDhtNode.GetOrCreate(config);
                if (node == null)
                    return;

                server.AddSimpleStreamHandler(new DhtNodeDocumentHandler(node.Identity, node.Config.HomeURI));
                server.AddStreamHandler(new DhtOwnsHandler(node));
                server.AddStreamHandler(new DhtRpcHandler(node));
                WireUserAccounts(config, node);
                node.Start();
                m_log.Info("[UUID-DHT]: /dht/node /dht/owns /dht/rpc registered on Robust public port");
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[UUID-DHT]: failed to start DHT node: {0}", e.Message);
            }
        }

        private static void WireUserAccounts(IConfigSource config, UuidDhtNode node)
        {
            IConfig ua = config.Configs["UserAccountService"];
            if (ua == null)
                return;
            string dll = ua.GetString("LocalServiceModule", string.Empty);
            if (dll.Length == 0)
                return;
            try
            {
                IUserAccountService users = ServerUtils.LoadPlugin<IUserAccountService>(dll, new object[] { config });
                if (users != null)
                    node.SetUserAccountService(users);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[UUID-DHT]: UserAccountService plugin not loaded for owns: {0}", e.Message);
            }
        }
    }
}
