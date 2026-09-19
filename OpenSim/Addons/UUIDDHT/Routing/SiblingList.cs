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

namespace OpenSim.Addons.UUIDDHT
{
    public sealed class SiblingList
    {
        private readonly DhtPeer m_self;
        private readonly int m_s;
        private List<DhtPeer> m_closest = new List<DhtPeer>();

        public SiblingList(DhtPeer self, int s)
        {
            m_self = self;
            m_s = s > 0 ? s : 20;
            m_closest.Add(self.Clone());
        }

        public int Count
        {
            get { return m_closest.Count; }
        }

        public IReadOnlyList<DhtPeer> Snapshot()
        {
            List<DhtPeer> copy = new List<DhtPeer>(m_closest.Count);
            for (int i = 0; i < m_closest.Count; i++)
                copy.Add(m_closest[i].Clone());
            return copy;
        }

        public void Rebuild(IReadOnlyList<DhtPeer> contacts)
        {
            List<DhtPeer> all = new List<DhtPeer>(contacts.Count + 1);
            all.Add(m_self.Clone());
            for (int i = 0; i < contacts.Count; i++)
            {
                if (!contacts[i].NodeId.Equals(m_self.NodeId))
                    all.Add(contacts[i].Clone());
            }
            m_closest = Closest(m_self.NodeId, all, m_s);
        }

        public bool IsResponsible(DhtKey key, IReadOnlyList<DhtPeer> known)
        {
            List<DhtPeer> all = new List<DhtPeer>(known.Count + 1);
            all.Add(m_self.Clone());
            for (int i = 0; i < known.Count; i++)
            {
                if (!known[i].NodeId.Equals(m_self.NodeId))
                    all.Add(known[i]);
            }
            List<DhtPeer> closest = Closest(key, all, m_s);
            for (int i = 0; i < closest.Count; i++)
            {
                if (closest[i].NodeId.Equals(m_self.NodeId))
                    return true;
            }
            return false;
        }

        public static List<DhtPeer> Closest(DhtKey target, IReadOnlyList<DhtPeer> peers, int n)
        {
            List<DhtPeer> list = new List<DhtPeer>(peers.Count);
            for (int i = 0; i < peers.Count; i++)
                list.Add(peers[i]);
            list.Sort((a, b) => a.NodeId.Xor(target).CompareTo(b.NodeId.Xor(target)));
            if (list.Count > n)
                list.RemoveRange(n, list.Count - n);
            return list;
        }
    }
}
