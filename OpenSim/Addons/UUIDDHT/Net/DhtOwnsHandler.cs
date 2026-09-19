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
using System.IO;
using System.Net;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Addons.UUIDDHT
{
    public sealed class DhtOwnsHandler : BaseStreamHandler
    {
        private readonly UuidDhtNode m_node;

        public DhtOwnsHandler(UuidDhtNode node)
            : base("GET", "/dht/owns")
        {
            m_node = node ?? throw new ArgumentNullException(nameof(node));
        }

        protected override byte[] ProcessRequest(
            string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            string param = GetParam(path);
            if (param.StartsWith("/"))
                param = param.Substring(1);
            if (!UUID.TryParse(param, out UUID uuid) || uuid.IsZero())
            {
                httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                return Array.Empty<byte>();
            }

            DhtOwnsDocument doc = m_node.CreateOwnsDocument(uuid);
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(doc, DhtJson.WireOptions);
            httpResponse.ContentType = "application/json; charset=utf-8";
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            return body;
        }
    }
}
