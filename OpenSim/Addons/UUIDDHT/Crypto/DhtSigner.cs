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
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Org.BouncyCastle.Security;

namespace OpenSim.Addons.UUIDDHT
{
    public static class DhtSigner
    {
        public const int PublicKeySize = 32;
        public const int SecretKeySize = 32;
        public const int SignatureSize = 64;

        public static void GenerateKeyPair(out byte[] secretKey, out byte[] publicKey)
        {
            Ed25519PrivateKeyParameters priv = new Ed25519PrivateKeyParameters(new SecureRandom());
            secretKey = priv.GetEncoded();
            publicKey = priv.GeneratePublicKey().GetEncoded();
        }

        public static byte[] Sign(byte[] secretKey, byte[] message)
        {
            if (secretKey == null || secretKey.Length != SecretKeySize)
                throw new ArgumentException("Ed25519 secret key must be 32 bytes", nameof(secretKey));
            if (message == null)
                message = Array.Empty<byte>();

            Ed25519PrivateKeyParameters priv = new Ed25519PrivateKeyParameters(secretKey, 0);
            byte[] sig = new byte[SignatureSize];
            priv.Sign(Ed25519.Algorithm.Ed25519, null, message, 0, message.Length, sig, 0);
            return sig;
        }

        public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            if (publicKey == null || publicKey.Length != PublicKeySize)
                return false;
            if (signature == null || signature.Length != SignatureSize)
                return false;
            if (message == null)
                message = Array.Empty<byte>();

            return Ed25519.Verify(signature, 0, publicKey, 0, message, 0, message.Length);
        }

        public static DhtKey NodeIdFromPubkey(byte[] publicKey)
        {
            if (publicKey == null || publicKey.Length != PublicKeySize)
                throw new ArgumentException("Ed25519 public key must be 32 bytes", nameof(publicKey));
            return DhtKey.Sha256(publicKey);
        }

        public static string ToHex(byte[] bytes)
        {
            return Convert.ToHexString(bytes ?? Array.Empty<byte>()).ToLowerInvariant();
        }
    }
}
