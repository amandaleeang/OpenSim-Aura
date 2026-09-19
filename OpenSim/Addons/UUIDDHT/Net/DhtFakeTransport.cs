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
using System.Threading;
using OpenMetaverse;

namespace OpenSim.Addons.UUIDDHT
{
    public sealed class DhtFakeTransport : IDhtTransport, IDhtStoreValidator
    {
        private readonly object m_lock = new object();
        private readonly Dictionary<string, UuidDhtNode> m_nodes = new Dictionary<string, UuidDhtNode>(StringComparer.Ordinal);
        private int m_rpcCount;
        private int m_getNodeCount;

        public bool AllowAutoRedirect
        {
            get { return false; }
        }

        public int RpcCount
        {
            get { lock (m_lock) return m_rpcCount; }
        }

        public int GetNodeCount
        {
            get { lock (m_lock) return m_getNodeCount; }
        }

        public void Add(string homeURI, UuidDhtNode node)
        {
            lock (m_lock)
                m_nodes[homeURI] = node;
        }

        public void Remove(string homeURI)
        {
            lock (m_lock)
                m_nodes.Remove(homeURI);
        }

        public DhtNodeDocument GetNode(string homeURI, CancellationToken ct)
        {
            UuidDhtNode node;
            lock (m_lock)
            {
                m_getNodeCount++;
                if (!m_nodes.TryGetValue(homeURI, out node))
                    throw new InvalidOperationException("no fake node for " + homeURI);
            }
            return node.CreateNodeDocument();
        }

        public DhtOwnsDocument GetOwns(string homeURI, UUID uuid, CancellationToken ct)
        {
            UuidDhtNode node;
            lock (m_lock)
            {
                if (!m_nodes.TryGetValue(homeURI, out node))
                    throw new InvalidOperationException("no fake node for " + homeURI);
            }
            return node.CreateOwnsDocument(uuid);
        }

        public bool ProofMatches(string homeURI, DhtKey expectedNodeId)
        {
            try
            {
                DhtNodeDocument doc = GetNode(homeURI, CancellationToken.None);
                if (doc == null || !DhtKey.TryParseHex(doc.NodeId, out DhtKey id) || !id.Equals(expectedNodeId))
                    return false;
                return doc.HomeURI == homeURI;
            }
            catch
            {
                return false;
            }
        }

        public bool OwnsUuid(string homeURI, UUID uuid, DhtKey expectedNodeId)
        {
            try
            {
                DhtOwnsDocument doc = GetOwns(homeURI, uuid, CancellationToken.None);
                DhtNodeDocument node = GetNode(homeURI, CancellationToken.None);
                return DhtHttpClient.VerifyOwnsDocument(doc, homeURI, uuid, expectedNodeId, node);
            }
            catch
            {
                return false;
            }
        }

        public DhtRpcResponse Rpc(string homeURI, DhtRpcRequest request, CancellationToken ct)
        {
            UuidDhtNode node;
            lock (m_lock)
            {
                m_rpcCount++;
                if (!m_nodes.TryGetValue(homeURI, out node))
                    throw new InvalidOperationException("no fake node for " + homeURI);
            }
            return node.HandleRpc(request);
        }

        public void Dispose()
        {
        }
    }
}
