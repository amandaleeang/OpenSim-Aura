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

using OpenMetaverse;
using Nini.Config;
using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Mono.Addins;

namespace OpenSim.Region.CoreModules.Avatar.BakedTextures
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "XBakes.Module")]
    public class XBakesModule : INonSharedRegionModule, IBakedTextureModule
    {
        protected Scene m_Scene;
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private string m_URL = String.Empty;
        private string m_BaseDirectory = String.Empty;
        private static XmlSerializer m_serializer = new XmlSerializer(typeof(AssetBase));
        private bool m_enabled = false;

        private static IServiceAuth m_Auth;

        public void Initialise(IConfigSource configSource)
        {
            IConfig config = configSource.Configs["XBakes"];
            if (config == null)
                return;

            m_URL = config.GetString("URL", String.Empty);
            m_BaseDirectory = config.GetString("BaseDirectory", String.Empty);
            if (m_URL.Length == 0 && m_BaseDirectory.Length == 0)
                return;

            m_enabled = true;

            if (m_URL.Length > 0)
                m_Auth = ServiceAuth.Create(configSource, "XBakes");
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_Scene = scene;

            scene.RegisterModuleInterface<IBakedTextureModule>(this);
            if (m_URL.Length > 0)
                m_log.InfoFormat("[XBakes]: Enabled (Robust) for region {0}", scene.RegionInfo.RegionName);
            else
                m_log.InfoFormat("[XBakes]: Enabled (local {0}) for region {1}", m_BaseDirectory, scene.RegionInfo.RegionName);
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "XBakes.Module"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public WearableCacheItem[] Get(UUID id)
        {
            if (m_URL.Length > 0)
            {
                using (RestClient rc = new RestClient(m_URL))
                {
                    rc.AddResourcePath("bakes/" + id.ToString());
                    rc.RequestMethod = "GET";
                    try
                    {
                        using (MemoryStream s = rc.Request(m_Auth))
                            return DecodeBakes(s, id);
                    }
                    catch (XmlException)
                    {
                        return null;
                    }
                }
            }

            if (m_BaseDirectory.Length == 0)
                return null;

            string diskFile = LocalBakePath(id);
            if (!File.Exists(diskFile))
                return null;

            try
            {
                using (FileStream fs = File.OpenRead(diskFile))
                    return DecodeBakes(fs, id);
            }
            catch (XmlException)
            {
                return null;
            }
            catch (IOException e)
            {
                m_log.WarnFormat("[XBakes]: Failed to read local bakes for {0}: {1}", id, e.Message);
                return null;
            }
        }

        public void Store(UUID agentId)
        {
        }

        public void UpdateMeshAvatar(UUID agentId)
        {
        }

        public void Store(UUID agentId, WearableCacheItem[] data)
        {
            if (m_URL.Length == 0 && m_BaseDirectory.Length == 0)
                return;

            byte[] uploadData = EncodeBakes(data, out int numberWears);
            if (uploadData == null || numberWears == 0)
                return;

            if (m_URL.Length > 0)
            {
                Util.FireAndForget(
                    delegate
                    {
                        using (RestClient rc = new RestClient(m_URL))
                        {
                            rc.AddResourcePath("bakes/" + agentId.ToString());
                            rc.POSTRequest(uploadData, m_Auth);
                            m_log.DebugFormat("[XBakes]: stored {0} textures for user {1}", numberWears, agentId);
                        }
                    }, null, "XBakesModule.Store");
                return;
            }

            Util.FireAndForget(
                delegate
                {
                    try
                    {
                        string diskFile = LocalBakePath(agentId);
                        Directory.CreateDirectory(Path.GetDirectoryName(diskFile));
                        File.Delete(diskFile);
                        File.WriteAllBytes(diskFile, uploadData);
                        m_log.DebugFormat("[XBakes]: stored {0} textures locally for user {1}", numberWears, agentId);
                    }
                    catch (Exception e)
                    {
                        m_log.WarnFormat("[XBakes]: Failed to store local bakes for {0}: {1}", agentId, e.Message);
                    }
                }, null, "XBakesModule.StoreLocal");
        }

        /// <summary>
        /// Same hashed path as Robust XBakes (OpenSim.Server.Handlers.BakedTextures.XBakes).
        /// A shared BaseDirectory can be used by standalone and Robust.
        /// </summary>
        private string LocalBakePath(UUID id)
        {
            string hash = id.ToString();
            return Path.Combine(m_BaseDirectory,
                Path.Combine(hash.Substring(0, 2),
                Path.Combine(hash.Substring(2, 2),
                Path.Combine(hash.Substring(4, 2),
                Path.Combine(hash.Substring(6, 4), hash)))));
        }

        private static byte[] EncodeBakes(WearableCacheItem[] data, out int numberWears)
        {
            numberWears = 0;
            using (MemoryStream bakeStream = new MemoryStream())
            using (XmlTextWriter bakeWriter = new XmlTextWriter(bakeStream, null))
            {
                bakeWriter.WriteStartElement(String.Empty, "BakedAppearance", String.Empty);
                List<int> extended = new List<int>();
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] != null && data[i].TextureAsset != null)
                    {
                        if (data[i].TextureIndex > 26)
                        {
                            extended.Add(i);
                            continue;
                        }
                        bakeWriter.WriteStartElement(String.Empty, "BakedTexture", String.Empty);
                        bakeWriter.WriteAttributeString(String.Empty, "TextureIndex", String.Empty, data[i].TextureIndex.ToString());
                        bakeWriter.WriteAttributeString(String.Empty, "CacheId", String.Empty, data[i].CacheId.ToString());
                        m_serializer.Serialize(bakeWriter, data[i].TextureAsset);
                        bakeWriter.WriteEndElement();
                        numberWears++;
                    }
                }

                if (extended.Count > 0)
                {
                    foreach (int i in extended)
                    {
                        bakeWriter.WriteStartElement(String.Empty, "BESetA", String.Empty);
                        bakeWriter.WriteAttributeString(String.Empty, "TextureIndex", String.Empty, data[i].TextureIndex.ToString());
                        bakeWriter.WriteAttributeString(String.Empty, "CacheId", String.Empty, data[i].CacheId.ToString());
                        m_serializer.Serialize(bakeWriter, data[i].TextureAsset);
                        bakeWriter.WriteEndElement();
                        numberWears++;
                    }
                }

                bakeWriter.WriteEndElement();
                bakeWriter.Flush();
                return bakeStream.ToArray();
            }
        }

        private WearableCacheItem[] DecodeBakes(Stream s, UUID id)
        {
            List<WearableCacheItem> ret = new List<WearableCacheItem>();
            using (XmlTextReader sr = new XmlTextReader(s))
            {
                sr.DtdProcessing = DtdProcessing.Ignore;
                sr.ReadStartElement("BakedAppearance");
                while (sr.LocalName == "BakedTexture")
                {
                    string sTextureIndex = sr.GetAttribute("TextureIndex");
                    int lTextureIndex = Convert.ToInt32(sTextureIndex);
                    string sCacheId = sr.GetAttribute("CacheId");
                    UUID.TryParse(sCacheId, out UUID lCacheId);

                    sr.ReadStartElement("BakedTexture");
                    if (sr.Name == "AssetBase")
                    {
                        AssetBase a = (AssetBase)m_serializer.Deserialize(sr);
                        ret.Add(new WearableCacheItem()
                        {
                            CacheId = lCacheId,
                            TextureIndex = (uint)lTextureIndex,
                            TextureAsset = a,
                            TextureID = a.FullID
                        });
                        sr.ReadEndElement();
                    }
                }
                while (sr.LocalName == "BESetA")
                {
                    string sTextureIndex = sr.GetAttribute("TextureIndex");
                    int lTextureIndex = Convert.ToInt32(sTextureIndex);
                    string sCacheId = sr.GetAttribute("CacheId");
                    UUID.TryParse(sCacheId, out UUID lCacheId);

                    sr.ReadStartElement("BESetA");
                    if (sr.Name == "AssetBase")
                    {
                        AssetBase a = (AssetBase)m_serializer.Deserialize(sr);
                        ret.Add(new WearableCacheItem()
                        {
                            CacheId = lCacheId,
                            TextureIndex = (uint)lTextureIndex,
                            TextureAsset = a,
                            TextureID = a.FullID
                        });
                        sr.ReadEndElement();
                    }
                }
                m_log.DebugFormat("[XBakes]: read {0} textures for user {1}", ret.Count, id);
            }
            return ret.Count > 0 ? ret.ToArray() : null;
        }
    }
}
