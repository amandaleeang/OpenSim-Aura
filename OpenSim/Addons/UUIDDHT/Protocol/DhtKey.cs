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
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using OpenMetaverse;

namespace OpenSim.Addons.UUIDDHT
{
    public readonly struct DhtKey : IEquatable<DhtKey>, IComparable<DhtKey>
    {
        public const int Size = 32;

        private readonly byte[] m_bytes;

        private DhtKey(byte[] bytes)
        {
            m_bytes = bytes;
        }

        public static DhtKey FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length != Size)
                throw new ArgumentException("DhtKey must be 32 bytes", nameof(bytes));
            byte[] copy = new byte[Size];
            Buffer.BlockCopy(bytes, 0, copy, 0, Size);
            return new DhtKey(copy);
        }

        public static DhtKey ParseHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length != Size * 2)
                throw new ArgumentException("DhtKey hex must be 64 chars", nameof(hex));
            return FromBytes(Convert.FromHexString(hex));
        }

        public static bool TryParseHex(string hex, out DhtKey key)
        {
            key = default;
            if (string.IsNullOrEmpty(hex) || hex.Length != Size * 2)
                return false;
            try
            {
                key = FromBytes(Convert.FromHexString(hex));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public static DhtKey Sha256(byte[] data)
        {
            return FromBytes(SHA256.HashData(data ?? Array.Empty<byte>()));
        }

        public static DhtKey Sha256Utf8(string text)
        {
            return Sha256(Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        public static DhtKey ForUuid(UUID uuid)
        {
            return Sha256Utf8("uuid:" + uuid.ToString());
        }

        public static DhtKey ForLoc(string normalizedHomeURI)
        {
            return Sha256Utf8("loc:" + (normalizedHomeURI ?? string.Empty));
        }

        /// <summary>
        /// Kademlia bucket i holds XOR distances in [2^i, 2^{i+1}).
        /// Returns -1 when this equals other (distance 0).
        /// </summary>
        public int BucketIndex(DhtKey other)
        {
            byte[] x = Xor(other).Bytes;
            for (int i = 0; i < Size; i++)
            {
                if (x[i] != 0)
                    return (Size - 1 - i) * 8 + BitOperations.Log2(x[i]);
            }
            return -1;
        }

        public byte[] ToByteArray()
        {
            byte[] copy = new byte[Size];
            Buffer.BlockCopy(Bytes, 0, copy, 0, Size);
            return copy;
        }

        public string ToHex()
        {
            return Convert.ToHexString(Bytes).ToLowerInvariant();
        }

        public DhtKey Xor(DhtKey other)
        {
            byte[] a = Bytes;
            byte[] b = other.Bytes;
            byte[] r = new byte[Size];
            for (int i = 0; i < Size; i++)
                r[i] = (byte)(a[i] ^ b[i]);
            return new DhtKey(r);
        }

        public int CompareTo(DhtKey other)
        {
            byte[] a = Bytes;
            byte[] b = other.Bytes;
            for (int i = 0; i < Size; i++)
            {
                int c = a[i].CompareTo(b[i]);
                if (c != 0)
                    return c;
            }
            return 0;
        }

        public bool Equals(DhtKey other)
        {
            return CompareTo(other) == 0;
        }

        public override bool Equals(object obj)
        {
            return obj is DhtKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            HashCode hc = new HashCode();
            hc.AddBytes(Bytes);
            return hc.ToHashCode();
        }

        public override string ToString()
        {
            return ToHex();
        }

        public static bool operator ==(DhtKey a, DhtKey b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(DhtKey a, DhtKey b)
        {
            return !a.Equals(b);
        }

        private byte[] Bytes
        {
            get { return m_bytes ?? s_zero; }
        }

        private static readonly byte[] s_zero = new byte[Size];
    }
}
