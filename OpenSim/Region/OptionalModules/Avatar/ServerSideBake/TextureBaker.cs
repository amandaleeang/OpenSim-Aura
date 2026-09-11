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
using System.Drawing;
using System.IO;
using OpenMetaverse;
using OpenMetaverse.Imaging;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBake
{
    public sealed class BakeLayerImage
    {
        public ManagedImage Image;
        public bool IsAlphaMask;
        public Color4 Tint = Color4.White;
        public bool ApplyTint;
    }

    /// <summary>
    /// Scale wearable textures to the bake size, stack them in order, blend
    /// with each layer's alpha, and encode JPEG2000.
    /// </summary>
    public static class TextureBaker
    {
        public static bool TryDecode(byte[] j2k, out ManagedImage image)
        {
            image = null;
            if (j2k == null || j2k.Length == 0)
                return false;
            try
            {
                return OpenJPEG.DecodeToImage(j2k, out image) && image != null;
            }
            catch
            {
                image = null;
                return false;
            }
        }

        public static byte[] Encode(ManagedImage image)
        {
            if (image == null)
                return null;
            try
            {
                return OpenJPEG.Encode(image, false);
            }
            catch
            {
                return null;
            }
        }

        public static ManagedImage Composite(int width, int height, BakeLayerImage[] layers,
            Color4 fill, bool opaqueBody, ManagedImage libraryColor, ManagedImage libraryGrain)
        {
            if (width <= 0 || height <= 0)
                return null;

            ManagedImage dest = new ManagedImage(width, height,
                ManagedImage.ImageChannels.Color | ManagedImage.ImageChannels.Alpha);
            int pixels = width * height;
            dest.Red = new byte[pixels];
            dest.Green = new byte[pixels];
            dest.Blue = new byte[pixels];
            dest.Alpha = new byte[pixels];

            Fill(dest, fill, opaqueBody);

            if (libraryColor != null)
            {
                ManagedImage color = libraryColor;
                if (color.Width != width || color.Height != height)
                    color = ScaleBilinear(color, width, height);
                BlendOver(dest, color);
            }

            if (libraryGrain != null)
            {
                ManagedImage grain = libraryGrain;
                if (grain.Width != width || grain.Height != height)
                    grain = ScaleBilinear(grain, width, height);
                MultiplyByMask(dest, grain);
            }

            if (layers == null)
                return dest;

            for (int i = 0; i < layers.Length; i++)
            {
                BakeLayerImage layer = layers[i];
                if (layer?.Image == null)
                    continue;

                ManagedImage src = layer.Image;
                if (src.Width != width || src.Height != height)
                    src = ScaleBilinear(src, width, height);

                if (layer.IsAlphaMask)
                {
                    ApplyAlphaMask(dest, src);
                    continue;
                }

                if (layer.ApplyTint)
                    ApplyTint(src, layer.Tint);
                BlendOver(dest, src);
            }

            return dest;
        }

        /// <summary>
        /// TGA from bin/openmetaverse_data via OpenMetaverse.Imaging.Baker.
        /// </summary>
        public static ManagedImage TryLoadResource(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;

            EnsureResourceDir();

            try
            {
                ManagedImage img = Baker.LoadResourceLayer(fileName);
                if (img != null)
                    return img;
            }
            catch
            {
            }

            string dir = Settings.RESOURCE_DIR;
            if (string.IsNullOrEmpty(dir))
                return null;

            string path = Path.Combine(dir, fileName);
            if (!File.Exists(path))
                path = Path.Combine(dir, "character", fileName);
            if (!File.Exists(path))
                return null;

            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    Bitmap bmp = LoadTGAClass.LoadTGA(fs);
                    if (bmp == null)
                        return null;
                    try
                    {
                        return new ManagedImage(bmp);
                    }
                    finally
                    {
                        bmp.Dispose();
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        public static void EnsureResourceDir()
        {
            string dir = Settings.RESOURCE_DIR;
            if (string.IsNullOrEmpty(dir) || !Path.IsPathRooted(dir))
            {
                string rooted = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    string.IsNullOrEmpty(dir) ? "openmetaverse_data" : dir);
                if (Directory.Exists(rooted))
                    Settings.RESOURCE_DIR = rooted;
            }
        }

        private static void Fill(ManagedImage dest, Color4 color, bool opaque)
        {
            int n = dest.Width * dest.Height;
            byte r = Utils.FloatToByte(color.R, 0f, 1f);
            byte g = Utils.FloatToByte(color.G, 0f, 1f);
            byte b = Utils.FloatToByte(color.B, 0f, 1f);
            byte a = opaque ? (byte)255 : (byte)0;
            for (int i = 0; i < n; i++)
            {
                dest.Red[i] = r;
                dest.Green[i] = g;
                dest.Blue[i] = b;
                dest.Alpha[i] = a;
            }
        }

        private static void ApplyTint(ManagedImage dest, Color4 tint)
        {
            if (dest?.Red == null || dest.Green == null || dest.Blue == null)
                return;
            if (tint.R >= 0.999f && tint.G >= 0.999f && tint.B >= 0.999f)
                return;

            int n = dest.Width * dest.Height;
            byte tr = Utils.FloatToByte(tint.R, 0f, 1f);
            byte tg = Utils.FloatToByte(tint.G, 0f, 1f);
            byte tb = Utils.FloatToByte(tint.B, 0f, 1f);
            for (int i = 0; i < n; i++)
            {
                dest.Red[i] = (byte)((dest.Red[i] * tr) >> 8);
                dest.Green[i] = (byte)((dest.Green[i] * tg) >> 8);
                dest.Blue[i] = (byte)((dest.Blue[i] * tb) >> 8);
            }
        }

        private static void MultiplyByMask(ManagedImage dest, ManagedImage src)
        {
            int n = dest.Width * dest.Height;
            bool srcHasAlpha = src.Alpha != null && src.Alpha.Length >= n;
            bool srcHasColor = src.Red != null && src.Red.Length >= n;
            for (int i = 0; i < n; i++)
            {
                int m;
                if (srcHasAlpha)
                    m = src.Alpha[i];
                else if (srcHasColor)
                    m = src.Red[i];
                else
                    continue;
                dest.Red[i] = (byte)((dest.Red[i] * m) >> 8);
                dest.Green[i] = (byte)((dest.Green[i] * m) >> 8);
                dest.Blue[i] = (byte)((dest.Blue[i] * m) >> 8);
            }
        }

        private static void BlendOver(ManagedImage dest, ManagedImage src)
        {
            int n = dest.Width * dest.Height;
            bool srcHasAlpha = src.Alpha != null && src.Alpha.Length >= n;
            bool srcHasColor = src.Red != null && src.Green != null && src.Blue != null
                && src.Red.Length >= n;

            for (int i = 0; i < n; i++)
            {
                int sa = srcHasAlpha ? src.Alpha[i] : 255;
                if (sa == 0)
                    continue;

                int sr = srcHasColor ? src.Red[i] : 0;
                int sg = srcHasColor ? src.Green[i] : 0;
                int sb = srcHasColor ? src.Blue[i] : 0;

                if (sa == 255)
                {
                    dest.Red[i] = (byte)sr;
                    dest.Green[i] = (byte)sg;
                    dest.Blue[i] = (byte)sb;
                    dest.Alpha[i] = 255;
                    continue;
                }

                int inv = 255 - sa;
                dest.Red[i] = (byte)((sr * sa + dest.Red[i] * inv) / 255);
                dest.Green[i] = (byte)((sg * sa + dest.Green[i] * inv) / 255);
                dest.Blue[i] = (byte)((sb * sa + dest.Blue[i] * inv) / 255);
                dest.Alpha[i] = (byte)(sa + dest.Alpha[i] * inv / 255);
            }
        }

        /// <summary>
        /// Alpha wearables punch holes: multiply dest color and alpha by the
        /// source alpha (or luminance if the mask has no alpha channel).
        /// </summary>
        private static void ApplyAlphaMask(ManagedImage dest, ManagedImage src)
        {
            int n = dest.Width * dest.Height;
            bool srcHasAlpha = src.Alpha != null && src.Alpha.Length >= n;
            bool srcHasColor = src.Red != null && src.Red.Length >= n;

            for (int i = 0; i < n; i++)
            {
                int a;
                if (srcHasAlpha)
                    a = src.Alpha[i];
                else if (srcHasColor)
                    a = src.Red[i];
                else
                    continue;

                dest.Red[i] = (byte)(dest.Red[i] * a / 255);
                dest.Green[i] = (byte)(dest.Green[i] * a / 255);
                dest.Blue[i] = (byte)(dest.Blue[i] * a / 255);
                dest.Alpha[i] = (byte)(dest.Alpha[i] * a / 255);
            }
        }

        public static ManagedImage ScaleBilinear(ManagedImage src, int destW, int destH)
        {
            if (src == null || destW <= 0 || destH <= 0)
                return src;
            if (src.Width == destW && src.Height == destH)
                return src;

            bool hasColor = src.Red != null && src.Green != null && src.Blue != null;
            bool hasAlpha = src.Alpha != null;
            ManagedImage dest = new ManagedImage(destW, destH, src.Channels);
            int destPixels = destW * destH;
            if (hasColor)
            {
                dest.Red = new byte[destPixels];
                dest.Green = new byte[destPixels];
                dest.Blue = new byte[destPixels];
            }
            if (hasAlpha)
                dest.Alpha = new byte[destPixels];

            float xRatio = (src.Width > 1 && destW > 1) ? (float)(src.Width - 1) / (destW - 1) : 0f;
            float yRatio = (src.Height > 1 && destH > 1) ? (float)(src.Height - 1) / (destH - 1) : 0f;
            int srcW = src.Width;
            int srcH = src.Height;

            for (int y = 0; y < destH; y++)
            {
                float sy = y * yRatio;
                int y0 = (int)sy;
                int y1 = Math.Min(y0 + 1, srcH - 1);
                float fy = sy - y0;

                for (int x = 0; x < destW; x++)
                {
                    float sx = x * xRatio;
                    int x0 = (int)sx;
                    int x1 = Math.Min(x0 + 1, srcW - 1);
                    float fx = sx - x0;
                    int di = y * destW + x;

                    if (hasColor)
                    {
                        dest.Red[di] = Sample(src.Red, srcW, x0, y0, x1, y1, fx, fy);
                        dest.Green[di] = Sample(src.Green, srcW, x0, y0, x1, y1, fx, fy);
                        dest.Blue[di] = Sample(src.Blue, srcW, x0, y0, x1, y1, fx, fy);
                    }
                    if (hasAlpha)
                        dest.Alpha[di] = Sample(src.Alpha, srcW, x0, y0, x1, y1, fx, fy);
                }
            }

            return dest;
        }

        private static byte Sample(byte[] ch, int width, int x0, int y0, int x1, int y1, float fx, float fy)
        {
            float v00 = ch[y0 * width + x0];
            float v10 = ch[y0 * width + x1];
            float v01 = ch[y1 * width + x0];
            float v11 = ch[y1 * width + x1];
            float v0 = v00 + (v10 - v00) * fx;
            float v1 = v01 + (v11 - v01) * fx;
            return (byte)(v0 + (v1 - v0) * fy + 0.5f);
        }
    }
}
