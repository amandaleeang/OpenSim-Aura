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

using System.Text;

namespace OpenSim.Addons.UUIDDHT
{
    public static class DhtCanonical
    {
        public static byte[] Utf8(string s)
        {
            return Encoding.UTF8.GetBytes(s ?? string.Empty);
        }

        public static string Proof(string nodeIdHex, string homeURI, string pubkeyHex, long ts)
        {
            return "proof-v1|" + nodeIdHex + "|" + homeURI + "|" + pubkeyHex + "|" + ts;
        }

        public static string NodeRecord(string nodeIdHex, string homeURI, string pubkeyHex, long seq)
        {
            return "node-v1|" + nodeIdHex + "|" + homeURI + "|" + pubkeyHex + "|" + seq;
        }

        public static string UuidRecord(string uuid, string nodeIdHex, long seq, bool tombstone)
        {
            return "uuid-v1|" + uuid + "|" + nodeIdHex + "|" + seq + "|" + (tombstone ? "1" : "0");
        }

        public static string LocRecord(string homeHashHex, string nodeIdHex, string homeURI, long seq, bool tombstone)
        {
            return "loc-v1|" + homeHashHex + "|" + nodeIdHex + "|" + homeURI + "|" + seq + "|" + (tombstone ? "1" : "0");
        }

        public static string Owns(string uuid, string nodeIdHex, string homeURI, bool has, long ts)
        {
            return "owns-v1|" + uuid + "|" + nodeIdHex + "|" + homeURI + "|" + (has ? "1" : "0") + "|" + ts;
        }

        public static string RpcRequest(string op, string sender, string senderHome, string nonce, long ts, string bodyHash)
        {
            return "rpc-v1|" + op + "|" + sender + "|" + senderHome + "|" + nonce + "|" + ts + "|" + bodyHash;
        }

        public static string RpcResponse(string op, string sender, string nonce, long ts, bool ok, string error, string bodyHash)
        {
            return "rpc-resp-v1|" + op + "|" + sender + "|" + nonce + "|" + ts + "|" + (ok ? "1" : "0") + "|" + (error ?? string.Empty) + "|" + bodyHash;
        }
    }
}
