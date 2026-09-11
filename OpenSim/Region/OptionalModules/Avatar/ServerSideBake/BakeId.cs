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

using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenMetaverse;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBake
{
    /// <summary>
    /// WearableCacheItem.CacheId for one bake slot. Classic viewers send this;
    /// server-side bake computes it from the wearables on that body part
    /// (item, source texture, tint, plus slot fill). Same outfit → same id.
    /// Reuse publishes TextureID = this hash. Force-rebake mints a new
    /// TextureID and keeps this hash as CacheId / XBakes key.
    /// </summary>
    public static class BakeId
    {
        public static UUID FromLayers(int faceIndex, IList<ResolvedLayer> layers, Color4 fill)
        {
            if (layers == null || layers.Count == 0)
                return UUID.Zero;

            StringBuilder sb = new StringBuilder(80 + layers.Count * 90);
            sb.Append("ssb-v1|");
            sb.Append(faceIndex);
            sb.Append("|fill=");
            AppendTint(sb, fill);
            for (int i = 0; i < layers.Count; i++)
            {
                ResolvedLayer layer = layers[i];
                if (BakeLayerMap.IsUnsetTexture(layer.TextureID))
                    continue;
                sb.Append('|');
                sb.Append((int)layer.TextureIndex);
                sb.Append(':');
                sb.Append(layer.ItemID.ToString());
                sb.Append(':');
                sb.Append(layer.TextureID.ToString());
                sb.Append(':');
                AppendTint(sb, layer.Tint);
            }

            byte[] hash = MD5.HashData(Encoding.ASCII.GetBytes(sb.ToString()));
            // RFC 4122 version 3 / variant 10 so this cannot look like a random v4 viewer bake.
            hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
            hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
            return new UUID(hash, 0);
        }

        private static void AppendTint(StringBuilder sb, Color4 c)
        {
            sb.Append(c.R.ToString("0.000", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(c.G.ToString("0.000", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(c.B.ToString("0.000", CultureInfo.InvariantCulture));
        }
    }
}
