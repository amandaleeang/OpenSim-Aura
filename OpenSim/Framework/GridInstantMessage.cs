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
using OpenMetaverse;

namespace OpenSim.Framework
{
    [Serializable]
    public class GridInstantMessage
    {
        public Guid fromAgentID;
        public string fromAgentName;
        public Guid toAgentID;
        public byte dialog;
        public bool fromGroup;
        public string message;
        public Guid imSessionID;
        public byte offline;
        public Vector3 Position;
        public byte[] binaryBucket;

        public uint ParentEstateID;
        public Guid RegionID;
        public uint timestamp;

        /// <summary>
        /// Optional home grid URI of the sender (HG). Carried over XML-RPC as
        /// from_agent_home_uri so the receiving grid can resolve profile/IM replies
        /// for senders who are not friends and have never visited.
        /// </summary>
        public string fromAgentHomeURI;

        public GridInstantMessage()
        {
            binaryBucket = Array.Empty<byte>();
            fromAgentHomeURI = string.Empty;
        }

        public GridInstantMessage(GridInstantMessage im, bool addTimestamp)
        {
            fromAgentID = im.fromAgentID;
            fromAgentName = im.fromAgentName;
            toAgentID = im.toAgentID;
            dialog = im.dialog;
            fromGroup = im.fromGroup;
            message = im.message;
            imSessionID = im.imSessionID;
            offline = im.offline;
            Position = im.Position;
            binaryBucket = im.binaryBucket;
            RegionID = im.RegionID;
            ParentEstateID = im.ParentEstateID;
            fromAgentHomeURI = im.fromAgentHomeURI ?? string.Empty;

            if (addTimestamp)
                timestamp = (uint)Util.UnixTimeSinceEpoch();
        }

        public GridInstantMessage(IScene scene, UUID _fromAgentID,
                string _fromAgentName, UUID _toAgentID,
                byte _dialog, bool _fromGroup, string _message,
                UUID _imSessionID, bool _offline, Vector3 _position,
                byte[] _binaryBucket, bool addTimestamp)
        {
            fromAgentID = _fromAgentID.Guid;
            fromAgentName = _fromAgentName;
            toAgentID = _toAgentID.Guid;
            dialog = _dialog;
            fromGroup = _fromGroup;
            message = _message;
            imSessionID = _imSessionID.Guid;

            if (_offline)
                offline = 1;
            else
                offline = 0;
            Position = _position;
            binaryBucket = _binaryBucket;

            if (scene != null)
            {
                ParentEstateID = scene.RegionInfo.EstateSettings.ParentEstateID;
                RegionID = scene.RegionInfo.RegionSettings.RegionUUID.Guid;
            }

            fromAgentHomeURI = string.Empty;

            if (addTimestamp)
                timestamp = (uint)Util.UnixTimeSinceEpoch();
        }

        public GridInstantMessage(IScene scene, UUID _fromAgentID,
                string _fromAgentName, UUID _toAgentID, byte _dialog,
                string _message, bool _offline,
                Vector3 _position) : this(scene, _fromAgentID, _fromAgentName,
                _toAgentID, _dialog, false, _message,
                _fromAgentID ^ _toAgentID, _offline, _position, Array.Empty<byte>(), true)
        {
        }

        /// <summary>
        /// Universal user identifier for an IM participant: uuid;homeURI;First Last.
        /// Same form as friends, creator data, and get_uui.
        /// </summary>
        public static string BuildUUI(UUID id, string displayName, string homeURI)
        {
            if (id.IsZero() || string.IsNullOrWhiteSpace(homeURI))
                return string.Empty;

            OSHHTPHost host = new(homeURI);
            if (!host.IsValidHost)
                return string.Empty;

            SplitDisplayName(displayName, out string first, out string last);
            return id.ToString() + ";" + host.URIwEndSlash + ";" + first + " " + last;
        }

        public string BuildFromAgentUUI()
        {
            return BuildUUI(new UUID(fromAgentID), fromAgentName, fromAgentHomeURI);
        }

        public static void SplitDisplayName(string displayName, out string first, out string last)
        {
            first = "Unknown";
            last = "User";
            if (string.IsNullOrWhiteSpace(displayName))
                return;

            int parsed = Util.ParseAvatarName(displayName, out string f, out string l, out _);
            if (parsed >= 1 && !string.IsNullOrWhiteSpace(f))
                first = f;
            if (parsed >= 2 && !string.IsNullOrWhiteSpace(l) && !l.StartsWith('@'))
                last = l;
        }

        /// <summary>
        /// Resolve a sender HomeURI from optional HG IM XML-RPC fields and/or an
        /// HG-style from-name ("First.Last @host:port").
        /// </summary>
        public static string ResolveSenderHomeURI(string homeUriField, string uuiField, string fromAgentName)
        {
            if (!string.IsNullOrWhiteSpace(uuiField)
                    && Util.ParseFullUniversalUserIdentifier(uuiField, out _, out string uuiHome, out _, out _)
                    && !string.IsNullOrWhiteSpace(uuiHome))
            {
                OSHHTPHost uuiHost = new(uuiHome);
                if (uuiHost.IsValidHost)
                    return uuiHost.URI;
            }

            if (!string.IsNullOrWhiteSpace(homeUriField))
            {
                OSHHTPHost homeHost = new(homeUriField);
                if (homeHost.IsValidHost)
                    return homeHost.URI;
            }

            if (!string.IsNullOrWhiteSpace(fromAgentName)
                    && Util.ParseAvatarName(fromAgentName, out _, out _, out string nameHome) == 3
                    && !string.IsNullOrWhiteSpace(nameHome))
            {
                OSHHTPHost nameHost = new(nameHome);
                if (nameHost.IsValidHost)
                    return nameHost.URI;
            }

            return string.Empty;
        }
    }
}
