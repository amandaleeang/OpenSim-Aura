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
using OpenMetaverse;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBake
{
    public sealed class BakeSlot
    {
        public BakeType BakeType;
        public AvatarTextureIndex FaceIndex;
        public int DefaultWidth;
        public int DefaultHeight;
        /// <summary>Source wearable texture indices, bottom layer first.</summary>
        public AvatarTextureIndex[] Sources;
    }

    /// <summary>
    /// Classic + Bakes-on-Mesh slot table. libomv BakeTypeToTextures is missing
    /// Universal/BoM tattoo indices, so this map is explicit.
    /// </summary>
    public static class BakeLayerMap
    {
        public static readonly BakeSlot[] Slots =
        {
            new BakeSlot
            {
                BakeType = BakeType.Head,
                FaceIndex = AvatarTextureIndex.HeadBaked,
                DefaultWidth = 512, DefaultHeight = 512,
                // Not Hair: hair_grain is the hair-mesh UV (Linden BAKED_HEAD
                // is bodypaint/tattoo/univ/alpha only). Blitting it here
                // paints hair across the face.
                Sources = new[]
                {
                    AvatarTextureIndex.HeadBodypaint,
                    AvatarTextureIndex.HeadTattoo,
                    AvatarTextureIndex.HeadUnivTattoo,
                    AvatarTextureIndex.HeadAlpha
                }
            },
            new BakeSlot
            {
                BakeType = BakeType.UpperBody,
                FaceIndex = AvatarTextureIndex.UpperBaked,
                DefaultWidth = 512, DefaultHeight = 512,
                Sources = new[]
                {
                    AvatarTextureIndex.UpperBodypaint,
                    AvatarTextureIndex.UpperTattoo,
                    AvatarTextureIndex.UpperUnivTattoo,
                    AvatarTextureIndex.UpperGloves,
                    AvatarTextureIndex.UpperUndershirt,
                    AvatarTextureIndex.UpperShirt,
                    AvatarTextureIndex.UpperJacket,
                    AvatarTextureIndex.UpperAlpha
                }
            },
            new BakeSlot
            {
                BakeType = BakeType.LowerBody,
                FaceIndex = AvatarTextureIndex.LowerBaked,
                DefaultWidth = 512, DefaultHeight = 512,
                Sources = new[]
                {
                    AvatarTextureIndex.LowerBodypaint,
                    AvatarTextureIndex.LowerTattoo,
                    AvatarTextureIndex.LowerUnivTattoo,
                    AvatarTextureIndex.LowerUnderpants,
                    AvatarTextureIndex.LowerSocks,
                    AvatarTextureIndex.LowerShoes,
                    AvatarTextureIndex.LowerPants,
                    AvatarTextureIndex.LowerJacket,
                    AvatarTextureIndex.LowerAlpha
                }
            },
            new BakeSlot
            {
                BakeType = BakeType.Eyes,
                FaceIndex = AvatarTextureIndex.EyesBaked,
                DefaultWidth = 128, DefaultHeight = 128,
                Sources = new[]
                {
                    AvatarTextureIndex.EyesIris,
                    AvatarTextureIndex.EyesTattoo,
                    AvatarTextureIndex.EyesAlpha
                }
            },
            new BakeSlot
            {
                BakeType = BakeType.Skirt,
                FaceIndex = AvatarTextureIndex.SkirtBaked,
                DefaultWidth = 512, DefaultHeight = 512,
                Sources = new[]
                {
                    AvatarTextureIndex.Skirt,
                    AvatarTextureIndex.SkirtTattoo
                }
            },
            new BakeSlot
            {
                BakeType = BakeType.Hair,
                FaceIndex = AvatarTextureIndex.HairBaked,
                DefaultWidth = 512, DefaultHeight = 512,
                Sources = new[]
                {
                    AvatarTextureIndex.Hair,
                    AvatarTextureIndex.HairTattoo,
                    AvatarTextureIndex.HairAlpha
                }
            },
            new BakeSlot
            {
                BakeType = BakeType.LeftArm,
                FaceIndex = AvatarTextureIndex.LeftArmBaked,
                DefaultWidth = 512, DefaultHeight = 512,
                Sources = new[] { AvatarTextureIndex.LeftArmTattoo }
            },
            new BakeSlot
            {
                BakeType = BakeType.LeftLeg,
                FaceIndex = AvatarTextureIndex.LeftLegBaked,
                DefaultWidth = 512, DefaultHeight = 512,
                Sources = new[] { AvatarTextureIndex.LeftLegTattoo }
            },
            new BakeSlot
            {
                BakeType = BakeType.Aux1,
                FaceIndex = AvatarTextureIndex.Aux1Baked,
                DefaultWidth = 512, DefaultHeight = 512,
                Sources = new[] { AvatarTextureIndex.Aux1Tattoo }
            },
            new BakeSlot
            {
                BakeType = BakeType.Aux2,
                FaceIndex = AvatarTextureIndex.Aux2Baked,
                DefaultWidth = 512, DefaultHeight = 512,
                Sources = new[] { AvatarTextureIndex.Aux2Tattoo }
            },
            new BakeSlot
            {
                BakeType = BakeType.Aux3,
                FaceIndex = AvatarTextureIndex.Aux3Baked,
                DefaultWidth = 512, DefaultHeight = 512,
                Sources = new[] { AvatarTextureIndex.Aux3Tattoo }
            }
        };

        private static readonly HashSet<AvatarTextureIndex> s_bakeFaces;

        static BakeLayerMap()
        {
            s_bakeFaces = new HashSet<AvatarTextureIndex>();
            foreach (BakeSlot slot in Slots)
                s_bakeFaces.Add(slot.FaceIndex);
        }

        public static bool IsBakeFace(AvatarTextureIndex index)
        {
            return s_bakeFaces.Contains(index);
        }

        public static bool IsUnsetTexture(UUID id)
        {
            return id.IsZero() || id.Equals(AppearanceManager.DEFAULT_AVATAR_TEXTURE);
        }

        public static bool IsAlphaMask(AvatarTextureIndex index)
        {
            return index == AvatarTextureIndex.HeadAlpha
                || index == AvatarTextureIndex.UpperAlpha
                || index == AvatarTextureIndex.LowerAlpha
                || index == AvatarTextureIndex.EyesAlpha
                || index == AvatarTextureIndex.HairAlpha;
        }

        /// <summary>
        /// Skin bodypaint already has colour in the pixels. Linden's Baker
        /// does not multiply these by the wearable tint.
        /// </summary>
        public static bool IsBodypaint(AvatarTextureIndex index)
        {
            return index == AvatarTextureIndex.HeadBodypaint
                || index == AvatarTextureIndex.UpperBodypaint
                || index == AvatarTextureIndex.LowerBodypaint;
        }

        /// <summary>
        /// Slots that include this wearable texture index in their source list.
        /// Hair grain is Hair bake only (not Head).
        /// </summary>
        public static List<BakeSlot> SlotsForSource(AvatarTextureIndex index)
        {
            List<BakeSlot> hits = new List<BakeSlot>();
            foreach (BakeSlot slot in Slots)
            {
                foreach (AvatarTextureIndex src in slot.Sources)
                {
                    if (src == index)
                    {
                        hits.Add(slot);
                        break;
                    }
                }
            }
            return hits;
        }

        public const int MinBakeSize = 512;
        public const int MaxBakeSize = 2048;

        /// <summary>
        /// Per-slot bake size from the largest decoded wearable layer.
        /// Eyes stay at the classic 128. Other slots snap to 512 / 1024 / 2048.
        /// </summary>
        public static int SizeFor(BakeSlot slot, BakeLayerImage[] layers)
        {
            if (slot.BakeType == BakeType.Eyes)
                return slot.DefaultWidth;

            int largest = 0;
            if (layers != null)
            {
                for (int i = 0; i < layers.Length; i++)
                {
                    BakeLayerImage layer = layers[i];
                    if (layer == null || layer.Image == null)
                        continue;
                    int dim = layer.Image.Width > layer.Image.Height
                        ? layer.Image.Width : layer.Image.Height;
                    if (dim > largest)
                        largest = dim;
                }
            }

            if (largest <= MinBakeSize)
                return MinBakeSize;
            if (largest <= 1024)
                return 1024;
            return MaxBakeSize;
        }
    }
}
