// By Shadow Wolf and Lone Wolf, Wolf Territories Grid and IntelligentWolf Ltd.
// Copyright 2026 IntelligentWolf Ltd. Added to the os-webrtc-janus addon; distributed under the same
// MPL 2.0 licence as WebRtcVoiceRegionModule.cs.
//
// [HARDENING 2026-10-23] The pure rules of Wolf voice rooms, kept apart from the module so the
// regression test (Tests/WolfVoiceRegression) can check them against wolfvoice's own:
// the credential (main.rs call_credentials) and the room names the region hands out
// (HandleCall, WolfConference.cs RespondCredentials / ConfChannel / HandleWolfChannelCall).

using System;
using System.Security.Cryptography;
using System.Text;

using OpenMetaverse;

namespace WebRtcVoice
{
    public enum WolfVoiceRoomKind
    {
        Unknown,
        /// <summary>A private call: the bare P2P IM session id (caller XOR callee).</summary>
        PrivateCall,
        /// <summary>"group-" + group id: a group's voice.</summary>
        Group,
        /// <summary>"conf-" + session id: an ad-hoc conference.</summary>
        Conference,
        /// <summary>"wchan-" + group id + "-" + channel id: a Wolf Chat voice room.</summary>
        WolfChat,
    }

    public static class WolfVoiceRules
    {
        /// <summary>Lowercase hex HMAC-SHA256(key, "wolfcall1:" + channel + ":" + avatar) - byte for
        /// byte wolfvoice main.rs call_credentials (avatar = UUID.ToString(), lowercase hyphenated,
        /// the userID string the connector sends).</summary>
        public static string CallCredentials(string key, string channel, UUID avatarID)
        {
            using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
                return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes("wolfcall1:" + channel + ":" + avatarID.ToString()))).ToLowerInvariant();
        }

        /// <summary>Compare two secrets without the time taken telling how much matched.</summary>
        public static bool SameSecret(string a, string b)
        {
            if (a is null || b is null) return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
        }

        private static bool Uuid36(string s, out UUID id)
        {
            id = UUID.Zero;
            return s is not null && s.Length == 36 && UUID.TryParse(s, out id);
        }

        /// <summary>What kind of room a multiagent channel names, and its ids: `first` is the session
        /// (private call, conference) or the group (group, Wolf Chat); `second` the Wolf Chat channel.</summary>
        public static WolfVoiceRoomKind ParseRoom(string channel, out UUID first, out UUID second)
        {
            first = UUID.Zero;
            second = UUID.Zero;
            if (string.IsNullOrEmpty(channel)) return WolfVoiceRoomKind.Unknown;
            if (channel.StartsWith("group-", StringComparison.Ordinal))
                return Uuid36(channel.Substring(6), out first) ? WolfVoiceRoomKind.Group : WolfVoiceRoomKind.Unknown;
            if (channel.StartsWith("conf-", StringComparison.Ordinal))
                return Uuid36(channel.Substring(5), out first) ? WolfVoiceRoomKind.Conference : WolfVoiceRoomKind.Unknown;
            if (channel.StartsWith("wchan-", StringComparison.Ordinal))
            {
                string rest = channel.Substring(6);
                if (rest.Length == 73 && rest[36] == '-' && Uuid36(rest.Substring(0, 36), out first) && Uuid36(rest.Substring(37), out second))
                    return WolfVoiceRoomKind.WolfChat;
                first = second = UUID.Zero;
                return WolfVoiceRoomKind.Unknown;
            }
            return Uuid36(channel, out first) ? WolfVoiceRoomKind.PrivateCall : WolfVoiceRoomKind.Unknown;
        }
    }
}
