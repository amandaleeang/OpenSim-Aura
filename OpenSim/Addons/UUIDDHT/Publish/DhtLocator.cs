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
using System.Net.Sockets;
using OpenSim.Framework;

namespace OpenSim.Addons.UUIDDHT
{
    public static class DhtLocator
    {
        public static bool TryNormalize(string raw, bool allowPrivate, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri uri))
                return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                return false;

            string path = uri.AbsolutePath;
            if (path != "/" && path != string.Empty)
                return false;

            string host = uri.IdnHost;
            if (string.IsNullOrEmpty(host))
                return false;
            host = host.ToLowerInvariant();
            if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal))
                return false;

            if (IPAddress.TryParse(host, out IPAddress ip))
            {
                if (RejectAddress(ip, allowPrivate))
                    return false;
            }
            else
            {
                IPAddress resolved = Util.GetHostFromDNS(host);
                if (resolved != null && RejectAddress(resolved, allowPrivate))
                    return false;
            }

            int port = uri.Port;
            bool dropPort = (uri.Scheme == Uri.UriSchemeHttp && port == 80)
                || (uri.Scheme == Uri.UriSchemeHttps && port == 443);

            string hostPart = host.Contains(':') ? "[" + host + "]" : host;
            normalized = uri.Scheme + "://" + hostPart;
            if (!dropPort && port > 0)
                normalized += ":" + port;
            normalized += "/";
            return true;
        }

        public static bool SameHome(string a, string b, bool allowPrivate)
        {
            if (!TryNormalize(a, allowPrivate, out string na))
                return false;
            if (!TryNormalize(b, allowPrivate, out string nb))
                return false;
            return string.Equals(na, nb, StringComparison.Ordinal);
        }

        public static bool RejectAddress(IPAddress ip, bool allowPrivate)
        {
            if (ip == null)
                return true;
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
                || ip.Equals(IPAddress.None) || ip.Equals(IPAddress.Broadcast))
                return true;
            if (IsLinkLocal(ip) || IsMulticast(ip) || IsCgnat(ip))
                return true;
            if (!allowPrivate && IsPrivate(ip))
                return true;
            return false;
        }

        private static bool IsLinkLocal(IPAddress ip)
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return ip.IsIPv6LinkLocal;

            byte[] b = ip.GetAddressBytes();
            return b[0] == 169 && b[1] == 254;
        }

        private static bool IsMulticast(IPAddress ip)
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return ip.IsIPv6Multicast;
            byte[] b = ip.GetAddressBytes();
            return b[0] >= 224 && b[0] <= 239;
        }

        private static bool IsCgnat(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
                return false;
            byte[] b = ip.GetAddressBytes();
            return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
        }

        private static bool IsPrivate(IPAddress ip)
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return ip.IsIPv6UniqueLocal;

            byte[] b = ip.GetAddressBytes();
            if (b[0] == 10)
                return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                return true;
            if (b[0] == 192 && b[1] == 168)
                return true;
            return false;
        }
    }
}
