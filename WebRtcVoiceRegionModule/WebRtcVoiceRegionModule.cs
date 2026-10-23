// Copyright 2024 Robert Adams (misterblue@misterblue.com)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Collections.Generic;
using System.Reflection;

using Mono.Addins;

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

using log4net;
using Nini.Config;

[assembly: Addin("WebRtcVoiceRegionModule", "1.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace WebRtcVoice
{
    /// <summary>
    /// This module provides the WebRTC voice interface for viewer clients..
    /// 
    /// In particular, it provides the following capabilities:
    ///      ProvisionVoiceAccountRequest, VoiceSignalingRequest, and ParcelVoiceInfoRequest.    
    /// which are the user interface to the voice service.
    /// 
    /// Initially, when the user connects to the region, the region feature "VoiceServiceType" is
    /// set to "webrtc" and the capabilities that support voice are enabled.
    /// The capabilities then pass the user request information to the IWebRtcVoiceService interface
    /// that has been registered for the reqion.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionVoiceModule")]
    public partial class WebRtcVoiceRegionModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string logHeader = "[REGION WEBRTC VOICE]";

        private bool _MessageDetails = false;

        // Control info
        private static bool m_Enabled = false;

        private readonly Dictionary<string, string> m_UUIDName = new Dictionary<string, string>();
        private Dictionary<string, string> m_ParcelAddress = new Dictionary<string, string>();

        private IConfig m_Config;

        // [FIRE-36421] STUN servers advertised to the viewer via SimulatorFeatures["stun-servers"].
        // Firestorm 7.2.4+ OpenSim build reads these; an EMPTY list makes libwebrtc reject the
        // peer connection ("Empty uri") and WebRTC voice never starts. Default to a public STUN
        // so voice works out of the box; override with StunServers in the [WebRtcVoice] ini.
        private string m_StunServers = "stun:stun.l.google.com:19302,stun:stun1.l.google.com:19302";

        // [WOLF CALLS 2026-10-19] Private (P2P) voice calls. The relay key wolfvoice already requires from this
        // grid (WebRtcVoiceServiceConnector.cs:40-54, same file and default) signs each call
        // credential; wolfvoice re-computes it (main.rs call_credentials) before it opens the room.
        // [HARDENING 2026-10-23] Two keys: the relay key ([WebRtcVoice] RpcKeyFile) authenticates
        // this region to wolfvoice; room credentials are signed with the credential key
        // ([WebRtcVoice] CredKeyFile, default /etc/opensim/voice_cred_key, wolfvoice
        // /etc/wolfvoice/cred_key). Until that file exists the relay key signs, exactly as before,
        // so the order of deployment cannot break voice. Both files are re-read when they change.
        private WolfVoiceKeyFile m_RpcKeyFile = new WolfVoiceKeyFile(string.Empty);
        private WolfVoiceKeyFile m_CredKeyFile = new WolfVoiceKeyFile(string.Empty);
        // Accept a credential signed with the RELAY key as well as the credential key (a call
        // invitation from a region not yet on this module, during the change-over).
        private bool m_AcceptLegacyCredentials = true;
        private string RpcKey { get { return m_RpcKeyFile.Value; } }
        private string CredKey
        {
            get
            {
                string c = m_CredKeyFile.Value;
                return c.Length > 0 ? c : m_RpcKeyFile.Value;
            }
        }
        /// <summary>Calls, group voice and conference voice need a key to sign credentials with.</summary>
        private bool CallsOn { get { return CredKey.Length > 0; } }
        private readonly List<Scene> m_Scenes = new List<Scene>();
        // The invitation travels to the callee's region as a grid IM with this dialog: a value
        // InstantMessageDialog does not define (OpenMetaverse enum has 0,1,3..26,28..42), so the IM
        // module drops it (InstantMessageModule.cs:151-160) and only this module acts on it.
        private const byte WOLF_CALL_DIALOG = 2;
        private const string WOLF_CALL_MARK = "wolfcall1\n";
        // Calls one avatar may start per minute (ringing is a notification on someone else's screen).
        private const int CALLS_PER_MINUTE = 6;
        private readonly Dictionary<UUID, Queue<DateTime>> m_CallTimes = new Dictionary<UUID, Queue<DateTime>>();

        // ISharedRegionModule.Initialize
        public void Initialise(IConfigSource config)
        {
            m_Config = config.Configs["WebRtcVoice"];
            if (m_Config is not null)
            {
                m_Enabled = m_Config.GetBoolean("Enabled", false);
                if (m_Enabled)
                {
                    _MessageDetails = m_Config.GetBoolean("MessageDetails", false);
                    m_StunServers = m_Config.GetString("StunServers", m_StunServers);

                    // [WOLF CALLS 2026-10-19] Same setting and default as the connector.
                    m_RpcKeyFile = new WolfVoiceKeyFile(m_Config.GetString("RpcKeyFile", "/etc/opensim/voice_rpc_key"));
                    // [HARDENING 2026-10-23] The separate credential key (see m_CredKeyFile).
                    m_CredKeyFile = new WolfVoiceKeyFile(m_Config.GetString("CredKeyFile", "/etc/opensim/voice_cred_key"));
                    m_AcceptLegacyCredentials = m_Config.GetBoolean("AcceptLegacyCallCredentials", true);
                    if (!CallsOn)
                        m_log.Warn($"{logHeader}: no voice relay key - private voice calls are off");

                    // [HARDENING 2026-10-23] Avatar positions to wolfvoice (WolfPositionFeed.cs).
                    InitPositionFeed(m_Config);

                    // [WOLF GROUP CALLS 2026-10-20] conference chats, group voice, Wolf Chat rooms (WolfConference.cs)
                    InitConference(config);

                    m_log.Info($"{logHeader}: enabled");
                }
            }
        }

        // ISharedRegionModule.PostInitialize
        public void PostInitialise()
        {
        }

        // ISharedRegionModule.AddRegion
        public void AddRegion(Scene scene)
        {
            if (m_Enabled)
            {
                // Get the hook that means Capbibilities are being registered
                scene.EventManager.OnRegisterCaps += (UUID agentID, Caps caps) =>
                    {
                        OnRegisterCaps(scene, agentID, caps);
                    };

                // [WOLF CALLS 2026-10-19] Call invitations arriving from other regions.
                lock (m_Scenes) m_Scenes.Add(scene);
                scene.EventManager.OnIncomingInstantMessage += OnIncomingCallInvitation;

                // [WOLF GROUP CALLS 2026-10-20] conference text typed here, and conference deliveries from
                // other regions (WolfConference.cs).
                scene.EventManager.OnIncomingInstantMessage += OnIncomingConference;
                scene.EventManager.OnMakeRootAgent += OnMakeRootAgentConf;
                scene.EventManager.OnMakeChildAgent += OnMakeChildAgentConf;

                // [HARDENING 2026-10-23] A new root agent here is asked about at the next feed tick.
                scene.EventManager.OnMakeRootAgent += OnMakeRootAgentFeed;
                StartPositionFeed();
            }
        }

        // ISharedRegionModule.RemoveRegion
        public void RemoveRegion(Scene scene)
        {
            lock (m_Scenes) m_Scenes.Remove(scene);
            scene.EventManager.OnIncomingInstantMessage -= OnIncomingCallInvitation;
            scene.EventManager.OnIncomingInstantMessage -= OnIncomingConference;
            scene.EventManager.OnMakeRootAgent -= OnMakeRootAgentConf;
            scene.EventManager.OnMakeChildAgent -= OnMakeChildAgentConf;
            scene.EventManager.OnMakeRootAgent -= OnMakeRootAgentFeed;
            var sfm = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
            sfm.OnSimulatorFeaturesRequest -= OnSimulatorFeatureRequestHandler;
        }

        // ISharedRegionModule.RegionLoaded
        public void RegionLoaded(Scene scene)
        {
            if (m_Enabled)
            {
                // Register for the region feature reporting so we can add 'webrtc'
                var sfm = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
                sfm.OnSimulatorFeaturesRequest += OnSimulatorFeatureRequestHandler;
                m_log.DebugFormat("{0}: registering OnSimulatorFeatureRequestHandler", logHeader);
            }
        }

        // ISharedRegionModule.Close
        public void Close()
        {
            StopPositionFeed();
        }

        // ISharedRegionModule.Name
        public string Name
        {
            get { return "RegionVoiceModule"; }
        }

        // ISharedRegionModule.ReplaceableInterface
        public Type ReplaceableInterface
        {
            get { return null; }
        }

        // Called when the simulator features are being constructed.
        // Add the flag that says we support WebRtc voice.
        private void OnSimulatorFeatureRequestHandler(UUID agentID, ref OSDMap features)
        {
            m_log.DebugFormat("{0}: setting VoiceServerType=webrtc for agent {1}", logHeader, agentID);
            features["VoiceServerType"] = "webrtc";
            if (!String.IsNullOrEmpty(m_StunServers))
                features["stun-servers"] = m_StunServers;
        }

        // <summary>
        // OnRegisterCaps is invoked via the scene.EventManager
        // everytime OpenSim hands out capabilities to a client
        // (login, region crossing). We contribute three capabilities to
        // the set of capabilities handed back to the client:
        // ProvisionVoiceAccountRequest, VoiceSignalingRequest, and ParcelVoiceInfoRequest.
        //
        // ProvisionVoiceAccountRequest allows the client to obtain
        // voice communication information the the avater.
        //
        // VoiceSignalingRequest: Used for trickling ICE candidates.
        //
        // ParcelVoiceInfoRequest is invoked whenever the client
        // changes from one region or parcel to another.
        //
        // Note that OnRegisterCaps is called here via a closure
        // delegate containing the scene of the respective region (see
        // Initialise()).
        // </summary>
        public void OnRegisterCaps(Scene scene, UUID agentID, Caps caps)
        {
            m_log.DebugFormat(
                "{0}: OnRegisterCaps() called with agentID {1} caps {2} in scene {3}",
                logHeader, agentID, caps, scene.RegionInfo.RegionName);

            caps.RegisterSimpleHandler("ProvisionVoiceAccountRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                    {
                        ProvisionVoiceAccountRequest(httpRequest, httpResponse, agentID, scene);
                    }));

            caps.RegisterSimpleHandler("VoiceSignalingRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                    {
                        VoiceSignalingRequest(httpRequest, httpResponse, agentID, scene);
                    }));

            caps.RegisterSimpleHandler("ParcelVoiceInfoRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                    {
                        ParcelVoiceInfoRequest(httpRequest, httpResponse, agentID, scene);
                    }));

            // [FIX 2026-08-23] ChatSessionRequest is MANDATORY once this module is enabled, and
            // NOT for voice reasons: without it, plain TEXT peer-to-peer IMs break for every
            // Firestorm/LL-derived viewer on the region. See ChatSessionRequest() below for the
            // full chain with source citations.
            caps.RegisterSimpleHandler("ChatSessionRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                    {
                        ChatSessionRequest(httpRequest, httpResponse, agentID, scene);
                    }));

        }

        /// <summary>
        /// Callback for a client request for Voice Account Details
        /// </summary>
        /// <param name="scene">current scene object of the client</param>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="agentID"></param>
        /// <param name="caps"></param>
        /// <returns></returns>
        public void ProvisionVoiceAccountRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
        {
            if(request.HttpMethod != "POST")
            {
                m_log.DebugFormat("[{0}][ProvisionVoice]: Not a POST request. Agent={1}", logHeader, agentID.ToString());
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // Deserialize the request. Convert the LLSDXml to OSD for our use
            OSDMap map = null;
            using (Stream inputStream = request.InputStream)
            {
                if (inputStream.Length > 0)
                {
                    OSD tmp = OSDParser.DeserializeLLSDXml(inputStream);
                    if (_MessageDetails) m_log.DebugFormat("{0}[ProvisionVoice]: Request: {1}", logHeader, tmp.ToString());
                    map = tmp as OSDMap;
                }
            }

            if (map is null)
            {
                m_log.ErrorFormat("{0}[ProvisionVoice]: No request data found. Agent={1}", logHeader, agentID.ToString());
                response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            // Get the voice service. If it doesn't exist, return an error.
            IWebRtcVoiceService voiceService = scene.RequestModuleInterface<IWebRtcVoiceService>();
            if (voiceService is null)
            {
                m_log.ErrorFormat("{0}[ProvisionVoice]: avatar \"{1}\": no voice service", logHeader, agentID);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // Make sure the request is for WebRtc voice
            if (map.TryGetValue("voice_server_type", out OSD vstosd))
            {
                if (vstosd is OSDString vst && !((string)vst).Equals("webrtc", StringComparison.OrdinalIgnoreCase))
                {
                    m_log.WarnFormat("{0}[ProvisionVoice]: voice_server_type is not 'webrtc'. Request: {1}", logHeader, map.ToString());
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                }
            }

            // [Wolf 2026-10-09] The REGION decides which parcel's room a local (spatial) session is
            // in, from where the avatar actually stands - not the viewer, which could otherwise
            // name any parcel and listen in on it (security scan M4). A parcel with its own voice
            // channel is its own room; one using the estate channel is the region's room, exactly
            // the rule Firestorm uses to decide whether to send parcel_local_id at all.
            string channelType = map.ContainsKey("channel_type") ? map["channel_type"].AsString() : "local";
            // [HARDENING 2026-10-23] A logout only ends a session (llvoicewebrtc.cpp:2731-2735) and
            // is never refused; everything below is about opening or moving one.
            bool logout = map.ContainsKey("logout") && map["logout"].AsBoolean();
            if (!logout && channelType == "local")
            {
                bool root = scene.TryGetScenePresence(agentID, out ScenePresence presence) && !presence.IsChildAgent;
                // [HARDENING 2026-10-23] Voice switched off is enforced here, with the checks
                // ParcelVoiceInfoRequest uses to tell the viewer (below): the estate's AllowVoice,
                // then - unless the estate is TaxFree - the parcel's AllowVoiceChat. Before this they
                // were only advice to the viewer; a modified one could connect anyway.
                if (!scene.RegionInfo.EstateSettings.AllowVoice)
                {
                    RefuseProvision(response, agentID, scene, "voice is off in this estate");
                    return;
                }
                if (root)
                {
                    ILandObject land = scene.LandChannel?.GetLandObject(presence.AbsolutePosition.X, presence.AbsolutePosition.Y);
                    if (land != null && !scene.RegionInfo.EstateSettings.TaxFree && (land.LandData.Flags & (uint)ParcelFlags.AllowVoiceChat) == 0)
                    {
                        RefuseProvision(response, agentID, scene, "voice is off on this parcel");
                        return;
                    }
                    // As the upstream module does for the parcel the viewer names: an avatar banned
                    // from, or not on the access list of, the parcel it stands on gets no parcel voice.
                    if (land != null && (land.IsBannedFromLand(agentID) || land.IsRestrictedFromLand(agentID)))
                    {
                        RefuseProvision(response, agentID, scene, "not allowed on this parcel");
                        return;
                    }
                    if (land != null && (land.LandData.Flags & (uint)ParcelFlags.UseEstateVoiceChan) == 0)
                        map["parcel_local_id"] = OSD.FromInteger(land.LandData.LocalID);
                    else
                        map.Remove("parcel_local_id");
                }
                else
                {
                    // [HARDENING 2026-10-23] A child agent (a neighbour-region connection) or no
                    // presence at all: always the region's estate room. This region does not know
                    // which of its parcels such an avatar would be "on", and stock Firestorm never
                    // asks a neighbour for parcel voice - its neighbour connections are made with
                    // INVALID_PARCEL_ID (llvoicewebrtc.cpp:2244), and parcel_local_id is only sent
                    // when valid (:2795-2798). So nothing honest is refused, and a forged parcel id
                    // can no longer pick a private parcel's room from next door.
                    map.Remove("parcel_local_id");
                }
            }

            // [HARDENING 2026-10-23] A call, group or conference room is joined with the
            // credentials the viewer was given when the call started, and stock Firestorm keeps
            // those for the whole call and sends them again on every reconnect
            // (llvoicewebrtc.cpp:1483, :3400) - so they cannot expire on a timer. Instead the right
            // to be in the room is checked again here, on every connect: someone removed from the
            // group, the role, the conference or the Wolf Chat room is refused from their next one.
            if (!logout && channelType == "multiagent" && map.ContainsKey("jsep"))
            {
                string channel = map.ContainsKey("channel") ? map["channel"].AsString() : String.Empty;
                if (!MayJoinRoom(scene, agentID, channel, out string why))
                {
                    RefuseProvision(response, agentID, scene, why);
                    return;
                }
            }

            // The checks passed. Send the request to the voice service.
            OSDMap resp = voiceService.ProvisionVoiceAccountRequest(map, agentID, scene.RegionInfo.RegionID).Result;

            // [Wolf 2026-10-09] No answer (an unknown session, a refused move, the voice server
            // down): say so instead of throwing on resp.ToString() below. The viewer treats a
            // non-200 as a failed provision - a Wolf viewer's refused move falls back to a new
            // connection.
            if (resp is null)
            {
                m_log.WarnFormat("{0}[ProvisionVoice]: no answer from the voice service for {1}", logHeader, agentID);
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                return;
            }

            if (_MessageDetails) m_log.DebugFormat("{0}[ProvisionVoice]: response: {1}", logHeader, resp.ToString());

            // [HARDENING 2026-10-23] wolfvoice's "room is full" (admission.rs Refusal::RoomFull) is
            // answered 409, which the viewer reports as ERROR_CHANNEL_FULL
            // (llvoicewebrtc.cpp:2822-2824, :3422-3424). Other answers go back as before.
            if (resp.ContainsKey("error") && resp["error"].AsString().Contains("voice room is full"))
            {
                m_log.InfoFormat("{0}[ProvisionVoice]: {1}'s room is full", logHeader, agentID);
                response.StatusCode = (int)HttpStatusCode.Conflict;
                return;
            }
            // A session opened through this region: ask wolfvoice about the avatar at the next feed tick.
            if (!logout && channelType == "local" && resp.ContainsKey("viewer_session") && !resp.ContainsKey("error"))
                FeedNote(agentID);

            // Errors from the voice service are returned in the map, as before (the viewer finds no
            // viewer_session/jsep in it and retries, llvoicewebrtc.cpp:2847-2861).

            // Convert the OSD to LLSDXml for the response
            string xmlResp = OSDParser.SerializeLLSDXmlString(resp);

            response.StatusCode = (int)HttpStatusCode.OK;
            response.RawBuffer = Util.UTF8.GetBytes(xmlResp);
            return;
        }

        public void VoiceSignalingRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
        {
            if(request.HttpMethod != "POST")
            {
                m_log.ErrorFormat("[{0}][VoiceSignaling]: Not a POST request. Agent={1}", logHeader, agentID.ToString());
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // Deserialize the request. Convert the LLSDXml to OSD for our use
            OSDMap map = null;
            using (Stream inputStream = request.InputStream)
            {
                if (inputStream.Length > 0)
                {
                    OSD tmp = OSDParser.DeserializeLLSDXml(inputStream);
                    if (_MessageDetails) m_log.DebugFormat("{0}[VoiceSignalingRequest]: Request: {1}", logHeader, tmp.ToString());

                    if (tmp is OSDMap)
                    {
                        map = (OSDMap)tmp;
                    }
                }
            }
            if (map is null)
            {
                m_log.ErrorFormat("{0}[VoiceSignalingRequest]: No request data found. Agent={1}", logHeader, agentID.ToString());
                response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            // Make sure the request is for WebRTC voice
            if (map.TryGetValue("voice_server_type", out OSD vstosd))
            {
                if (vstosd is OSDString vst && !((string)vst).Equals("webrtc", StringComparison.OrdinalIgnoreCase))
                {
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                }
            }

            IWebRtcVoiceService voiceService = scene.RequestModuleInterface<IWebRtcVoiceService>();
            if (voiceService is null)
            {
                m_log.ErrorFormat("{0}[VoiceSignalingRequest]: avatar \"{1}\": no voice service", logHeader, agentID);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            OSDMap resp = voiceService.VoiceSignalingRequest(map, agentID, scene.RegionInfo.RegionID).Result;
            if (_MessageDetails) m_log.DebugFormat("{0}[VoiceSignalingRequest]: Response: {1}", logHeader, resp);

            // TODO: check for errors and package the response

            response.StatusCode = (int)HttpStatusCode.OK;
            response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
            return;
        }

        /// <summary>[HARDENING 2026-10-23] A provision refused by this region: 401, which the viewer
        /// reports as ERROR_CHANNEL_LOCKED (llvoicewebrtc.cpp:2825-2826, :3425-3426) and ends that
        /// connection attempt (VOICE_STATE_SESSION_EXIT); nothing is sent to the voice server.</summary>
        private void RefuseProvision(IOSHttpResponse response, UUID agentID, Scene scene, string why)
        {
            m_log.InfoFormat("{0}[ProvisionVoice]: refused {1} in {2}: {3}", logHeader, agentID, scene.RegionInfo.RegionName, why);
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
        }

        // NOTE NOTE!! This is code from the FreeSwitch module. It is not clear if this is correct for WebRtc.
        /// <summary>
        /// Callback for a client request for ParcelVoiceInfo
        /// </summary>
        /// <param name="scene">current scene object of the client</param>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="agentID"></param>
        /// <param name="caps"></param>
        /// <returns></returns>
        public void ParcelVoiceInfoRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            response.StatusCode = (int)HttpStatusCode.OK;

            m_log.DebugFormat(
                "{0}[PARCELVOICE]: ParcelVoiceInfoRequest() on {1} for {2}",
                logHeader, scene.RegionInfo.RegionName, agentID);

            ScenePresence avatar = scene.GetScenePresence(agentID);
            if(avatar == null)
            {
                response.RawBuffer = Util.UTF8.GetBytes("<llsd>undef</llsd>");
                return;
            }

            string avatarName = avatar.Name;

            // - check whether we have a region channel in our cache
            // - if not:
            //       create it and cache it
            // - send it to the client
            // - send channel_uri: as "sip:regionID@m_sipDomain"
            try
            {
                string channelUri;

                if (null == scene.LandChannel)
                {
                    m_log.ErrorFormat("region \"{0}\": avatar \"{1}\": land data not yet available",
                                                      scene.RegionInfo.RegionName, avatarName);
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd>undef</llsd>");
                    return;
                }

                // get channel_uri: check first whether estate
                // settings allow voice, then whether parcel allows
                // voice, if all do retrieve or obtain the parcel
                // voice channel
                LandData land = scene.GetLandData(avatar.AbsolutePosition);

                // TODO: EstateSettings don't seem to get propagated...
                 if (!scene.RegionInfo.EstateSettings.AllowVoice)
                 {
                     m_log.DebugFormat("{0}[PARCELVOICE]: region \"{1}\": voice not enabled in estate settings",
                                       logHeader, scene.RegionInfo.RegionName);
                    channelUri = String.Empty;
                }
                else

                if (!scene.RegionInfo.EstateSettings.TaxFree && (land.Flags & (uint)ParcelFlags.AllowVoiceChat) == 0)
                {
                    channelUri = String.Empty;
                }
                else
                {
                    channelUri = ChannelUri(scene, land);
                }

                // fast foward encode
                osUTF8 lsl = LLSDxmlEncode2.Start(512);
                LLSDxmlEncode2.AddMap(lsl);
                LLSDxmlEncode2.AddElem("parcel_local_id", land.LocalID, lsl);
                LLSDxmlEncode2.AddElem("region_name", scene.Name, lsl);
                LLSDxmlEncode2.AddMap("voice_credentials", lsl);
                LLSDxmlEncode2.AddElem("channel_uri", channelUri, lsl);
                //LLSDxmlEncode2.AddElem("channel_credentials", channel_credentials, lsl);
                LLSDxmlEncode2.AddEndMap(lsl);
                LLSDxmlEncode2.AddEndMap(lsl);

                response.RawBuffer= LLSDxmlEncode2.EndToBytes(lsl);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0}[PARCELVOICE]: region \"{1}\": avatar \"{2}\": {3}, retry later",
                                  logHeader, scene.RegionInfo.RegionName, avatarName, e.Message);
                m_log.DebugFormat("{0}[PARCELVOICE]: region \"{1}\": avatar \"{2}\": {3} failed",
                                  logHeader, scene.RegionInfo.RegionName, avatarName, e.ToString());

                response.RawBuffer = Util.UTF8.GetBytes("<llsd>undef</llsd>");
            }
        }

        // Firestorm/LL EMultiAgentChatSessionType (llimview.cpp:115-121). The viewer's own
        // comment there states the mechanism this handler exists to serve: "webrtc uses the
        // multiagent chat mechanism for p2p calls, instead of relying on vivox calling."
        private const int CONFERENCE_SESSION = 1;
        private const int P2P_CHAT_SESSION = 2;

        /// <summary>
        /// ChatSessionRequest capability.
        ///
        /// WHY THIS EXISTS — it is an IM fix, not a voice feature.
        ///
        /// When this module is loaded the region advertises voice server type "webrtc". A
        /// Firestorm/LL viewer then resolves its WebRTC voice module and asks it for a P2P
        /// outgoing-call interface. WebRTC deliberately returns none
        /// (llvoicewebrtc.h:170 `getOutgoingCallInterface() override { return nullptr; }`);
        /// only Vivox implements it (llvoicevivox.h:72,178). Because that is null, the viewer
        /// flags EVERY new P2P session — including an ordinary TEXT IM — as "P2P as ad-hoc
        /// call" (llimview.cpp:908-911), routes it through this capability
        /// (llimview.cpp:2499-2507), and arms a 30s timer (SESSION_INITIALIZATION_TIMEOUT,
        /// llimview.cpp:110/937).
        ///
        /// Until that session is initialised the viewer does NOT send typed messages — it
        /// queues them (fsfloaterim.cpp:620-628 -> mQueuedMsgsForInit) and flushes that queue
        /// ONLY on the reply (fsfloaterim.cpp:1542-1552). There is no timeout flush and no
        /// retry, so with no reply the user's messages are silently discarded and they get
        /// "Unable to start a new chat session with X. The session initialization is timed
        /// out" (llimview.cpp:140-149).
        ///
        /// Neither core OpenSim nor this addon served the capability
        /// (OpenSimDefaults.ini Cap_ChatSessionRequest = ""; the registrations in
        /// VivoxVoiceModule.cs:434-437 and FreeSwitchVoiceModule.cs:296-302 are commented
        /// out), which is why enabling WebRTC voice broke text IM grid-wide.
        ///
        /// NOTE the reply must go out on the agent's EVENT QUEUE, not in this HTTP response:
        /// startP2PVoiceCoro (llimview.cpp:639-654) inspects only the HTTP status and ignores
        /// the body on success. EventQueueGetModule holds per-agent in-memory queues and
        /// exposes no external enqueue endpoint, so this has to be done in-process here.
        ///
        /// SCOPE: this initialises the chat session so text IM works, and (since 2026-10-19,
        /// see WOLF CALLS below) also runs private P2P voice calls: "call", the callee's
        /// invitation and "decline p2p voice". Group and conference voice are still not offered
        /// ("call" on those answers 403, which the viewer shows as VoiceNotAllowed).
        /// </summary>
        public void ChatSessionRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
        {
            if (request.HttpMethod != "POST")
            {
                m_log.DebugFormat("{0}[CHATSESSION]: Not a POST request. Agent={1}", logHeader, agentID);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            OSDMap map = null;
            using (Stream inputStream = request.InputStream)
            {
                if (inputStream.Length > 0)
                {
                    OSD tmp = OSDParser.DeserializeLLSDXml(inputStream);
                    if (_MessageDetails) m_log.DebugFormat("{0}[CHATSESSION]: Request: {1}", logHeader, tmp.ToString());
                    map = tmp as OSDMap;
                }
            }

            if (map is null)
            {
                m_log.ErrorFormat("{0}[CHATSESSION]: No request data found. Agent={1}", logHeader, agentID);
                response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            // "session-id" is hyphenated on the wire (llimview.cpp:626).
            string method = map.ContainsKey("method") ? map["method"].AsString() : String.Empty;
            UUID sessionID = map.ContainsKey("session-id") ? map["session-id"].AsUUID() : UUID.Zero;

            response.StatusCode = (int)HttpStatusCode.OK;

            // A start request with no session id cannot be answered: the viewer matches the
            // reply by temp_session_id (llimview.cpp:4851 findIMSession), so a zero id would
            // match nothing and the event would be noise. Refuse it loudly instead — reaching
            // here means the request shape has changed.
            if (sessionID.IsZero() && (method == "start p2p voice" || method == "start conference"))
            {
                m_log.WarnFormat("{0}[CHATSESSION]: '{1}' with no session-id. Agent={2} Request={3}",
                        logHeader, method, agentID, map.ToString());
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            switch (method)
            {
                // The case this fix is for. Reply success with the SAME session id.
                //
                // Echoing the id back is not laziness, it is required: for IM_NOTHING_SPECIAL
                // the viewer's session id is the XOR of the two agent ids
                // (llimview.cpp:2536-2553), and that is also the id inbound UDP IMs are filed
                // under. Returning a NEW id would make the viewer re-key the floater
                // (processSessionInitializedReply -> setKey) and subsequent inbound IMs would
                // land in a different session.
                case "start p2p voice":
                    SendChatterBoxSessionStartReply(scene, agentID, sessionID,
                            P2P_CHAT_SESSION, OtherParticipantName(scene, map), true, String.Empty,
                            CallsOn);   // [WOLF CALLS] voice is real now, when a key to sign credentials with is here
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><map /></llsd>");
                    return;

                // [WOLF GROUP CALLS 2026-10-20] Ad-hoc conferences (WolfConference.cs). Every failure
                // is a StartReply with success=false and a strings.xml KEY (resolved via
                // LLTrans::getString, llimview.cpp:2566), so the viewer reports it at once instead of
                // waiting out the 30 s timeout.
                case "start conference":
                    HandleStartConference(scene, agentID, sessionID, map);
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><map /></llsd>");
                    return;
                case "invite":
                    HandleInvite(scene, agentID, sessionID, map);
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                case "accept invitation":
                    // Group sessions are the groups module's; the viewer ignores this answer for them
                    // (as before). Conferences answer with the speaker list.
                    if (IsGroupSession(scene, agentID, sessionID))
                    {
                        response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                        return;
                    }
                    HandleAccept(scene, agentID, sessionID, response);
                    return;
                case "decline invitation":
                    if (!IsGroupSession(scene, agentID, sessionID))
                        HandleLeave(scene, agentID, sessionID);
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                // [WOLF CHAT 2026-10-20] a Wolf Chat voice room (WolfViewer's Wolf Chat window; optional,
                // needs the conference service - WolfConference.cs).
                case "wolf channel call":
                    HandleWolfChannelCall(scene, agentID, sessionID, map, response);
                    return;

                // chatterBoxHistoryCoro (llimview.cpp:775-815) requires an ARRAY and otherwise
                // just logs. We keep no server-side chat history, so answer an empty array.
                // Fired automatically after a successful start when the viewer's
                // FetchGroupChatHistory setting is on (llimview.cpp:4877-4886).
                case "fetch history":
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><array /></llsd>");
                    return;

                // Voice-call and moderation methods. None of these gate session
                // initialisation, so answering undef leaves text IM unaffected. They are
                // listed explicitly rather than lumped into default so the log distinguishes
                // "known but unimplemented" from "unexpected".
                // [WOLF CALLS 2026-10-19] The caller pressed Call (LLVoiceChannelGroup::voiceCallCapCoro,
                // llvoicechannel.cpp:631) and the callee pressed Decline (LLIncomingCallDialog::
                // processCallResponse, llimview.cpp:3487-3494).
                case "call":
                    HandleCall(scene, agentID, sessionID, response);
                    return;
                case "decline p2p voice":
                    HandleDeclineCall(scene, agentID, sessionID);
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;

                case "mute update":
                case "session update":
                    m_log.DebugFormat("{0}[CHATSESSION]: method '{1}' not implemented. Agent={2}",
                            logHeader, method, agentID);
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;

                default:
                    m_log.WarnFormat("{0}[CHATSESSION]: unrecognised method '{1}'. Agent={2} Request={3}",
                            logHeader, method, agentID, map.ToString());
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
            }
        }

        /// <summary>
        /// Best-effort display name for the other party in a P2P request, whose UUID arrives in
        /// "params" (llimview.cpp:627). Cosmetic only — Firestorm's consumer of
        /// session_info.session_name (processSessionUpdate) is commented out — so an empty
        /// string is an acceptable answer and no name-service dependency is added for it.
        /// </summary>
        private string OtherParticipantName(Scene scene, OSDMap map)
        {
            try
            {
                if (map.ContainsKey("params"))
                {
                    UUID otherID = map["params"].AsUUID();
                    if (otherID != UUID.Zero)
                    {
                        ScenePresence sp = scene.GetScenePresence(otherID);
                        if (sp is not null)
                            return sp.Name;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.DebugFormat("{0}[CHATSESSION]: could not resolve participant name: {1}", logHeader, e.Message);
            }
            return String.Empty;
        }

        /// <summary>
        /// Put a ChatterBoxSessionStartReply on the agent's event queue.
        ///
        /// Uses OpenSim's existing builder (EventQueueGetHandlers.cs:259-283), which emits
        /// exactly the fields the viewer reads in LLViewerChatterBoxSessionStartReply::post
        /// (llimview.cpp:4845-4896): session_id, temp_session_id, success, and on success a
        /// session_info map. Hand-rolling the LLSD here would only risk getting that wrong.
        /// </summary>
        private void SendChatterBoxSessionStartReply(Scene scene, UUID agentID, UUID sessionID,
                        int sessionType, string sessionName, bool success, string error, bool voiceEnabled)
        {
            IEventQueue eq = scene.RequestModuleInterface<IEventQueue>();
            if (eq is null)
            {
                // Without the event queue the viewer cannot be told the session is ready, and
                // it will time out after 30s. Log loudly — this is a misconfigured region.
                m_log.ErrorFormat("{0}[CHATSESSION]: no IEventQueue in region \"{1}\"; cannot reply to agent {2}",
                        logHeader, scene.RegionInfo.RegionName, agentID);
                return;
            }

            try
            {
                // Signature: EventQueueGetHandlers.cs:259 ChatterBoxSessionStartReply(sessionID,
                // sessionName, type, voiceEnabled, voiceModerated, tmpSessionID, success, error,
                // toAgent). [WOLF CALLS 2026-10-19] voiceEnabled is true for a P2P session once private
                // calls exist (HandleCall), false for anything this module cannot put a call on.
                eq.ChatterBoxSessionStartReply(sessionID, sessionName, sessionType,
                        voiceEnabled, false, sessionID, success, error, agentID);

                // Structural event, not a payload dump, so it is NOT gated behind MessageDetails
                // — this is the one line that confirms the IM-unblocking reply actually went out.
                m_log.DebugFormat("{0}[CHATSESSION]: ChatterBoxSessionStartReply session={1} success={2} agent={3}",
                        logHeader, sessionID, success, agentID);
            }
            catch (Exception e)
            {
                // Never let an event-queue problem turn into a 500 on the capability: the viewer
                // would then take the "session_does_not_exist_error" path instead of simply
                // retrying, and the caller still needs to send its HTTP response.
                m_log.ErrorFormat("{0}[CHATSESSION]: failed to enqueue reply for agent {1}: {2}",
                        logHeader, agentID, e.ToString());
            }
        }

        // ===================================================================================== WOLF CALLS
        // [WOLF CALLS 2026-10-19] Private (P2P) voice calls on WebRTC. The viewer runs a P2P call as a
        // two-person "multiagent" room (llimview.cpp:1007-1011: no P2P call interface on WebRTC ->
        // LLVoiceChannelGroup(is_p2p)):
        //   caller presses Call  -> ChatSessionRequest {"method":"call","session-id"}
        //                           (llvoicechannel.cpp:631) and joins with result["voice_credentials"]
        //                           {channel_uri, channel_credentials} (llvoicechannel.cpp:685,
        //                           llvoicewebrtc.cpp startAdHocSession).
        //   callee is rung       -> a ChatterBoxInvitation event whose body carries "voice"
        //                           (llimview.cpp:5269-5286); Accept joins with that map as its channel
        //                           info (llimview.cpp:3397-3401), Decline posts "decline p2p voice"
        //                           (llimview.cpp:3487-3494).
        // The session id of a P2P IM is caller XOR callee on both viewers (llimview.cpp:2620) and on
        // the server (GridInstantMessage.cs:111), so the other avatar is recovered from it - no state
        // is kept between requests, and the callee's region (any simulator) needs nothing from ours.
        // Each avatar's credential is the region's signature over channel + avatar (CallCredentials);
        // wolfvoice re-computes it with the same relay key before it opens the room (main.rs
        // call_credentials), so nobody else can join the call.

        /// <summary>Lowercase hex HMAC-SHA256(credential key, "wolfcall1:" + channel + ":" + avatar),
        /// byte for byte what wolfvoice main.rs call_credentials computes (avatar = UUID.ToString(),
        /// the same string the connector sends as userID, WebRtcVoiceServiceConnector.cs).
        /// [HARDENING 2026-10-23] Signed with the credential key (the relay key until one is installed).</summary>
        private string CallCredentials(string channel, UUID avatarID)
        {
            return WolfVoiceRules.CallCredentials(CredKey, channel, avatarID);
        }

        /// <summary>[HARDENING 2026-10-23] Is `given` a credential this grid issued `avatarID` for
        /// `channel`? The credential key's signature, or - during the change-over, while
        /// AcceptLegacyCallCredentials is on - the relay key's (a region not yet on this module).</summary>
        private bool CredentialMatches(string channel, UUID avatarID, string given)
        {
            string cred = CredKey;
            if (cred.Length > 0 && WolfVoiceRules.SameSecret(WolfVoiceRules.CallCredentials(cred, channel, avatarID), given))
                return true;
            string rpc = RpcKey;
            return m_AcceptLegacyCredentials && rpc.Length > 0 && rpc != cred
                && WolfVoiceRules.SameSecret(WolfVoiceRules.CallCredentials(rpc, channel, avatarID), given);
        }

        /// <summary>[HARDENING 2026-10-23] A group's id: its chat session uses it (llimview.cpp:2595-2598),
        /// so a "call" on it is group voice and never a private call, whatever it XORs to.</summary>
        private static bool IsGroupId(Scene scene, UUID id)
        {
            IGroupsModule groups = scene.RequestModuleInterface<IGroupsModule>();
            return groups?.GetGroupRecord(id) != null;
        }

        private static bool IsKnownAvatar(Scene scene, UUID id)
        {
            if (scene.GetScenePresence(id) is not null) return true;
            IUserManagement um = scene.RequestModuleInterface<IUserManagement>();
            UserData ud = um?.GetUserData(id);
            return ud is not null && !ud.IsUnknownUser;   // UserManagementModule.cs:1005-1013
        }

        private static string AvatarName(Scene scene, UUID id)
        {
            ScenePresence sp = scene.GetScenePresence(id);
            if (sp is not null) return sp.Name;
            IUserManagement um = scene.RequestModuleInterface<IUserManagement>();
            return um is not null ? um.GetUserName(id) : String.Empty;
        }

        private bool AllowCall(UUID callerID)
        {
            DateTime now = DateTime.UtcNow;
            lock (m_CallTimes)
            {
                if (!m_CallTimes.TryGetValue(callerID, out Queue<DateTime> times))
                    m_CallTimes[callerID] = times = new Queue<DateTime>();
                while (times.Count > 0 && (now - times.Peek()).TotalSeconds > 60.0) times.Dequeue();
                if (times.Count >= CALLS_PER_MINUTE) return false;
                times.Enqueue(now);
                return true;
            }
        }

        private void HandleCall(Scene scene, UUID callerID, UUID sessionID, IOSHttpResponse response)
        {
            // [HARDENING 2026-10-23] The caller is the agent this capability belongs to (never the
            // request), and the callee is DERIVED from the session - a P2P IM session is caller XOR
            // callee on both viewers (llimview.cpp:2620) and on the server (GridInstantMessage.cs:111)
            // - then verified: someone else, a real avatar, and the session not a group's. XOR alone
            // does not pin a pair (an id crafted as session XOR someone real "derives" a callee too),
            // so wolfvoice keys the call room on the PAIR the credential was issued for, not on the
            // session (main.rs p2p_pair): such a caller only ever reaches a room with that someone.
            UUID calleeID = sessionID ^ callerID;
            // 403 is the viewer's "no ability" answer: it shows VoiceNotAllowed and hangs up
            // (llvoicechannel.cpp:655-668) instead of waiting. Group and conference sessions land
            // here too (their id XOR the caller is nobody): since 2026-10-20 they get their own
            // rooms (HandleGroupOrConferenceCall, WolfConference.cs).
            if (!CallsOn || sessionID.IsZero() || calleeID.IsZero() || calleeID == callerID
                || IsGroupId(scene, sessionID) || !IsKnownAvatar(scene, calleeID))
            {
                if (HandleGroupOrConferenceCall(scene, callerID, sessionID, response))
                    return;
                m_log.InfoFormat("{0}[CALL]: refused call by {1} on session {2} (not a P2P session, or calls are off)", logHeader, callerID, sessionID);
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }
            if (!AllowCall(callerID))
            {
                m_log.InfoFormat("{0}[CALL]: {1} is calling too often; refused", logHeader, callerID);
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string channel = sessionID.ToString();
            string callerName = AvatarName(scene, callerID);

            OSDMap mine = new OSDMap
            {
                { "channel_uri", OSD.FromString(channel) },
                { "channel_credentials", OSD.FromString(CallCredentials(channel, callerID)) },
                { "voice_server_type", OSD.FromString("webrtc") }
            };
            response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(new OSDMap { { "voice_credentials", mine } });

            OSDMap voice = new OSDMap
            {
                { "channel_uri", OSD.FromString(channel) },
                { "channel_credentials", OSD.FromString(CallCredentials(channel, calleeID)) },
                { "invitation_type", OSD.FromInteger(P2P_CHAT_SESSION) },
                { "voice_server_type", OSD.FromString("webrtc") }
            };
            OSDMap body = new OSDMap
            {
                { "session_id", OSD.FromUUID(sessionID) },
                { "session_name", OSD.FromString(callerName) },
                { "from_id", OSD.FromUUID(callerID) },
                { "from_name", OSD.FromString(callerName) },
                { "voice", voice }
            };
            m_log.InfoFormat("{0}[CALL]: {1} is calling {2} (session {3})", logHeader, callerID, calleeID, sessionID);

            if (EnqueueCallInvitation(calleeID, body))
                return;

            // Not on this simulator: send the invitation to wherever the callee is, as a grid IM their
            // region's copy of this module turns into the event (OnIncomingCallInvitation).
            IMessageTransferModule tm = scene.RequestModuleInterface<IMessageTransferModule>();
            if (tm is null)
            {
                m_log.WarnFormat("{0}[CALL]: no message transfer module; cannot ring {1}", logHeader, calleeID);
                return;
            }
            GridInstantMessage gim = new GridInstantMessage(scene, callerID, callerName, calleeID, WOLF_CALL_DIALOG,
                    false, String.Empty, sessionID, false, Vector3.Zero,
                    Util.UTF8.GetBytes(WOLF_CALL_MARK + OSDParser.SerializeJsonString(body)), true);
            tm.SendInstantMessage(gim, delegate (bool success)
            {
                if (success) return;
                // Tell the caller, in their IM window with the callee, why nothing rings.
                ScenePresence caller = scene.GetScenePresence(callerID);
                caller?.ControllingClient.SendInstantMessage(new GridInstantMessage(scene, calleeID, AvatarName(scene, calleeID),
                        callerID, (byte)InstantMessageDialog.BusyAutoResponse,
                        "is not online, so the voice call could not ring.", false, Vector3.Zero));
                m_log.InfoFormat("{0}[CALL]: {1} could not be reached", logHeader, calleeID);
            });
        }

        /// <summary>Puts the voice invitation on the callee's event queue when they are a root agent on
        /// any region of this simulator. Built the way EventQueueGetHandlers.StartEvent does
        /// ({"message": name, "body": {...}}).</summary>
        private bool EnqueueCallInvitation(UUID calleeID, OSDMap body)
        {
            Scene[] scenes;
            lock (m_Scenes) scenes = m_Scenes.ToArray();
            foreach (Scene s in scenes)
            {
                ScenePresence sp = s.GetScenePresence(calleeID);
                if (sp is null || sp.IsChildAgent) continue;
                IEventQueue eq = s.RequestModuleInterface<IEventQueue>();
                if (eq is null) continue;
                OSD evt = new OSDMap { { "message", OSD.FromString("ChatterBoxInvitation") }, { "body", body } };
                eq.Enqueue(evt, calleeID);   // the OSD overload (an OSDMap also converts to byte[])
                m_log.InfoFormat("{0}[CALL]: rang {1} in {2}", logHeader, calleeID, s.RegionInfo.RegionName);
                return true;
            }
            return false;
        }

        /// <summary>A call invitation from another region (HandleCall's grid IM). Fires only on the
        /// region where the callee is a root agent (MessageTransferModule.cs:407-415).</summary>
        private void OnIncomingCallInvitation(GridInstantMessage msg)
        {
            if (msg.dialog != WOLF_CALL_DIALOG || msg.binaryBucket is null || !CallsOn) return;
            string text = Util.UTF8.GetString(msg.binaryBucket);
            if (!text.StartsWith(WOLF_CALL_MARK, StringComparison.Ordinal)) return;
            try
            {
                if (OSDParser.DeserializeJson(text.Substring(WOLF_CALL_MARK.Length)) is not OSDMap got || got["voice"] is not OSDMap gotVoice)
                    return;
                UUID calleeID = new UUID(msg.toAgentID);
                string channel = gotVoice["channel_uri"].AsString();
                // A forged invitation has no working credential: drop it here instead of ringing anyone.
                // [HARDENING 2026-10-23] Either key during the change-over (CredentialMatches), and the
                // invitation must hang together: the session is the caller XOR this callee and the
                // channel is that session (HandleCall builds it so).
                UUID inviteSession = got["session_id"].AsUUID();
                UUID inviteFrom = got["from_id"].AsUUID();
                if (!CredentialMatches(channel, calleeID, gotVoice["channel_credentials"].AsString())
                    || inviteSession.IsZero() || inviteSession != (inviteFrom ^ calleeID) || channel != inviteSession.ToString())
                {
                    m_log.WarnFormat("{0}[CALL]: dropped an invitation for {1} with a bad credential", logHeader, calleeID);
                    return;
                }
                // JSON carried the uuids as strings: give the viewer the LLSD types it reads
                // (session_id/from_id asUUID, invitation_type asInteger - llimview.cpp:5277-5286).
                OSDMap voice = new OSDMap
                {
                    { "channel_uri", OSD.FromString(channel) },
                    { "channel_credentials", OSD.FromString(gotVoice["channel_credentials"].AsString()) },
                    { "invitation_type", OSD.FromInteger(P2P_CHAT_SESSION) },
                    { "voice_server_type", OSD.FromString("webrtc") }
                };
                OSDMap body = new OSDMap
                {
                    { "session_id", OSD.FromUUID(got["session_id"].AsUUID()) },
                    { "session_name", OSD.FromString(got["session_name"].AsString()) },
                    { "from_id", OSD.FromUUID(got["from_id"].AsUUID()) },
                    { "from_name", OSD.FromString(got["from_name"].AsString()) },
                    { "voice", voice }
                };
                if (!EnqueueCallInvitation(calleeID, body))
                    m_log.InfoFormat("{0}[CALL]: invitation for {1} arrived but they are not here", logHeader, calleeID);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("{0}[CALL]: unreadable call invitation: {1}", logHeader, e.Message);
            }
        }

        /// <summary>The callee pressed Decline: tell the caller in their IM window, so they are not left
        /// waiting on a call that will never be answered.</summary>
        private void HandleDeclineCall(Scene scene, UUID calleeID, UUID sessionID)
        {
            UUID callerID = sessionID ^ calleeID;
            if (sessionID.IsZero() || callerID.IsZero() || callerID == calleeID) return;
            IMessageTransferModule tm = scene.RequestModuleInterface<IMessageTransferModule>();
            if (tm is null) return;
            tm.SendInstantMessage(new GridInstantMessage(scene, calleeID, AvatarName(scene, calleeID), callerID,
                    (byte)InstantMessageDialog.MessageFromAgent, false, "(declined the voice call)", sessionID,
                    false, Vector3.Zero, Array.Empty<byte>(), true), delegate (bool success) { });
            m_log.InfoFormat("{0}[CALL]: {1} declined the call from {2}", logHeader, calleeID, callerID);
        }

        // NOTE NOTE!! This is code from the FreeSwitch module. It is not clear if this is correct for WebRtc.
        // Not sure what this Uri is for. Is this FreeSwitch specific?
        // TODO: is this useful for WebRtc?
        private string ChannelUri(Scene scene, LandData land)
        {
            string channelUri = null;

            string landUUID;
            string landName;

            // Create parcel voice channel. If no parcel exists, then the voice channel ID is the same
            // as the directory ID. Otherwise, it reflects the parcel's ID.

            lock (m_ParcelAddress)
            {
                if (m_ParcelAddress.ContainsKey(land.GlobalID.ToString()))
                {
                    m_log.DebugFormat("{0}: parcel id {1}: using sip address {2}",
                                      logHeader, land.GlobalID, m_ParcelAddress[land.GlobalID.ToString()]);
                    return m_ParcelAddress[land.GlobalID.ToString()];
                }
            }

            if (land.LocalID != 1 && (land.Flags & (uint)ParcelFlags.UseEstateVoiceChan) == 0)
            {
                landName = String.Format("{0}:{1}", scene.RegionInfo.RegionName, land.Name);
                landUUID = land.GlobalID.ToString();
                m_log.DebugFormat("{0}: Region:Parcel \"{1}\": parcel id {2}: using channel name {3}",
                                  logHeader, landName, land.LocalID, landUUID);
            }
            else
            {
                landName = String.Format("{0}:{1}", scene.RegionInfo.RegionName, scene.RegionInfo.RegionName);
                landUUID = scene.RegionInfo.RegionID.ToString();
                m_log.DebugFormat("{0}: Region:Parcel \"{1}\": parcel id {2}: using channel name {3}",
                                  logHeader, landName, land.LocalID, landUUID);
            }

            // slvoice handles the sip address differently if it begins with confctl, hiding it from the user in
            // the friends list. however it also disables the personal speech indicators as well unless some
            // siren14-3d codec magic happens. we dont have siren143d so we'll settle for the personal speech indicator.
            channelUri = String.Format("sip:conf-{0}@{1}",
                     "x" + Convert.ToBase64String(Encoding.ASCII.GetBytes(landUUID)),
                     /*m_freeSwitchRealm*/ "webRTC");

            lock (m_ParcelAddress)
            {
                if (!m_ParcelAddress.ContainsKey(land.GlobalID.ToString()))
                {
                    m_ParcelAddress.Add(land.GlobalID.ToString(),channelUri);
                }
            }

            return channelUri;
        }

    }
}
