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
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace OpenSim.Addons.UUIDDHT
{
    public sealed class DhtRpcBody
    {
        [JsonPropertyOrder(0)]
        public string Key { get; set; }
        [JsonPropertyOrder(1)]
        public DhtRecord Record { get; set; }
        [JsonPropertyOrder(2)]
        public DhtRecord Node { get; set; }
    }

    public sealed class DhtPeerWire
    {
        [JsonPropertyOrder(0)]
        public string NodeId { get; set; }
        [JsonPropertyOrder(1)]
        public string HomeURI { get; set; }
    }

    public sealed class DhtRpcResponseBody
    {
        [JsonPropertyOrder(0)]
        public List<DhtPeerWire> Peers { get; set; }
        [JsonPropertyOrder(1)]
        public DhtRecord Value { get; set; }
    }

    public sealed class DhtRpcRequest
    {
        [JsonPropertyOrder(0)]
        public int V { get; set; }
        [JsonPropertyOrder(1)]
        public string Op { get; set; }
        [JsonPropertyOrder(2)]
        public string Sender { get; set; }
        [JsonPropertyOrder(3)]
        public string SenderHome { get; set; }
        [JsonPropertyOrder(4)]
        public string Pubkey { get; set; }
        [JsonPropertyOrder(5)]
        public string Nonce { get; set; }
        [JsonPropertyOrder(6)]
        public long Ts { get; set; }
        [JsonPropertyOrder(7)]
        public string Sig { get; set; }
        [JsonPropertyOrder(8)]
        public DhtRpcBody Body { get; set; }

        public static DhtRpcRequest Create(DhtIdentity id, string homeURI, string op, DhtRpcBody body)
        {
            DhtRpcBody b = body ?? new DhtRpcBody();
            string hash = DhtJson.BodyHash(b);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            string sender = id.NodeId.ToHex();
            string pub = DhtSigner.ToHex(id.PublicKey);
            DhtRpcRequest req = new DhtRpcRequest
            {
                V = 1,
                Op = op,
                Sender = sender,
                SenderHome = homeURI,
                Pubkey = pub,
                Nonce = nonce,
                Ts = ts,
                Body = b
            };
            req.Sig = DhtSigner.ToHex(id.Sign(DhtCanonical.Utf8(
                DhtCanonical.RpcRequest(op, sender, homeURI, nonce, ts, hash))));
            return req;
        }
    }

    public sealed class DhtRpcResponse
    {
        [JsonPropertyOrder(0)]
        public int V { get; set; }
        [JsonPropertyOrder(1)]
        public string Op { get; set; }
        [JsonPropertyOrder(2)]
        public bool Ok { get; set; }
        [JsonPropertyOrder(3)]
        public string Error { get; set; }
        [JsonPropertyOrder(4)]
        public string Sender { get; set; }
        [JsonPropertyOrder(5)]
        public string SenderHome { get; set; }
        [JsonPropertyOrder(6)]
        public string Pubkey { get; set; }
        [JsonPropertyOrder(7)]
        public string Nonce { get; set; }
        [JsonPropertyOrder(8)]
        public long Ts { get; set; }
        [JsonPropertyOrder(9)]
        public string Sig { get; set; }
        [JsonPropertyOrder(10)]
        public List<DhtPeerWire> Peers { get; set; }
        [JsonPropertyOrder(11)]
        public DhtRecord Value { get; set; }

        public static DhtRpcResponse Create(DhtIdentity id, string homeURI, string op, string nonce,
            bool ok, string error, List<DhtPeerWire> peers, DhtRecord value)
        {
            DhtRpcResponseBody payload = new DhtRpcResponseBody { Peers = peers, Value = value };
            string hash = DhtJson.BodyHash(payload);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string sender = id.NodeId.ToHex();
            string pub = DhtSigner.ToHex(id.PublicKey);
            DhtRpcResponse resp = new DhtRpcResponse
            {
                V = 1,
                Op = op,
                Ok = ok,
                Error = error,
                Sender = sender,
                SenderHome = homeURI,
                Pubkey = pub,
                Nonce = nonce,
                Ts = ts,
                Peers = peers,
                Value = value
            };
            resp.Sig = DhtSigner.ToHex(id.Sign(DhtCanonical.Utf8(
                DhtCanonical.RpcResponse(op, sender, nonce ?? string.Empty, ts, ok, error, hash))));
            return resp;
        }
    }

    public sealed class DhtOwnsDocument
    {
        [JsonPropertyOrder(0)]
        public int V { get; set; }
        [JsonPropertyOrder(1)]
        public string Uuid { get; set; }
        [JsonPropertyOrder(2)]
        public string NodeId { get; set; }
        [JsonPropertyOrder(3)]
        public string HomeURI { get; set; }
        [JsonPropertyOrder(4)]
        public bool Has { get; set; }
        [JsonPropertyOrder(5)]
        public long Ts { get; set; }
        [JsonPropertyOrder(6)]
        public string Sig { get; set; }
    }
}
