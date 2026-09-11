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
using System.Collections.Generic;
using log4net;
using OpenMetaverse;
using OpenMetaverse.Assets;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBake
{
    public sealed class ResolvedLayer
    {
        public AvatarTextureIndex TextureIndex;
        public UUID TextureID;
        public UUID ItemID;
        public bool IsAlphaMask;
        public Color4 Tint = Color4.White;
    }

    /// <summary>
    /// Decode worn clothing/bodypart assets and group their texture UUIDs
    /// onto classic and BoM bake slots, in composite order.
    /// </summary>
    public class WearableTextureResolver
    {
        private readonly ILog m_log;
        private readonly BakeAssetFetcher m_fetcher;
        private readonly IInventoryService m_inventory;
        private readonly UUID m_owner;

        public Color4 SkinTint { get; private set; } = Color4.White;
        public Color4 HairTint { get; private set; } = Color4.White;
        public Color4 EyesTint { get; private set; } = Color4.White;

        public WearableTextureResolver(ILog log, BakeAssetFetcher fetcher,
            IInventoryService inventory, UUID owner)
        {
            m_log = log;
            m_fetcher = fetcher;
            m_inventory = inventory;
            m_owner = owner;
        }

        private struct WornItem
        {
            public WearableType Type;
            public UUID ItemID;
            public UUID AssetID;
        }

        public Dictionary<BakeType, List<ResolvedLayer>> Resolve(AvatarAppearance appearance, string foreignAssetService)
        {
            // Per bake, per source index: layers in wear order.
            Dictionary<BakeType, Dictionary<AvatarTextureIndex, List<ResolvedLayer>>> collected =
                new Dictionary<BakeType, Dictionary<AvatarTextureIndex, List<ResolvedLayer>>>();

            List<WornItem> worn = CollectFromCof();
            int appearanceCount = CountAppearance(appearance);
            if (worn.Count > 0)
            {
                m_log.InfoFormat("[SSBAKE]: Current Outfit {0} wearable(s) (appearance list {1})",
                    worn.Count, appearanceCount);
            }
            else
            {
                worn = CollectFromAppearance(appearance);
                m_log.InfoFormat("[SSBAKE]: Current Outfit empty or missing, using appearance wearable list ({0})",
                    worn.Count);
            }

            foreach (WornItem item in worn)
                CollectTextures(collected, item, foreignAssetService);

            return BuildOrdered(collected);
        }

        /// <summary>
        /// SSA viewers keep every stacked layer (tattoos, extra shirts, …)
        /// as links in Current Outfit. AgentIsNowWearing only carries one
        /// item per WearableType, so appearance.Wearables is not enough.
        /// Order is COF link CreationDate, oldest closest to the body.
        /// </summary>
        private List<WornItem> CollectFromCof()
        {
            List<WornItem> result = new List<WornItem>();
            if (m_inventory == null || m_owner.IsZero())
                return result;

            InventoryFolderBase cof;
            try
            {
                cof = m_inventory.GetFolderForType(m_owner, FolderType.CurrentOutfit);
            }
            catch
            {
                return result;
            }
            if (cof == null || cof.ID.IsZero())
                return result;

            List<InventoryItemBase> items;
            try
            {
                items = m_inventory.GetFolderItems(m_owner, cof.ID);
            }
            catch
            {
                return result;
            }
            if (items == null || items.Count == 0)
                return result;

            items.Sort((a, b) => a.CreationDate.CompareTo(b.CreationDate));

            foreach (InventoryItemBase raw in items)
            {
                if (raw == null)
                    continue;
                if (raw.AssetType == (int)AssetType.LinkFolder)
                    continue;

                InventoryItemBase wear = raw;
                if (raw.AssetType == (int)AssetType.Link)
                {
                    if (raw.AssetID.IsZero())
                        continue;
                    wear = ResolveInventoryItem(raw.AssetID);
                    if (wear == null)
                        continue;
                }

                if (wear.AssetType != (int)AssetType.Clothing
                    && wear.AssetType != (int)AssetType.Bodypart)
                    continue;
                if (wear.AssetID.IsZero())
                    continue;

                WearableType wtype = (WearableType)(wear.Flags & 0xFF);
                result.Add(new WornItem
                {
                    Type = wtype,
                    ItemID = wear.ID,
                    AssetID = wear.AssetID
                });
                m_log.DebugFormat("[SSBAKE]: COF {0} item={1} asset={2}",
                    wtype, wear.ID, wear.AssetID);
            }

            return result;
        }

        private List<WornItem> CollectFromAppearance(AvatarAppearance appearance)
        {
            List<WornItem> result = new List<WornItem>();
            if (appearance?.Wearables == null)
                return result;

            for (int wtype = 0; wtype < appearance.Wearables.Length; wtype++)
            {
                AvatarWearable worn = appearance.Wearables[wtype];
                if (worn == null || worn.Count == 0)
                    continue;

                for (int i = 0; i < worn.Count; i++)
                {
                    WearableItem item = worn[i];
                    if (item.AssetID.IsZero() && item.ItemID.IsNotZero())
                    {
                        UUID resolved = ResolveAssetId(item.ItemID);
                        if (resolved.IsNotZero())
                        {
                            worn.Add(item.ItemID, resolved);
                            item = new WearableItem(item.ItemID, resolved);
                            m_log.InfoFormat("[SSBAKE]: resolved {0} item {1} -> asset {2}",
                                (WearableType)wtype, item.ItemID, resolved);
                        }
                    }

                    if (item.AssetID.IsZero())
                    {
                        m_log.DebugFormat("[SSBAKE]: wearable type {0} item {1} has no asset id",
                            (WearableType)wtype, item.ItemID);
                        continue;
                    }

                    result.Add(new WornItem
                    {
                        Type = (WearableType)wtype,
                        ItemID = item.ItemID,
                        AssetID = item.AssetID
                    });
                }
            }

            return result;
        }

        private void CollectTextures(
            Dictionary<BakeType, Dictionary<AvatarTextureIndex, List<ResolvedLayer>>> collected,
            WornItem item, string foreignAssetService)
        {
            AssetBase asset = m_fetcher.Get(item.AssetID, foreignAssetService, out string source);
            if (asset == null || asset.Data == null || asset.Data.Length == 0)
            {
                m_log.InfoFormat("[SSBAKE]: wearable {0} asset {1} missing ({2})",
                    item.Type, item.AssetID, source);
                return;
            }

            AssetWearable decoded = DecodeWearable(item.Type, item.AssetID, asset.Data);
            if (decoded?.Textures == null)
            {
                m_log.WarnFormat("[SSBAKE]: failed to decode wearable {0} asset {1} ({2} bytes from {3})",
                    item.Type, item.AssetID, asset.Data.Length, source);
                return;
            }

            m_log.DebugFormat("[SSBAKE]: decoded wearable {0} asset {1} from {2}: {3} texture(s)",
                item.Type, item.AssetID, source, decoded.Textures.Count);

            Color4 tint = TintFromWearable(decoded, item.Type);
            if (item.Type == WearableType.Skin)
                SkinTint = tint;
            else if (item.Type == WearableType.Hair)
                HairTint = tint;
            else if (item.Type == WearableType.Eyes)
                EyesTint = tint;
            if (!IsWhite(tint))
                m_log.InfoFormat("[SSBAKE]: {0} tint r={1:0.00} g={2:0.00} b={3:0.00} a={4:0.00}",
                    item.Type, tint.R, tint.G, tint.B, tint.A);

            foreach (KeyValuePair<AvatarTextureIndex, UUID> kvp in decoded.Textures)
            {
                if (BakeLayerMap.IsBakeFace(kvp.Key))
                    continue;
                if (BakeLayerMap.IsUnsetTexture(kvp.Value))
                    continue;

                List<BakeSlot> slots = BakeLayerMap.SlotsForSource(kvp.Key);
                if (slots.Count == 0)
                {
                    m_log.DebugFormat("[SSBAKE]: texture index {0} ({1}) not mapped to a bake slot",
                        (int)kvp.Key, kvp.Key);
                    continue;
                }

                foreach (BakeSlot slot in slots)
                {
                    if (!collected.TryGetValue(slot.BakeType, out Dictionary<AvatarTextureIndex, List<ResolvedLayer>> byIndex))
                    {
                        byIndex = new Dictionary<AvatarTextureIndex, List<ResolvedLayer>>();
                        collected[slot.BakeType] = byIndex;
                    }
                    if (!byIndex.TryGetValue(kvp.Key, out List<ResolvedLayer> list))
                    {
                        list = new List<ResolvedLayer>();
                        byIndex[kvp.Key] = list;
                    }
                    list.Add(new ResolvedLayer
                    {
                        TextureIndex = kvp.Key,
                        TextureID = kvp.Value,
                        ItemID = item.ItemID,
                        IsAlphaMask = BakeLayerMap.IsAlphaMask(kvp.Key),
                        Tint = tint
                    });
                    m_log.DebugFormat("[SSBAKE]:   {0} item={1} tex={2} ({3})",
                        kvp.Key, item.ItemID, kvp.Value, slot.BakeType);
                }
            }
        }

        private static int CountAppearance(AvatarAppearance appearance)
        {
            if (appearance?.Wearables == null)
                return 0;
            int n = 0;
            for (int i = 0; i < appearance.Wearables.Length; i++)
            {
                AvatarWearable w = appearance.Wearables[i];
                if (w != null)
                    n += w.Count;
            }
            return n;
        }

        private static Dictionary<BakeType, List<ResolvedLayer>> BuildOrdered(
            Dictionary<BakeType, Dictionary<AvatarTextureIndex, List<ResolvedLayer>>> collected)
        {
            Dictionary<BakeType, List<ResolvedLayer>> result = new Dictionary<BakeType, List<ResolvedLayer>>();
            foreach (BakeSlot slot in BakeLayerMap.Slots)
            {
                if (!collected.TryGetValue(slot.BakeType, out Dictionary<AvatarTextureIndex, List<ResolvedLayer>> byIndex))
                    continue;

                List<ResolvedLayer> layers = new List<ResolvedLayer>();
                foreach (AvatarTextureIndex src in slot.Sources)
                {
                    if (!byIndex.TryGetValue(src, out List<ResolvedLayer> srcLayers))
                        continue;
                    layers.AddRange(srcLayers);
                }

                if (layers.Count > 0)
                    result[slot.BakeType] = layers;
            }
            return result;
        }

        /// <summary>
        /// Wearable colour from VisualParams in OpenMetaverse (avatar_lad.xml)
        /// and the params stored on the clothing/bodypart asset.
        /// </summary>
        private static Color4 TintFromWearable(AssetWearable decoded, WearableType wtype)
        {
            if (decoded?.Params == null || decoded.Params.Count == 0)
                return Color4.White;

            AppearanceManager.WearableData wd = new AppearanceManager.WearableData
            {
                Asset = decoded,
                WearableType = wtype,
                AssetType = AppearanceManager.WearableTypeToAssetType(wtype)
            };

            int n = (int)AvatarTextureIndex.NumberOfEntries;
            if (n <= 0)
                n = 45;
            AppearanceManager.TextureData[] textures = new AppearanceManager.TextureData[n];
            try
            {
                AppearanceManager.DecodeWearableParams(wd, ref textures);
            }
            catch
            {
                return Color4.White;
            }

            for (int i = 0; i < textures.Length; i++)
            {
                Color4 c = textures[i].Color;
                if (c.R != 0f || c.G != 0f || c.B != 0f || c.A != 0f)
                    return c;
            }
            return Color4.White;
        }

        private static bool IsWhite(Color4 c)
        {
            return c.R >= 0.999f && c.G >= 0.999f && c.B >= 0.999f;
        }

        private UUID ResolveAssetId(UUID itemId)
        {
            InventoryItemBase inv = ResolveInventoryItem(itemId);
            return inv != null ? inv.AssetID : UUID.Zero;
        }

        private InventoryItemBase ResolveInventoryItem(UUID itemId)
        {
            if (m_inventory == null || m_owner.IsZero() || itemId.IsZero())
                return null;
            try
            {
                return m_inventory.GetItem(m_owner, itemId);
            }
            catch
            {
                return null;
            }
        }

        private static AssetWearable DecodeWearable(WearableType wtype, UUID assetId, byte[] data)
        {
            AssetType at = AppearanceManager.WearableTypeToAssetType(wtype);
            AssetWearable wearable;
            if (at == AssetType.Bodypart)
                wearable = new AssetBodypart(assetId, data);
            else
                wearable = new AssetClothing(assetId, data);

            try
            {
                if (wearable.Decode())
                    return wearable;
            }
            catch
            {
            }

            // Some grids store clothing as bodypart or the reverse; try the other.
            AssetWearable other = (at == AssetType.Bodypart)
                ? (AssetWearable)new AssetClothing(assetId, data)
                : new AssetBodypart(assetId, data);
            try
            {
                if (other.Decode())
                    return other;
            }
            catch
            {
            }

            return null;
        }
    }
}
