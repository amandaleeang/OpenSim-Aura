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
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using log4net;

namespace OpenSim.Addons.UUIDDHT
{
    public class DhtIdentityException : InvalidOperationException
    {
        public DhtIdentityException(string message) : base(message)
        {
        }
    }

    public sealed class DhtIdentity
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly object m_idLock = new object();
        private readonly string m_path;
        private readonly byte[] m_secretKey;
        private readonly byte[] m_publicKey;
        private long m_nodeSeq;

        private DhtIdentity(string path, byte[] secretKey, byte[] publicKey, DhtKey nodeId, long nodeSeq)
        {
            m_path = path;
            m_secretKey = secretKey;
            m_publicKey = publicKey;
            NodeId = nodeId;
            m_nodeSeq = nodeSeq;
        }

        public byte[] PublicKey
        {
            get
            {
                byte[] copy = new byte[m_publicKey.Length];
                Buffer.BlockCopy(m_publicKey, 0, copy, 0, copy.Length);
                return copy;
            }
        }

        public DhtKey NodeId { get; }

        public long NodeSeq
        {
            get { lock (m_idLock) return m_nodeSeq; }
        }

        public static DhtIdentity LoadOrCreate(string path, IDhtStore store)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("identity path is required", nameof(path));

            if (File.Exists(path))
            {
                DhtIdentity id = Load(path);
                BindOrRejectStore(store, id.NodeId, path);
                return id;
            }

            if (store != null && store.TryGetSelfNodeId(out DhtKey stored))
            {
                throw new DhtIdentityException(
                    "[UUID-DHT]: identity file missing at " + path
                    + " but store self_node_id is " + stored.ToHex()
                    + "; restore identity-{port}.json or delete the store");
            }

            DhtSigner.GenerateKeyPair(out byte[] sk, out byte[] pk);
            DhtKey nodeId = DhtSigner.NodeIdFromPubkey(pk);
            DhtIdentity created = new DhtIdentity(path, sk, pk, nodeId, 1);
            created.Persist();
            if (store != null)
                store.SetSelfNodeId(nodeId);
            m_log.InfoFormat("[UUID-DHT]: created node identity {0} at {1}", nodeId.ToHex(), path);
            return created;
        }

        public byte[] Sign(byte[] canonicalUtf8)
        {
            return DhtSigner.Sign(m_secretKey, canonicalUtf8);
        }

        public static bool Verify(byte[] pubkey, byte[] canonicalUtf8, byte[] sig)
        {
            return DhtSigner.Verify(pubkey, canonicalUtf8, sig);
        }

        public static DhtKey NodeIdFromPubkey(byte[] pubkey)
        {
            return DhtSigner.NodeIdFromPubkey(pubkey);
        }

        public long BumpNodeSeq()
        {
            lock (m_idLock)
            {
                m_nodeSeq++;
                PersistUnlocked();
                return m_nodeSeq;
            }
        }

        private static DhtIdentity Load(string path)
        {
            string json = File.ReadAllText(path);
            FileDto dto = JsonSerializer.Deserialize<FileDto>(json);
            if (dto == null || dto.V != 1 || dto.Alg != "Ed25519")
                throw new DhtIdentityException("[UUID-DHT]: invalid identity file at " + path);

            byte[] sk = Convert.FromBase64String(dto.Priv ?? string.Empty);
            byte[] pk = Convert.FromBase64String(dto.Pub ?? string.Empty);
            if (sk.Length != DhtSigner.SecretKeySize || pk.Length != DhtSigner.PublicKeySize)
                throw new DhtIdentityException("[UUID-DHT]: identity key length invalid at " + path);

            DhtKey nodeId = DhtSigner.NodeIdFromPubkey(pk);
            if (!DhtKey.TryParseHex(dto.NodeId, out DhtKey listed) || listed != nodeId)
                throw new DhtIdentityException("[UUID-DHT]: identity nodeId does not match SHA256(pubkey) at " + path);

            long seq = dto.NodeSeq < 1 ? 1 : dto.NodeSeq;
            return new DhtIdentity(path, sk, pk, nodeId, seq);
        }

        private static void BindOrRejectStore(IDhtStore store, DhtKey self, string path)
        {
            if (store == null)
                return;
            if (!store.TryGetSelfNodeId(out DhtKey stored))
            {
                store.SetSelfNodeId(self);
                return;
            }
            if (stored != self)
            {
                throw new DhtIdentityException(
                    "[UUID-DHT]: store self_node_id is " + stored.ToHex()
                    + " but identity " + path + " is " + self.ToHex()
                    + "; restore identity-{port}.json or delete the store");
            }
        }

        private void Persist()
        {
            lock (m_idLock)
                PersistUnlocked();
        }

        private void PersistUnlocked()
        {
            string dir = Path.GetDirectoryName(m_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            FileDto dto = new FileDto
            {
                V = 1,
                Alg = "Ed25519",
                Priv = Convert.ToBase64String(m_secretKey),
                Pub = Convert.ToBase64String(m_publicKey),
                NodeId = NodeId.ToHex(),
                NodeSeq = m_nodeSeq
            };
            string json = JsonSerializer.Serialize(dto);
            File.WriteAllText(m_path, json);
            TryUnixPrivateMode(m_path);
        }

        private static void TryUnixPrivateMode(string path)
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                m_log.Info("[UUID-DHT]: identity file contains the node private key; restrict NTFS ACLs on Windows");
                return;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private sealed class FileDto
        {
            [JsonPropertyName("v")]
            public int V { get; set; }
            [JsonPropertyName("alg")]
            public string Alg { get; set; }
            [JsonPropertyName("priv")]
            public string Priv { get; set; }
            [JsonPropertyName("pub")]
            public string Pub { get; set; }
            [JsonPropertyName("nodeId")]
            public string NodeId { get; set; }
            [JsonPropertyName("nodeSeq")]
            public long NodeSeq { get; set; }
        }
    }
}
