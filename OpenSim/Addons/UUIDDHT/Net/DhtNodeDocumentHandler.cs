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
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Addons.UUIDDHT
{
    public sealed class DhtNodeDocumentHandler : SimpleStreamHandler
    {
        private readonly DhtIdentity m_identity;
        private readonly string m_homeURI;

        public DhtNodeDocumentHandler(DhtIdentity identity, string homeURI)
            : base("/dht/node")
        {
            m_identity = identity ?? throw new ArgumentNullException(nameof(identity));
            m_homeURI = homeURI ?? string.Empty;
        }

        protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            string method = httpRequest.HttpMethod;
            if (method == "HEAD")
            {
                httpResponse.AddHeader("X-Dht-NodeId", m_identity.NodeId.ToHex());
                httpResponse.StatusCode = (int)HttpStatusCode.OK;
                return;
            }

            if (method != "GET")
            {
                httpResponse.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string nodeId = m_identity.NodeId.ToHex();
            string pub = DhtSigner.ToHex(m_identity.PublicKey);
            byte[] sig = m_identity.Sign(DhtCanonical.Utf8(DhtCanonical.Proof(nodeId, m_homeURI, pub, ts)));

            DhtNodeDocument doc = new DhtNodeDocument
            {
                V = 1,
                NodeId = nodeId,
                HomeURI = m_homeURI,
                Pubkey = pub,
                Ts = ts,
                Seq = m_identity.NodeSeq,
                Sig = DhtSigner.ToHex(sig)
            };

            byte[] body = JsonSerializer.SerializeToUtf8Bytes(doc, DhtJson.WireOptions);
            httpResponse.ContentType = "application/json; charset=utf-8";
            httpResponse.RawBuffer = body;
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
        }
    }

    public sealed class DhtNodeDocument
    {
        [JsonPropertyOrder(0)]
        public int V { get; set; }
        [JsonPropertyOrder(1)]
        public string NodeId { get; set; }
        [JsonPropertyOrder(2)]
        public string HomeURI { get; set; }
        [JsonPropertyOrder(3)]
        public string Pubkey { get; set; }
        [JsonPropertyOrder(4)]
        public long Ts { get; set; }
        [JsonPropertyOrder(5)]
        public long Seq { get; set; }
        [JsonPropertyOrder(6)]
        public string Sig { get; set; }
    }
}
