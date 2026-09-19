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

namespace OpenSim.Addons.UUIDDHT
{
    /// <summary>
    /// Least-recently-seen at the head, most-recently-seen at the tail.
    /// Caller holds RoutingTable's lock.
    /// </summary>
    internal sealed class KBucket
    {
        private readonly int m_k;
        private readonly List<DhtPeer> m_peers = new List<DhtPeer>();

        public KBucket(int k)
        {
            m_k = k > 0 ? k : 20;
        }

        public int Count
        {
            get { return m_peers.Count; }
        }

        public DhtPeer Head
        {
            get { return m_peers.Count == 0 ? null : m_peers[0]; }
        }

        public DhtPeer Find(DhtKey id)
        {
            for (int i = 0; i < m_peers.Count; i++)
            {
                if (m_peers[i].NodeId.Equals(id))
                    return m_peers[i];
            }
            return null;
        }

        public void Touch(DhtPeer existing, DhtPeer update)
        {
            m_peers.Remove(existing);
            existing.HomeURI = update.HomeURI ?? existing.HomeURI;
            existing.LastSeen = update.LastSeen;
            existing.FailCount = 0;
            m_peers.Add(existing);
        }

        public bool TryAdd(DhtPeer peer)
        {
            if (m_peers.Count >= m_k)
                return false;
            m_peers.Add(peer);
            return true;
        }

        public bool Remove(DhtKey id)
        {
            for (int i = 0; i < m_peers.Count; i++)
            {
                if (m_peers[i].NodeId.Equals(id))
                {
                    m_peers.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public void CopyTo(List<DhtPeer> dest)
        {
            for (int i = 0; i < m_peers.Count; i++)
                dest.Add(m_peers[i].Clone());
        }
    }
}
