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
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Addons.UUIDDHT
{
    public sealed class DhtRpcHandler : BaseStreamHandler
    {
        private readonly UuidDhtNode m_node;
        private readonly int m_maxBytes;

        public DhtRpcHandler(UuidDhtNode node)
            : base("POST", "/dht/rpc")
        {
            m_node = node ?? throw new ArgumentNullException(nameof(node));
            m_maxBytes = node.Config.MaxRpcBytes > 0 ? node.Config.MaxRpcBytes : 32768;
        }

        protected override byte[] ProcessRequest(
            string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            IPAddress ip = httpRequest != null && httpRequest.RemoteIPEndPoint != null
                ? httpRequest.RemoteIPEndPoint.Address : null;
            if (!m_node.TryRateLimit(ip))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.TooManyRequests;
                return Array.Empty<byte>();
            }

            byte[] raw = ReadLimited(request, m_maxBytes, out bool tooBig);
            if (tooBig)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                return Array.Empty<byte>();
            }

            DhtRpcRequest req;
            try
            {
                req = JsonSerializer.Deserialize<DhtRpcRequest>(raw, DhtJson.WireOptions);
            }
            catch (JsonException)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                return Array.Empty<byte>();
            }

            DhtRpcResponse resp = m_node.HandleRpc(req);
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(resp, DhtJson.WireOptions);
            if (body.Length > m_maxBytes * 2)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.InternalServerError;
                return Array.Empty<byte>();
            }
            httpResponse.ContentType = "application/json; charset=utf-8";
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            return body;
        }

        public static byte[] ReadLimited(Stream request, int max, out bool tooBig)
        {
            tooBig = false;
            if (request == null)
                return Array.Empty<byte>();
            using MemoryStream ms = new MemoryStream();
            byte[] buf = new byte[4096];
            int n;
            while ((n = request.Read(buf, 0, buf.Length)) > 0)
            {
                if (ms.Length + n > max)
                {
                    tooBig = true;
                    return Array.Empty<byte>();
                }
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }
    }
}
