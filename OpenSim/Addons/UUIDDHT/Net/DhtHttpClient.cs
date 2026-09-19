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
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;

namespace OpenSim.Addons.UUIDDHT
{
    public sealed class DhtHttpClient : IDhtTransport, IDhtStoreValidator, IDisposable
    {
        private readonly SocketsHttpHandler m_handler;
        private readonly HttpClient m_http;
        private readonly bool m_allowPrivate;
        private readonly int m_timeoutMs;

        public DhtHttpClient(bool allowPrivate, int rpcTimeoutMs)
        {
            m_allowPrivate = allowPrivate;
            m_timeoutMs = rpcTimeoutMs > 0 ? rpcTimeoutMs : UuidDhtConfig.DefaultRpcTimeoutMs;
            m_handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromMilliseconds(m_timeoutMs),
                ConnectCallback = ConnectAsync
            };
            m_http = new HttpClient(m_handler, true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public bool AllowAutoRedirect
        {
            get { return m_handler.AllowAutoRedirect; }
        }

        public DhtNodeDocument GetNode(string homeURI, CancellationToken ct)
        {
            if (!DhtLocator.TryNormalize(homeURI, m_allowPrivate, out string home))
                throw new HttpRequestException("bad locator");
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(m_timeoutMs);
            HttpResponseMessage resp = m_http.GetAsync(home + "dht/node", linked.Token).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            DhtNodeDocument doc = JsonSerializer.Deserialize<DhtNodeDocument>(json, DhtJson.WireOptions);
            if (doc == null || !VerifyNodeDocument(doc, home))
                throw new HttpRequestException("bad proof");
            return doc;
        }

        public DhtOwnsDocument GetOwns(string homeURI, UUID uuid, CancellationToken ct)
        {
            if (!DhtLocator.TryNormalize(homeURI, m_allowPrivate, out string home))
                throw new HttpRequestException("bad locator");
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(m_timeoutMs);
            HttpResponseMessage resp = m_http.GetAsync(home + "dht/owns/" + uuid, linked.Token).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            DhtOwnsDocument doc = JsonSerializer.Deserialize<DhtOwnsDocument>(json, DhtJson.WireOptions);
            if (doc == null)
                throw new HttpRequestException("bad owns");
            return doc;
        }

        public DhtRpcResponse Rpc(string homeURI, DhtRpcRequest request, CancellationToken ct)
        {
            if (!DhtLocator.TryNormalize(homeURI, m_allowPrivate, out string home))
                throw new HttpRequestException("bad locator");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request, DhtJson.WireOptions);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(m_timeoutMs);
            using HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Post, home + "dht/rpc");
            msg.Content = new ByteArrayContent(bytes);
            msg.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
            HttpResponseMessage resp = m_http.SendAsync(msg, linked.Token).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            DhtRpcResponse parsed = JsonSerializer.Deserialize<DhtRpcResponse>(json, DhtJson.WireOptions);
            if (parsed == null || !VerifyResponse(parsed))
                throw new HttpRequestException("bad rpc response");
            return parsed;
        }

        public bool ProofMatches(string homeURI, DhtKey expectedNodeId)
        {
            try
            {
                DhtNodeDocument doc = GetNode(homeURI, CancellationToken.None);
                return DhtKey.TryParseHex(doc.NodeId, out DhtKey id) && id.Equals(expectedNodeId);
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
                if (!DhtLocator.TryNormalize(homeURI, m_allowPrivate, out string home))
                    return false;
                return VerifyOwnsDocument(doc, home, uuid, expectedNodeId, node);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            m_http.Dispose();
        }

        private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
        {
            DnsEndPoint dns = ctx.DnsEndPoint;
            IPAddress[] addrs = await Dns.GetHostAddressesAsync(dns.Host, ct).ConfigureAwait(false);
            IPAddress chosen = null;
            for (int i = 0; i < addrs.Length; i++)
            {
                if (!DhtLocator.RejectAddress(addrs[i], m_allowPrivate))
                {
                    chosen = addrs[i];
                    break;
                }
            }
            if (chosen == null)
                throw new HttpRequestException("blocked address");

            Socket sock = new Socket(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await sock.ConnectAsync(new IPEndPoint(chosen, dns.Port), ct).ConfigureAwait(false);
                if (sock.RemoteEndPoint is IPEndPoint ep && DhtLocator.RejectAddress(ep.Address, m_allowPrivate))
                    throw new HttpRequestException("blocked address");
                return new NetworkStream(sock, true);
            }
            catch
            {
                sock.Dispose();
                throw;
            }
        }

        public static bool VerifyNodeDocument(DhtNodeDocument doc, string expectedHome)
        {
            if (doc == null || doc.V != 1)
                return false;
            byte[] pub;
            byte[] sig;
            try
            {
                pub = Convert.FromHexString(doc.Pubkey ?? string.Empty);
                sig = Convert.FromHexString(doc.Sig ?? string.Empty);
            }
            catch (FormatException)
            {
                return false;
            }
            if (DhtSigner.NodeIdFromPubkey(pub).ToHex() != doc.NodeId)
                return false;
            if (!string.IsNullOrEmpty(expectedHome) && doc.HomeURI != expectedHome)
                return false;
            string canon = DhtCanonical.Proof(doc.NodeId, doc.HomeURI, doc.Pubkey, doc.Ts);
            return DhtIdentity.Verify(pub, DhtCanonical.Utf8(canon), sig);
        }

        public static bool VerifyOwnsDocument(DhtOwnsDocument doc, string expectedHome, UUID uuid,
            DhtKey expectedNodeId, DhtNodeDocument node)
        {
            if (doc == null || doc.V != 1 || !doc.Has || node == null)
                return false;
            if (doc.NodeId != expectedNodeId.ToHex() || node.NodeId != doc.NodeId)
                return false;
            if (!string.Equals(doc.Uuid, uuid.ToString(), StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(expectedHome) && doc.HomeURI != expectedHome)
                return false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (doc.Ts < now - 300 || doc.Ts > now + 300)
                return false;
            byte[] pub;
            byte[] sig;
            try
            {
                pub = Convert.FromHexString(node.Pubkey ?? string.Empty);
                sig = Convert.FromHexString(doc.Sig ?? string.Empty);
            }
            catch (FormatException)
            {
                return false;
            }
            if (DhtSigner.NodeIdFromPubkey(pub).ToHex() != doc.NodeId)
                return false;
            string canon = DhtCanonical.Owns(doc.Uuid, doc.NodeId, doc.HomeURI, doc.Has, doc.Ts);
            return DhtIdentity.Verify(pub, DhtCanonical.Utf8(canon), sig);
        }

        public static bool VerifyResponse(DhtRpcResponse resp)
        {
            if (resp == null || resp.V != 1)
                return false;
            byte[] pub;
            byte[] sig;
            try
            {
                pub = Convert.FromHexString(resp.Pubkey ?? string.Empty);
                sig = Convert.FromHexString(resp.Sig ?? string.Empty);
            }
            catch (FormatException)
            {
                return false;
            }
            if (DhtSigner.NodeIdFromPubkey(pub).ToHex() != resp.Sender)
                return false;
            DhtRpcResponseBody payload = new DhtRpcResponseBody { Peers = resp.Peers, Value = resp.Value };
            string hash = DhtJson.BodyHash(payload);
            string canon = DhtCanonical.RpcResponse(resp.Op, resp.Sender, resp.Nonce ?? string.Empty,
                resp.Ts, resp.Ok, resp.Error, hash);
            return DhtIdentity.Verify(pub, DhtCanonical.Utf8(canon), sig);
        }
    }
}
