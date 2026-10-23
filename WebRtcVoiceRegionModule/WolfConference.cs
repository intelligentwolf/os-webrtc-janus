// By Shadow Wolf and Lone Wolf, Wolf Territories Grid and IntelligentWolf Ltd.
// Copyright 2026 IntelligentWolf Ltd. Added to the os-webrtc-janus addon; distributed under the same
// MPL 2.0 licence as WebRtcVoiceRegionModule.cs.
//
// Group voice (works on any grid with the Groups module and a credential key), plus two OPTIONAL
// features that need a conference service: ad-hoc conferences (text + voice) and Wolf Chat voice
// rooms. Both are OFF unless [WebRtcVoice] ConferenceApiUrl is set AND a sim secret file exists
// ([Wolf] SimSecretPath, default /etc/opensim/sim_secret). While they are off, "start conference"
// fails at once with the viewer's own error string, exactly as without this file. What the viewer sends and expects was
// read from Firestorm 7.2.x; the citations each method relies on are at the method.
//
// Who is in a conference lives in ONE place, the conference service (on the Wolf Territories Grid
// a web service of its own; requests are signed with the sim secret, see Brain()), because the
// people in one call stand on different region processes. This region keeps a 10 s copy per conference so a
// burst of typing costs one lookup.
//
// Voice: every room credential is HMAC(relay key, "wolfcall1:" + channel + ":" + avatar)
// (CallCredentials, WebRtcVoiceRegionModule.cs), which wolfvoice re-computes (main.rs
// call_credentials) - so wolfvoice needs no change. Room names are namespaced so a credential for one
// kind of room can never be used for another: "conf-<session>", "group-<group>",
// "wchan-<group>-<channel>"; a private call keeps the bare session id it has had since 2026-10-19.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace WebRtcVoice
{
    public partial class WebRtcVoiceRegionModule
    {
        // Source: llimview.cpp:120-125 EMultiAgentChatSessionType GROUP_CHAT_SESSION = 0.
        private const int GROUP_CHAT_SESSION = 0;
        // Source: llinstantmessage.h:44-112 IM_SESSION_SEND = 17, IM_SESSION_LEAVE = 18
        // (libomv InstantMessageDialog.SessionSend / SessionDrop).
        private const byte IM_SESSION_SEND = (byte)InstantMessageDialog.SessionSend;
        private const byte IM_SESSION_LEAVE = (byte)InstantMessageDialog.SessionDrop;

        // Cross-region conference deliveries ride the same unassigned IM dialog as call invitations
        // (WOLF_CALL_DIALOG), told apart by this marker.
        private const string WOLF_CONF_MARK = "wolfconf1\n";
        // The conference service refuses more than 25 people in one conference;
        // a single request never names more than this many new people.
        private const int CONFERENCE_MAX_INVITES = 24;

        private static readonly HttpClient s_brain = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly TimeSpan CONF_CACHE = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan NOT_CONF_CACHE = TimeSpan.FromMinutes(5);

        // [WebRtcVoice] ConferenceApiUrl. Empty (the default): conferences and Wolf Chat rooms are off.
        private string m_ConfUrl = string.Empty;
        private string m_SimSecret = string.Empty;
        private readonly Dictionary<UUID, Conference> m_Conferences = new Dictionary<UUID, Conference>();
        private readonly Dictionary<UUID, DateTime> m_NotConferences = new Dictionary<UUID, DateTime>();
        // [review] "you are not in it" (403), remembered a minute per session XOR agent, so typing in a session one is
        // not in does not ask the brain every line.
        private readonly Dictionary<UUID, DateTime> m_NotMember = new Dictionary<UUID, DateTime>();
        private static readonly TimeSpan NOT_MEMBER_CACHE = TimeSpan.FromMinutes(1);
        private const int CACHE_PRUNE_AT = 5000;
        // [review] an envelope older than this is a replay (or a far-out clock) and is dropped - the sim-signature skew
        // the conference service allows (300 s).
        private const long ENVELOPE_MAX_AGE = 300;

        private sealed class ConfMember
        {
            public UUID Id;
            public string Name = string.Empty;
            public bool Joined;
        }

        private sealed class Conference
        {
            public UUID Id;
            public string Name = string.Empty;
            public bool Voice;
            public List<ConfMember> Members = new List<ConfMember>();
            public List<UUID> Added = new List<UUID>();
            public DateTime Fetched;

            public ConfMember Find(UUID id)
            {
                foreach (ConfMember m in Members)
                    if (m.Id == id) return m;
                return null;
            }
        }

        // ------------------------------------------------------------------------------ set-up

        /// <summary>Reads the conference settings: [WebRtcVoice] ConferenceApiUrl and the file named by
        /// [Wolf] SimSecretPath (default /etc/opensim/sim_secret). Without a URL nothing is read and
        /// conferences stay off.</summary>
        private void InitConference(IConfigSource config)
        {
            IConfig voice = config.Configs["WebRtcVoice"];
            if (voice != null)
                m_ConfUrl = voice.GetString("ConferenceApiUrl", m_ConfUrl).Trim();
            if (m_ConfUrl.Length == 0)
            {
                m_log.Info($"{logHeader}: no ConferenceApiUrl - conference chats and Wolf Chat voice rooms are off (group voice is not affected)");
                return;
            }
            string secretPath = "/etc/opensim/sim_secret";
            IConfig wolf = config.Configs["Wolf"];
            if (wolf != null)
                secretPath = wolf.GetString("SimSecretPath", secretPath);
            try
            {
                if (!string.IsNullOrEmpty(secretPath) && File.Exists(secretPath))
                    m_SimSecret = File.ReadAllText(secretPath).Trim();
            }
            catch (Exception e)
            {
                m_log.WarnFormat("{0}: could not read {1}: {2}", logHeader, secretPath, e.Message);
            }
            if (m_SimSecret.Length == 0)
                m_log.Warn($"{logHeader}: no sim secret - conference chats and Wolf Chat voice rooms are off");
        }

        private void OnMakeRootAgentConf(ScenePresence sp)
        {
            // [review] A fresh login has no conference windows open, and the viewer drops conference text for a session
            // it does not have (llimprocessing.cpp:1865-1872). So at login every conference this avatar had joined goes
            // back to 'invited': the next line in it arrives as an invitation, which opens the window again.
            if ((sp.TeleportFlags & Constants.TeleportFlags.ViaLogin) != 0 && m_SimSecret.Length > 0)
            {
                Scene scene = sp.Scene;
                UUID who = sp.UUID;
                Util.FireAndForget(delegate
                {
                    Brain(scene, "relogin", who, UUID.Zero, null, out string _, out int _);
                    lock (m_Conferences) m_Conferences.Clear();
                }, null, "WolfConferenceRelogin");
            }
            // -= first: a presence made root twice must not get every message twice.
            sp.ControllingClient.OnInstantMessage -= OnClientConferenceMessage;
            sp.ControllingClient.OnInstantMessage += OnClientConferenceMessage;
        }

        private void OnMakeChildAgentConf(ScenePresence sp)
        {
            sp.ControllingClient.OnInstantMessage -= OnClientConferenceMessage;
        }

        // ------------------------------------------------------------------------------ the brain

        private static string HexHmac(string key, string text)
        {
            using (HMACSHA256 h = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
                return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        /// <summary>
        /// One conference-service request, acting for `agent`. Blocks: call it from an HTTP handler or a
        /// pool thread, never from the UDP/heartbeat threads. Returns the conference as the brain now
        /// has it, or null with `error` and `status` (HTTP status, 0 = no answer) set.
        /// Signed: hex hmac_sha256(sim secret, region + "\n" + agent + "\n" + time + "\n" + body)
        /// in X-Wolf-Sim-Sig, with X-Wolf-Sim (region), X-Wolf-Sim-Agent and X-Wolf-Sim-Time.
        /// </summary>
        private Conference Brain(Scene scene, string action, UUID agent, UUID session,
                                 Dictionary<string, object> extra, out string error, out int status)
        {
            return Brain(scene, action, agent, session, extra, out error, out status, out bool _);
        }

        /// <summary>As Brain, also saying whether the answer was the brain's own JSON refusal (`answered`) - only that
        /// may be cached as "not a conference": a proxy's HTML 404 (a missed deploy) must not turn conferences off.</summary>
        private Conference Brain(Scene scene, string action, UUID agent, UUID session,
                                 Dictionary<string, object> extra, out string error, out int status, out bool answered)
        {
            error = null;
            status = 0;
            answered = false;
            if (m_SimSecret.Length == 0 || m_ConfUrl.Length == 0)
            {
                error = "conferences are not configured on this region";
                return null;
            }
            string region = scene.RegionInfo.RegionID.ToString();
            Dictionary<string, object> body = extra is null ? new Dictionary<string, object>() : new Dictionary<string, object>(extra);
            body["action"] = action;
            body["region"] = region;
            body["session"] = session.ToString();
            string json = JsonSerializer.Serialize(body);
            long when = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string who = agent.ToString();
            string sig = HexHmac(m_SimSecret, region + "\n" + who + "\n" + when.ToString(CultureInfo.InvariantCulture) + "\n" + json);
            try
            {
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, m_ConfUrl))
                {
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    req.Headers.Add("X-Wolf-Sim", region);
                    req.Headers.Add("X-Wolf-Sim-Agent", who);
                    req.Headers.Add("X-Wolf-Sim-Time", when.ToString(CultureInfo.InvariantCulture));
                    req.Headers.Add("X-Wolf-Sim-Sig", sig);
                    using (HttpResponseMessage res = s_brain.SendAsync(req).GetAwaiter().GetResult())
                    {
                        status = (int)res.StatusCode;
                        string text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        using (JsonDocument doc = JsonDocument.Parse(text))
                        {
                            JsonElement root = doc.RootElement;
                            answered = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("success", out JsonElement _);
                            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out JsonElement ok)
                                || ok.ValueKind != JsonValueKind.True)
                            {
                                error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out JsonElement e)
                                        && e.ValueKind == JsonValueKind.String ? e.GetString() : "the conference service said no";
                                return null;
                            }
                            return ParseConference(root, session);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                error = "the conference service did not answer";
                m_log.WarnFormat("{0}[CONF]: {1} for {2} on {3}: {4}", logHeader, action, agent, session, e.Message);
                return null;
            }
        }

        private static Conference ParseConference(JsonElement root, UUID session)
        {
            Conference c = new Conference { Id = session, Fetched = DateTime.UtcNow };
            if (root.TryGetProperty("session", out JsonElement s) && s.ValueKind == JsonValueKind.Object)
            {
                if (s.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String) c.Name = n.GetString();
                if (s.TryGetProperty("voice", out JsonElement v) && v.ValueKind == JsonValueKind.Number) c.Voice = v.GetInt32() != 0;
            }
            if (root.TryGetProperty("members", out JsonElement ms) && ms.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement m in ms.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object) continue;
                    if (!m.TryGetProperty("agent", out JsonElement a) || a.ValueKind != JsonValueKind.String
                        || !UUID.TryParse(a.GetString(), out UUID id)) continue;
                    ConfMember cm = new ConfMember { Id = id };
                    if (m.TryGetProperty("name", out JsonElement nm) && nm.ValueKind == JsonValueKind.String) cm.Name = nm.GetString();
                    cm.Joined = m.TryGetProperty("state", out JsonElement st) && st.ValueKind == JsonValueKind.String && st.GetString() == "joined";
                    c.Members.Add(cm);
                }
            }
            if (root.TryGetProperty("added", out JsonElement ad) && ad.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement a in ad.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.String && UUID.TryParse(a.GetString(), out UUID id))
                        c.Added.Add(id);
            }
            return c;
        }

        private void Remember(Conference c)
        {
            lock (m_Conferences)
            {
                m_Conferences[c.Id] = c;
                m_NotConferences.Remove(c.Id);
                foreach (ConfMember m in c.Members) m_NotMember.Remove(c.Id ^ m.Id);
                PruneCaches(DateTime.UtcNow);
            }
        }

        private void Forget(UUID session)
        {
            lock (m_Conferences) m_Conferences.Remove(session);
        }

        /// <summary>[review] Keeps the three caches bounded (a client naming random session ids would grow them). Called
        /// with m_Conferences locked.</summary>
        private void PruneCaches(DateTime now)
        {
            if (m_NotConferences.Count + m_NotMember.Count + m_Conferences.Count < CACHE_PRUNE_AT) return;
            List<UUID> gone = new List<UUID>();
            foreach (KeyValuePair<UUID, DateTime> kv in m_NotConferences) if (now - kv.Value >= NOT_CONF_CACHE) gone.Add(kv.Key);
            foreach (UUID id in gone) m_NotConferences.Remove(id);
            gone.Clear();
            foreach (KeyValuePair<UUID, DateTime> kv in m_NotMember) if (now - kv.Value >= NOT_MEMBER_CACHE) gone.Add(kv.Key);
            foreach (UUID id in gone) m_NotMember.Remove(id);
            gone.Clear();
            foreach (KeyValuePair<UUID, Conference> kv in m_Conferences) if (now - kv.Value.Fetched >= CONF_CACHE) gone.Add(kv.Key);
            foreach (UUID id in gone) m_Conferences.Remove(id);
        }

        /// <summary>The conference `session` as `agent` (a member) may see it: the region's copy when it is
        /// under 10 s old and lists them, else the brain's. Null for an id that is not a conference (a
        /// group session, an unknown id) or someone who is not in it.</summary>
        private Conference GetConference(Scene scene, UUID agent, UUID session)
        {
            DateTime now = DateTime.UtcNow;
            lock (m_Conferences)
            {
                if (m_NotConferences.TryGetValue(session, out DateTime when) && now - when < NOT_CONF_CACHE)
                    return null;
                if (m_NotMember.TryGetValue(session ^ agent, out DateTime nm) && now - nm < NOT_MEMBER_CACHE)
                    return null;
                if (m_Conferences.TryGetValue(session, out Conference c) && now - c.Fetched < CONF_CACHE && c.Find(agent) != null)
                    return c;
            }
            Conference got = Brain(scene, "members", agent, session, null, out string error, out int status, out bool answered);
            if (got != null)
            {
                Remember(got);
                return got;
            }
            lock (m_Conferences)
            {
                if (answered && status == (int)HttpStatusCode.NotFound) m_NotConferences[session] = now;
                else if (answered && status == (int)HttpStatusCode.Forbidden) m_NotMember[session ^ agent] = now;
                PruneCaches(now);
            }
            m_log.DebugFormat("{0}[CONF]: {1} is not in conference {2}: {3}", logHeader, agent, session, error);
            return null;
        }

        private static List<UUID> UuidList(OSD param, UUID self)
        {
            List<UUID> list = new List<UUID>();
            if (param is OSDArray arr)
            {
                foreach (OSD o in arr)
                {
                    UUID id = o.AsUUID();
                    if (!id.IsZero() && id != self && !list.Contains(id)) list.Add(id);
                }
            }
            return list;
        }

        private static List<string> Strings(List<UUID> ids)
        {
            List<string> s = new List<string>(ids.Count);
            foreach (UUID id in ids) s.Add(id.ToString());
            return s;
        }

        // ------------------------------------------------------------------------------ ChatSessionRequest

        /// <summary>
        /// "start conference" {session-id: random (llimview.cpp:2600-2603), params: [invitees]}
        /// (llimview.cpp:585-598). The viewer only learns the result from the ChatterBoxSessionStartReply
        /// event (llimview.cpp:4925-5002); the HTTP body is ignored. Answering 400 would make the viewer
        /// fall back to the deprecated UDP conference start (llimview.cpp:610-617), so failures are a
        /// StartReply with success=false and a strings.xml key (showSessionStartError).
        /// </summary>
        private void HandleStartConference(Scene scene, UUID agent, UUID session, OSDMap map)
        {
            List<UUID> invitees = UuidList(map.ContainsKey("params") ? map["params"] : null, agent);
            if (invitees.Count == 0 || invitees.Count > CONFERENCE_MAX_INVITES)
            {
                SendChatterBoxSessionStartReply(scene, agent, session, CONFERENCE_SESSION, string.Empty, false,
                        invitees.Count == 0 ? "generic_request_error" : "no_ability_error", false);
                return;
            }
            // [review] The id is the viewer's choice (random, llimview.cpp:2600-2603) - so refuse one that is already
            // a group's chat (a group's session id is the group id, :2595-2598) or a private chat's (caller XOR callee,
            // :2610-2622): its text would land in that real window on the invitees' screens (llimprocessing.cpp:1863-1880).
            if (IsTakenSessionId(scene, agent, session, invitees))
            {
                m_log.WarnFormat("{0}[CONF]: {1} tried to start a conference on a group or private chat id {2}; refused", logHeader, agent, session);
                SendChatterBoxSessionStartReply(scene, agent, session, CONFERENCE_SESSION, string.Empty, false, "generic_request_error", false);
                return;
            }
            // Inviting rings or opens a window on other people's screens: the same 6 a minute as calls (AllowCall).
            if (!AllowCall(agent))
            {
                m_log.InfoFormat("{0}[CONF]: {1} is starting conferences too often; refused", logHeader, agent);
                SendChatterBoxSessionStartReply(scene, agent, session, CONFERENCE_SESSION, string.Empty, false, "generic_request_error", false);
                return;
            }
            string myName = AvatarName(scene, agent);
            Conference c = Brain(scene, "start", agent, session, new Dictionary<string, object>
            {
                ["invitees"] = Strings(invitees),
                ["name"] = "Conference with " + myName,
            }, out string error, out int _);
            if (c is null)
            {
                m_log.InfoFormat("{0}[CONF]: {1} could not start conference {2}: {3}", logHeader, agent, session, error);
                SendChatterBoxSessionStartReply(scene, agent, session, CONFERENCE_SESSION, string.Empty, false, "generic_request_error", false);
                return;
            }
            Remember(c);
            // Same id back: the viewer files the session under the id it chose (processSessionInitializedReply
            // re-keys only when they differ, llimview.cpp:1715-1748).
            SendChatterBoxSessionStartReply(scene, agent, session, CONFERENCE_SESSION, c.Name, true, string.Empty, CallsOn);
            SendAgentList(scene, agent, session, c, true);
            m_log.InfoFormat("{0}[CONF]: {1} started conference {2} with {3} people", logHeader, agent, session, invitees.Count);
            InviteAdded(scene, agent, myName, c, c.Added);
        }

        /// <summary>
        /// "invite" {session-id, params: [uuids]} from the Add button of an ad-hoc IM window
        /// (fsfloaterim.cpp:2107-2144; the reply is ignored). Anyone who has joined may add people.
        /// </summary>
        private void HandleInvite(Scene scene, UUID agent, UUID session, OSDMap map)
        {
            List<UUID> invitees = UuidList(map.ContainsKey("params") ? map["params"] : null, agent);
            if (invitees.Count == 0) return;
            string myName = AvatarName(scene, agent);
            if (invitees.Count > CONFERENCE_MAX_INVITES)
            {
                TellSystem(scene, agent, session, "No one was added: you can add at most " + CONFERENCE_MAX_INVITES + " people at once.");
                return;
            }
            if (!AllowCall(agent))
            {
                TellSystem(scene, agent, session, "No one was added: you are adding people too fast. Wait a minute and try again.");
                return;
            }
            Conference c = Brain(scene, "invite", agent, session, new Dictionary<string, object>
            {
                ["invitees"] = Strings(invitees),
            }, out string error, out int _);
            if (c is null)
            {
                TellSystem(scene, agent, session, "No one was added: " + (error ?? "the conference service said no") + ".");
                return;
            }
            Remember(c);
            if (c.Added.Count < invitees.Count)
                TellSystem(scene, agent, session, (invitees.Count - c.Added.Count) + " of the people you picked were already in this conversation.");
            InviteAdded(scene, agent, myName, c, c.Added);
        }

        /// <summary>Rings / messages the people just added. While a call is running they get the voice
        /// invitation (VoiceInviteAdHoc, llimview.cpp:4236-4241) with their own credential; otherwise the
        /// text invitation, which the viewer accepts by itself (llimview.cpp:5232-5267).</summary>
        private void InviteAdded(Scene scene, UUID inviter, string inviterName, Conference c, List<UUID> added)
        {
            foreach (UUID id in added)
            {
                if (c.Voice && CallsOn)
                    DeliverConf(scene, id, VoiceEnvelope(inviter, inviterName, c, id));
                else
                    DeliverConf(scene, id, TextEnvelope("invite", inviter, inviterName, c, "(added you to this conversation)"));
            }
        }

        /// <summary>
        /// "accept invitation" {session-id} (llimview.cpp:674-676). The HTTP reply goes straight to
        /// LLSpeakerMgr::setSpeakers (llimview.cpp:722; llspeakers.cpp:687-737 reads "agent_info"),
        /// and a 404 shows session_does_not_exist_error (llimview.cpp:697-701).
        /// </summary>
        private void HandleAccept(Scene scene, UUID agent, UUID session, IOSHttpResponse response)
        {
            Conference c = Brain(scene, "accept", agent, session, null, out string error, out int status);
            if (c is null)
            {
                m_log.InfoFormat("{0}[CONF]: {1} could not accept {2}: {3}", logHeader, agent, session, error);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            Remember(c);
            OSDMap info = new OSDMap();
            foreach (ConfMember m in c.Members)
            {
                if (!m.Joined) continue;
                info[m.Id.ToString()] = new OSDMap
                {
                    { "is_moderator", OSD.FromBoolean(false) },
                    { "mutes", new OSDMap { { "text", OSD.FromBoolean(false) } } }
                };
            }
            response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(new OSDMap { { "agent_info", info } });
            // The others see them arrive (ChatterBoxSessionAgentListUpdates, llimview.cpp:4424-4487).
            foreach (ConfMember m in c.Members)
                if (m.Joined && m.Id != agent)
                    DeliverConf(scene, m.Id, AgentsEnvelope(c, agent, true));
        }

        /// <summary>"decline invitation" (llimview.cpp:3504-3514) and IM_SESSION_LEAVE (llimview.cpp:2180-2196):
        /// out of the conference; the others see them go.</summary>
        private void HandleLeave(Scene scene, UUID agent, UUID session)
        {
            Conference c = Brain(scene, "leave", agent, session, null, out string error, out int status);
            if (c is null)
            {
                m_log.DebugFormat("{0}[CONF]: leave {1} by {2}: {3}", logHeader, session, agent, error);
                return;
            }
            Forget(session);
            foreach (ConfMember m in c.Members)
                if (m.Joined && m.Id != agent)
                    DeliverConf(scene, m.Id, AgentsEnvelope(c, agent, false));
        }

        /// <summary>
        /// "call" on a session that is not a private call (llvoicechannel.cpp:623-688): a group the caller
        /// belongs to (group voice) or a conference they have joined. Returns false when it is neither, so
        /// HandleCall answers its usual 403 (VoiceNotAllowed).
        /// </summary>
        private bool HandleGroupOrConferenceCall(Scene scene, UUID caller, UUID session, IOSHttpResponse response)
        {
            if (!CallsOn || session.IsZero()) return false;

            IGroupsModule groups = scene.RequestModuleInterface<IGroupsModule>();
            GroupMembershipData gm = groups?.GetMembershipData(session, caller);
            if (gm != null)
            {
                // Group voice: a member whose active role may talk (GroupPowers.AllowVoiceChat, the viewer's
                // GP_SESSION_VOICE = 1 << 27, roles_constants.h:145). No one is rung - the others press
                // "Join call" in their own group window, which asks here for their own credential.
                if ((gm.GroupPowers & (ulong)GroupPowers.AllowVoiceChat) == 0)
                {
                    response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return true;
                }
                RespondCredentials(response, "group-" + session.ToString(), caller);
                m_log.InfoFormat("{0}[CALL]: {1} joined group voice {2}", logHeader, caller, session);
                return true;
            }

            Conference c = GetConference(scene, caller, session);
            if (c is null) return false;
            if (!AllowCall(caller))
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return true;
            }
            Conference v = Brain(scene, "voice", caller, session, null, out string error, out int _);
            if (v is null)
            {
                m_log.InfoFormat("{0}[CALL]: conference call {1} by {2} refused: {3}", logHeader, session, caller, error);
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return true;
            }
            Remember(v);
            RespondCredentials(response, ConfChannel(session), caller);
            string callerName = AvatarName(scene, caller);
            // Ring everyone else in it. Someone already in the call just accepts it silently
            // (llimview.cpp:4277-4283 "you have already started a call").
            foreach (ConfMember m in v.Members)
                if (m.Id != caller)
                    DeliverConf(scene, m.Id, VoiceEnvelope(caller, callerName, v, m.Id));
            m_log.InfoFormat("{0}[CALL]: {1} started voice in conference {2}", logHeader, caller, session);
            return true;
        }

        /// <summary>
        /// [WOLF CHAT] "wolf channel call" {session-id: channel uuid, group-id} - a Wolf Chat voice room
        /// (sent by WolfViewer's Wolf Chat window). Group members whose active role may talk get a credential for
        /// "wchan-<group>-<channel>"; the room exists only while people are in it (wolfvoice).
        /// </summary>
        private void HandleWolfChannelCall(Scene scene, UUID agent, UUID channel, OSDMap map, IOSHttpResponse response)
        {
            UUID group = map.ContainsKey("group-id") ? map["group-id"].AsUUID() : UUID.Zero;
            IGroupsModule groups = scene.RequestModuleInterface<IGroupsModule>();
            GroupMembershipData gm = (group.IsZero() || channel.IsZero()) ? null : groups?.GetMembershipData(group, agent);
            if (!CallsOn || gm == null || (gm.GroupPowers & (ulong)GroupPowers.AllowVoiceChat) == 0)
            {
                m_log.InfoFormat("{0}[CALL]: refused Wolf Chat room {1}/{2} for {3}", logHeader, group, channel, agent);
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }
            // A Wolf Chat room may be limited to some of the group's roles; only the conference service knows, so it is
            // asked ("wchan": yes only for an
            // existing voice channel of this group that the avatar may see). No answer = no credential: a hidden room
            // never opens because the brain could not be reached (Wolf Chat itself needs the brain anyway).
            if (!WolfChannelAllowed(scene, agent, channel, group))
            {
                m_log.InfoFormat("{0}[CALL]: Wolf Chat room {1}/{2} not open to {3}", logHeader, group, channel, agent);
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }
            RespondCredentials(response, "wchan-" + group.ToString() + "-" + channel.ToString(), agent);
        }

        /// <summary>
        /// [HARDENING 2026-10-23] May `agent` connect to the multiagent room `channel` NOW? Asked on
        /// every ProvisionVoiceAccountRequest for a room (WebRtcVoiceRegionModule.cs), with the same
        /// rules that handed out the credential in the first place, so a credential the viewer kept
        /// (llvoicewebrtc.cpp:1483, :3400) is only as good as the rights behind it:
        ///   group-&lt;g&gt;      member of g, active role with AllowVoiceChat (HandleGroupOrConferenceCall)
        ///   conf-&lt;s&gt;       still in conference s (the brain; GetConference)
        ///   wchan-&lt;g&gt;-&lt;c&gt; as group-, and Wolf Chat room c still open to them (HandleWolfChannelCall)
        ///   &lt;session&gt;      a private call: session XOR agent is another real avatar (HandleCall);
        ///                   wolfvoice puts each such pair in a room of its own (main.rs p2p_pair)
        /// Anything else is not a room this grid hands out. `why` says what failed, for the log.
        /// </summary>
        private bool MayJoinRoom(Scene scene, UUID agent, string channel, out string why)
        {
            why = null;
            IGroupsModule groups = scene.RequestModuleInterface<IGroupsModule>();
            switch (WolfVoiceRules.ParseRoom(channel, out UUID first, out UUID second))
            {
                case WolfVoiceRoomKind.Group:
                case WolfVoiceRoomKind.WolfChat:
                {
                    GroupMembershipData gm = groups?.GetMembershipData(first, agent);
                    if (gm == null)
                        why = "not a member of group " + first;
                    else if ((gm.GroupPowers & (ulong)GroupPowers.AllowVoiceChat) == 0)
                        why = "their role in group " + first + " may not use voice";
                    else if (second != UUID.Zero && !WolfChannelAllowed(scene, agent, second, first))
                        why = "Wolf Chat room " + second + " is not open to them";
                    break;
                }
                case WolfVoiceRoomKind.Conference:
                    if (GetConference(scene, agent, first) is null)
                        why = "not in conference " + first;
                    break;
                case WolfVoiceRoomKind.PrivateCall:
                {
                    UUID other = first ^ agent;
                    if (other.IsZero() || other == agent || !IsKnownAvatar(scene, other))
                        why = "not a party to call " + first;
                    break;
                }
                default:
                    why = "not a voice room of this grid";
                    break;
            }
            return why == null;
        }

        // [2026-10-21] The brain's yes for agent|channel is kept a minute (a viewer asks again on every reconnect and its
        // voice client retries, llvoicechannel.cpp handleError ERROR_NOT_AVAILABLE); a no is not kept, so a role just
        // given works at once. Bounded: cleared when it grows past WCHAN_CACHE_MAX.
        private const int WCHAN_CACHE_SECONDS = 60;
        private const int WCHAN_CACHE_MAX = 2000;
        private static readonly Dictionary<string, DateTime> s_wchanAllowed = new Dictionary<string, DateTime>();

        private bool WolfChannelAllowed(Scene scene, UUID agent, UUID channel, UUID group)
        {
            string key = agent.ToString() + "|" + channel.ToString();
            lock (s_wchanAllowed)
            {
                if (s_wchanAllowed.TryGetValue(key, out DateTime until) && until > DateTime.UtcNow) return true;
            }
            Dictionary<string, object> extra = new Dictionary<string, object> { { "group", group.ToString() } };
            Conference ok = Brain(scene, "wchan", agent, channel, extra, out string error, out int status);
            if (ok is null)
            {
                m_log.InfoFormat("{0}[CALL]: brain refused Wolf Chat room {1} for {2}: {3} ({4})", logHeader, channel, agent, error, status);
                return false;
            }
            lock (s_wchanAllowed)
            {
                if (s_wchanAllowed.Count >= WCHAN_CACHE_MAX) s_wchanAllowed.Clear();
                s_wchanAllowed[key] = DateTime.UtcNow.AddSeconds(WCHAN_CACHE_SECONDS);
            }
            return true;
        }

        private static bool IsTakenSessionId(Scene scene, UUID agent, UUID session, List<UUID> invitees)
        {
            IGroupsModule groups = scene.RequestModuleInterface<IGroupsModule>();
            if (groups?.GetGroupRecord(session) != null) return true;
            if (IsKnownAvatar(scene, session ^ agent)) return true;
            foreach (UUID id in invitees)
                if (IsKnownAvatar(scene, session ^ id)) return true;
            return false;
        }

        /// <summary>A group's own chat session has the group's id (llimview.cpp:2595-2598) - the groups
        /// module's business, not a conference.</summary>
        private static bool IsGroupSession(Scene scene, UUID agent, UUID session)
        {
            IGroupsModule groups = scene.RequestModuleInterface<IGroupsModule>();
            return groups?.GetMembershipData(session, agent) != null;
        }

        private static string ConfChannel(UUID session)
        {
            return "conf-" + session.ToString();
        }

        /// <summary>The "call" answer the viewer reads: result["voice_credentials"]{channel_uri,
        /// channel_credentials} (llvoicechannel.cpp:687), voice_server_type picks the WebRTC module
        /// (llvoiceclient.cpp:615-629).</summary>
        private void RespondCredentials(IOSHttpResponse response, string channel, UUID avatar)
        {
            OSDMap creds = new OSDMap
            {
                { "channel_uri", OSD.FromString(channel) },
                { "channel_credentials", OSD.FromString(CallCredentials(channel, avatar)) },
                { "voice_server_type", OSD.FromString("webrtc") }
            };
            response.StatusCode = (int)HttpStatusCode.OK;
            response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(new OSDMap { { "voice_credentials", creds } });
        }

        // ------------------------------------------------------------------------------ typed text

        /// <summary>
        /// UDP ImprovedInstantMessage from a root agent here. Conference text is dialog 17 with ID = the
        /// session (llimview.cpp:2202-2277), and the viewer does NOT echo it locally (:2363-2369), so the
        /// sender gets it back too. Dialog 18 = leaving. Core InstantMessageModule drops both
        /// (InstantMessageModule.cs:151-160); GroupsMessagingModule acts only on groups. Runs off the
        /// UDP thread because it may ask the brain.
        /// </summary>
        private void OnClientConferenceMessage(IClientAPI client, GridInstantMessage im)
        {
            if (im.dialog != IM_SESSION_SEND && im.dialog != IM_SESSION_LEAVE) return;
            if (m_SimSecret.Length == 0 || im.fromGroup) return;
            UUID session = new UUID(im.imSessionID);
            UUID from = client.AgentId;
            if (session.IsZero()) return;
            Scene scene = client.Scene as Scene;
            if (scene is null) return;
            // A group's own session id is the group id (llimview.cpp:2595-2598): not ours.
            IGroupsModule groups = scene.RequestModuleInterface<IGroupsModule>();
            lock (m_Conferences)
            {
                if (m_NotConferences.TryGetValue(session, out DateTime when) && DateTime.UtcNow - when < NOT_CONF_CACHE)
                    return;
            }
            byte dialog = im.dialog;
            string message = im.message ?? string.Empty;
            Util.FireAndForget(delegate
            {
                try
                {
                    if (groups?.GetMembershipData(session, from) != null) return;
                    if (dialog == IM_SESSION_LEAVE)
                    {
                        HandleLeave(scene, from, session);
                        return;
                    }
                    Conference c = GetConference(scene, from, session);
                    if (c is null) return;
                    ConfMember me = c.Find(from);
                    if (me != null && !me.Joined)
                    {
                        // Typing in it is accepting it.
                        Conference a = Brain(scene, "accept", from, session, null, out string _, out int _);
                        if (a is null) return;
                        Remember(a);
                        c = a;
                    }
                    string fromName = AvatarName(scene, from);
                    foreach (ConfMember m in c.Members)
                    {
                        DeliverConf(scene, m.Id, m.Joined
                                ? TextEnvelope("text", from, fromName, c, message)
                                : TextEnvelope("invite", from, fromName, c, message));
                    }
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("{0}[CONF]: message from {1} in {2} failed: {3}", logHeader, from, session, e.Message);
                }
            }, null, "WolfConferenceMessage");
        }

        /// <summary>A line from no one (system) in the conference window of one person - for the failures
        /// of "invite", whose HTTP answer the viewer ignores. A null sender is how the viewer itself posts
        /// system lines into an IM session (llvoicechannel.cpp:597 addMessage(LLUUID::null, SYSTEM_FROM)).</summary>
        private void TellSystem(Scene scene, UUID to, UUID session, string text)
        {
            Conference c;
            lock (m_Conferences) m_Conferences.TryGetValue(session, out c);
            OSDMap p = new OSDMap
            {
                { "k", OSD.FromString("text") },
                { "session", OSD.FromUUID(session) },
                { "name", OSD.FromString(c?.Name ?? string.Empty) },
                { "from", OSD.FromUUID(UUID.Zero) },
                { "from_name", OSD.FromString("Wolf Territories") },
                { "message", OSD.FromString(text) }
            };
            DeliverConf(scene, to, p);
        }

        // ------------------------------------------------------------------------------ delivery

        private static OSDMap TextEnvelope(string kind, UUID from, string fromName, Conference c, string message)
        {
            return new OSDMap
            {
                { "k", OSD.FromString(kind) },
                { "session", OSD.FromUUID(c.Id) },
                { "name", OSD.FromString(c.Name) },
                { "from", OSD.FromUUID(from) },
                { "from_name", OSD.FromString(fromName) },
                { "message", OSD.FromString(message) }
            };
        }

        private OSDMap VoiceEnvelope(UUID from, string fromName, Conference c, UUID to)
        {
            string channel = ConfChannel(c.Id);
            return new OSDMap
            {
                { "k", OSD.FromString("voice") },
                { "session", OSD.FromUUID(c.Id) },
                { "name", OSD.FromString(c.Name) },
                { "from", OSD.FromUUID(from) },
                { "from_name", OSD.FromString(fromName) },
                { "channel", OSD.FromString(channel) },
                { "credentials", OSD.FromString(CallCredentials(channel, to)) }
            };
        }

        private static OSDMap AgentsEnvelope(Conference c, UUID who, bool enter)
        {
            return new OSDMap
            {
                { "k", OSD.FromString("agents") },
                { "session", OSD.FromUUID(c.Id) },
                { "agent", OSD.FromUUID(who) },
                { "enter", OSD.FromBoolean(enter) }
            };
        }

        /// <summary>The joined members as ENTER updates, to one person (their speaker list).</summary>
        private void SendAgentList(Scene scene, UUID to, UUID session, Conference c, bool joinedOnly)
        {
            IEventQueue eq = scene.RequestModuleInterface<IEventQueue>();
            if (eq is null) return;
            List<GroupChatListAgentUpdateData> updates = new List<GroupChatListAgentUpdateData>();
            foreach (ConfMember m in c.Members)
                if (m.Joined || !joinedOnly)
                    updates.Add(new GroupChatListAgentUpdateData(m.Id, true, false, false, true));
            if (updates.Count > 0)
                eq.ChatterBoxSessionAgentListUpdates(session, to, updates);
        }

        /// <summary>
        /// Sends one conference event to one person, wherever they are: applied here when they are a root
        /// agent on this simulator, otherwise as a grid IM (dialog WOLF_CALL_DIALOG) whose bucket is
        /// WOLF_CONF_MARK + signature + "\n" + JSON, applied by their region's copy of this module
        /// (OnIncomingConference). The signature (sim secret, bound to the recipient) stops anyone else
        /// from injecting conference text or invitations through the grid IM service.
        /// </summary>
        private void DeliverConf(Scene scene, UUID to, OSDMap payload)
        {
            if (ApplyConfLocally(to, payload)) return;
            // [review] signed with the time, so a captured envelope cannot be replayed later (OnIncomingConference)
            payload["ts"] = OSD.FromString(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            IMessageTransferModule tm = scene.RequestModuleInterface<IMessageTransferModule>();
            if (tm is null) return;
            string json = OSDParser.SerializeJsonString(payload);
            string sig = HexHmac(m_SimSecret, "wolfconf1:" + to.ToString() + ":" + json);
            UUID from = payload.ContainsKey("from") ? payload["from"].AsUUID() : UUID.Zero;
            GridInstantMessage gim = new GridInstantMessage(scene, from, payload.ContainsKey("from_name") ? payload["from_name"].AsString() : string.Empty,
                    to, WOLF_CALL_DIALOG, false, string.Empty, payload["session"].AsUUID(), false, Vector3.Zero,
                    Util.UTF8.GetBytes(WOLF_CONF_MARK + sig + "\n" + json), true);
            tm.SendInstantMessage(gim, delegate (bool success)
            {
                if (!success)
                    m_log.DebugFormat("{0}[CONF]: {1} could not be reached for {2}", logHeader, to, payload["k"].AsString());
            });
        }

        /// <summary>A conference envelope from another region (DeliverConf). Fires only where the recipient
        /// is a root agent (MessageTransferModule.cs:407-415).</summary>
        private void OnIncomingConference(GridInstantMessage msg)
        {
            if (msg.dialog != WOLF_CALL_DIALOG || msg.binaryBucket is null || m_SimSecret.Length == 0) return;
            string text = Util.UTF8.GetString(msg.binaryBucket);
            if (!text.StartsWith(WOLF_CONF_MARK, StringComparison.Ordinal)) return;
            try
            {
                string rest = text.Substring(WOLF_CONF_MARK.Length);
                int nl = rest.IndexOf('\n');
                if (nl <= 0) return;
                string sig = rest.Substring(0, nl);
                string json = rest.Substring(nl + 1);
                UUID to = new UUID(msg.toAgentID);
                string want = HexHmac(m_SimSecret, "wolfconf1:" + to.ToString() + ":" + json);
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sig), Encoding.ASCII.GetBytes(want)))
                {
                    m_log.DebugFormat("{0}[CONF]: dropped a conference message for {1} with a bad signature", logHeader, to);
                    return;
                }
                if (OSDParser.DeserializeJson(json) is not OSDMap p) return;
                long sent = 0;
                if (!p.ContainsKey("ts") || !long.TryParse(p["ts"].AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sent)
                    || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sent) > ENVELOPE_MAX_AGE)
                {
                    m_log.DebugFormat("{0}[CONF]: dropped a stale conference message for {1}", logHeader, to);
                    return;
                }
                if (!ApplyConfLocally(to, p))
                    m_log.DebugFormat("{0}[CONF]: conference message for {1} arrived but they are not here", logHeader, to);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("{0}[CONF]: unreadable conference message: {1}", logHeader, e.Message);
            }
        }

        /// <summary>Applies a conference event to `to` when they are a root agent on any region of this
        /// simulator. JSON carried uuids as strings, so every field is read with As*().</summary>
        private bool ApplyConfLocally(UUID to, OSDMap p)
        {
            Scene[] scenes;
            lock (m_Scenes) scenes = m_Scenes.ToArray();
            foreach (Scene s in scenes)
            {
                ScenePresence sp = s.GetScenePresence(to);
                if (sp is null || sp.IsChildAgent) continue;
                IEventQueue eq = s.RequestModuleInterface<IEventQueue>();
                UUID session = p["session"].AsUUID();
                string name = p.ContainsKey("name") ? p["name"].AsString() : string.Empty;
                UUID from = p.ContainsKey("from") ? p["from"].AsUUID() : UUID.Zero;
                string fromName = p.ContainsKey("from_name") ? p["from_name"].AsString() : string.Empty;
                // Session name as the bucket, NUL-terminated as GroupsMessagingModule sends it
                // (Utils.StringToBytes); the viewer reads it with ll_safe_string (llimprocessing.cpp:1939).
                byte[] bucket = Utils.StringToBytes(name);
                switch (p["k"].AsString())
                {
                    case "text":
                        sp.ControllingClient.SendInstantMessage(new GridInstantMessage(s, from, fromName, to, IM_SESSION_SEND,
                                false, p["message"].AsString(), session, false, Vector3.Zero, bucket, true));
                        return true;
                    case "invite":
                        // The text form of ChatterBoxInvitation (llimview.cpp:5122-5267): message_params.id is the
                        // session id, the bucket its name; the viewer opens the window and accepts by itself.
                        // Built exactly as GroupsMessagingModule.cs:608-628 builds a group's.
                        eq?.ChatterboxInvitation(session, name, from, p["message"].AsString(), to, fromName, IM_SESSION_SEND,
                                (uint)Util.UnixTimeSinceEpoch(), false, 0, Vector3.Zero, 1, session, false, bucket);
                        return true;
                    case "voice":
                    {
                        // As HandleCall's P2P ring, with invitation_type CONFERENCE_SESSION: the viewer asks
                        // "VoiceInviteAdHoc" (llimview.cpp:4236-4241, 5269-5287) and joins with this map.
                        OSDMap voice = new OSDMap
                        {
                            { "channel_uri", OSD.FromString(p["channel"].AsString()) },
                            { "channel_credentials", OSD.FromString(p["credentials"].AsString()) },
                            { "invitation_type", OSD.FromInteger(CONFERENCE_SESSION) },
                            { "voice_server_type", OSD.FromString("webrtc") }
                        };
                        OSDMap body = new OSDMap
                        {
                            { "session_id", OSD.FromUUID(session) },
                            { "session_name", OSD.FromString(name) },
                            { "from_id", OSD.FromUUID(from) },
                            { "from_name", OSD.FromString(fromName) },
                            { "voice", voice }
                        };
                        // The OSD overload, as EnqueueCallInvitation (an OSDMap also converts to byte[]).
                        OSD evt = new OSDMap { { "message", OSD.FromString("ChatterBoxInvitation") }, { "body", body } };
                        eq?.Enqueue(evt, to);
                        return true;
                    }
                    case "agents":
                        eq?.ChatterBoxSessionAgentListUpdates(session, to, new List<GroupChatListAgentUpdateData>
                        {
                            new GroupChatListAgentUpdateData(p["agent"].AsUUID(), true, false, false, p["enter"].AsBoolean())
                        });
                        return true;
                    default:
                        return true;
                }
            }
            return false;
        }
    }
}
