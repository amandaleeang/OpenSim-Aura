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
using System.Text.Json.Serialization;
using OpenMetaverse;

namespace OpenSim.Addons.UUIDDHT
{
    public enum DhtRecordKind
    {
        Uuid,
        Node,
        Loc
    }

    public sealed class DhtRecord
    {
        [JsonPropertyOrder(0)]
        public string Kind { get; set; }
        [JsonPropertyOrder(1)]
        public string Key { get; set; }
        [JsonPropertyOrder(2)]
        public string DhtKey { get; set; }
        [JsonPropertyOrder(3)]
        public string NodeId { get; set; }
        [JsonPropertyOrder(4)]
        public string HomeURI { get; set; }
        [JsonPropertyOrder(5)]
        public string Pubkey { get; set; }
        [JsonPropertyOrder(6)]
        public string Uuid { get; set; }
        [JsonPropertyOrder(7)]
        public long Seq { get; set; }
        [JsonPropertyOrder(8)]
        public bool Tombstone { get; set; }
        [JsonPropertyOrder(9)]
        public string Sig { get; set; }

        [JsonIgnore]
        public DhtRecordKind KindEnum
        {
            get
            {
                if (Kind == "node")
                    return DhtRecordKind.Node;
                if (Kind == "loc")
                    return DhtRecordKind.Loc;
                return DhtRecordKind.Uuid;
            }
        }

        public static DhtRecord CreateUuid(UUID uuid, DhtKey nodeId, long seq, bool tombstone)
        {
            DhtKey dht = OpenSim.Addons.UUIDDHT.DhtKey.ForUuid(uuid);
            return new DhtRecord
            {
                Kind = "uuid",
                Key = "uuid:" + uuid.ToString(),
                DhtKey = dht.ToHex(),
                NodeId = nodeId.ToHex(),
                Uuid = uuid.ToString(),
                Seq = seq,
                Tombstone = tombstone
            };
        }

        public static DhtRecord CreateNode(DhtKey nodeId, string homeURI, byte[] pubkey, long seq)
        {
            string pubHex = DhtSigner.ToHex(pubkey);
            return new DhtRecord
            {
                Kind = "node",
                Key = "node:" + nodeId.ToHex(),
                DhtKey = nodeId.ToHex(),
                NodeId = nodeId.ToHex(),
                HomeURI = homeURI,
                Pubkey = pubHex,
                Seq = seq,
                Tombstone = false
            };
        }

        public static DhtRecord CreateLoc(string normalizedHomeURI, DhtKey nodeId, long seq, bool tombstone)
        {
            DhtKey dht = OpenSim.Addons.UUIDDHT.DhtKey.ForLoc(normalizedHomeURI);
            return new DhtRecord
            {
                Kind = "loc",
                Key = "loc:" + dht.ToHex(),
                DhtKey = dht.ToHex(),
                NodeId = nodeId.ToHex(),
                HomeURI = normalizedHomeURI,
                Seq = seq,
                Tombstone = tombstone
            };
        }

        public string Canonical()
        {
            switch (KindEnum)
            {
                case DhtRecordKind.Node:
                    return DhtCanonical.NodeRecord(NodeId, HomeURI, Pubkey, Seq);
                case DhtRecordKind.Loc:
                    return DhtCanonical.LocRecord(DhtKey, NodeId, HomeURI, Seq, Tombstone);
                default:
                    return DhtCanonical.UuidRecord(Uuid, NodeId, Seq, Tombstone);
            }
        }

        public void SignWith(DhtIdentity identity)
        {
            byte[] sig = identity.Sign(DhtCanonical.Utf8(Canonical()));
            Sig = DhtSigner.ToHex(sig);
        }

        public bool Verify(byte[] ownerPubkey)
        {
            if (ownerPubkey == null || string.IsNullOrEmpty(Sig))
                return false;
            if (DhtSigner.NodeIdFromPubkey(ownerPubkey).ToHex() != NodeId)
                return false;
            try
            {
                byte[] sig = Convert.FromHexString(Sig);
                return DhtIdentity.Verify(ownerPubkey, DhtCanonical.Utf8(Canonical()), sig);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public bool TryParseDhtKey(out DhtKey key)
        {
            return OpenSim.Addons.UUIDDHT.DhtKey.TryParseHex(DhtKey, out key);
        }

        public bool TryParseNodeId(out DhtKey key)
        {
            return OpenSim.Addons.UUIDDHT.DhtKey.TryParseHex(NodeId, out key);
        }

        public bool MatchesComputedDhtKey()
        {
            if (!TryParseDhtKey(out DhtKey have))
                return false;
            switch (KindEnum)
            {
                case DhtRecordKind.Node:
                    return TryParseNodeId(out DhtKey nid) && have.Equals(nid);
                case DhtRecordKind.Loc:
                    return have.Equals(OpenSim.Addons.UUIDDHT.DhtKey.ForLoc(HomeURI ?? string.Empty));
                default:
                    if (!UUID.TryParse(Uuid, out UUID u))
                        return false;
                    return have.Equals(OpenSim.Addons.UUIDDHT.DhtKey.ForUuid(u));
            }
        }
    }
}
