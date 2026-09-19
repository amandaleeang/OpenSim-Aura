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
using System.IO;
using System.Text;

namespace OpenSim.Addons.UUIDDHT
{
    /// <summary>
    /// Living seed list at config-include/dht-seeds. One HomeURI per line.
    /// First entry is this server. Not an OpenSim Include.
    /// The routing table stays in RAM; this file is the on-disk peer list.
    /// Contacts are re-proven (PING / GET /dht/node) on every join.
    /// </summary>
    public static class DhtSeedFile
    {
        public const int DefaultMaxSeeds = 32;
        public const string DefaultPath = "config-include/dht-seeds";
        public const string ExampleSuffix = ".example";

        /// <summary>
        /// If dht-seeds is missing, copy dht-seeds.example so a downloaded
        /// StandaloneHypergrid binary joins the public overlay on first start.
        /// Does not overwrite an existing file (including an empty first-seed).
        /// </summary>
        public static bool EnsureFromExample(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || File.Exists(path))
                return false;
            string example = path + ExampleSuffix;
            if (!File.Exists(example))
                return false;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.Copy(example, path);
            return true;
        }

        public static List<string> Collect(string selfHome, IEnumerable<string> bootstrap,
            IEnumerable<string> discovered, bool allowPrivate, int max)
        {
            if (max <= 0)
                max = DefaultMaxSeeds;

            List<string> outList = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            Add(outList, seen, selfHome, allowPrivate, max);
            if (bootstrap != null)
            {
                foreach (string h in bootstrap)
                    Add(outList, seen, h, allowPrivate, max);
            }
            if (discovered != null)
            {
                foreach (string h in discovered)
                    Add(outList, seen, h, allowPrivate, max);
            }
            return outList;
        }

        public static string Format(IList<string> seeds)
        {
            if (seeds == null || seeds.Count == 0)
                return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < seeds.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(seeds[i]);
            }
            return sb.ToString();
        }

        public static string[] Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return Array.Empty<string>();

            string[] lines = File.ReadAllLines(path);
            List<string> homes = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim().Trim('"');
                if (line.Length == 0 || line[0] == '#' || line.StartsWith(";;", StringComparison.Ordinal))
                    continue;
                homes.Add(line);
            }
            return homes.ToArray();
        }

        public static bool Write(string path, IList<string> seeds)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# UUID-DHT bootstrap HomeURIs. First line is this server.");
            sb.AppendLine("# Rewritten on join and when a new contact is admitted.");
            if (seeds != null)
            {
                for (int i = 0; i < seeds.Count; i++)
                {
                    if (!string.IsNullOrEmpty(seeds[i]))
                        sb.AppendLine(seeds[i]);
                }
            }

            string next = sb.ToString();
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), next, StringComparison.Ordinal))
                return false;

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, next);
            File.Move(tmp, path, true);
            return true;
        }

        private static void Add(List<string> dest, HashSet<string> seen, string raw, bool allowPrivate, int max)
        {
            if (dest.Count >= max)
                return;
            if (!DhtLocator.TryNormalize(raw, allowPrivate, out string home))
                return;
            if (!seen.Add(home))
                return;
            dest.Add(home);
        }
    }
}
