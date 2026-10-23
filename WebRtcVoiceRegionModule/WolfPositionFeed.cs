// By Shadow Wolf and Lone Wolf, Wolf Territories Grid and IntelligentWolf Ltd.
// Copyright 2026 IntelligentWolf Ltd. Added to the os-webrtc-janus addon; distributed under the same
// MPL 2.0 licence as WebRtcVoiceRegionModule.cs.
//
// [HARDENING 2026-10-23] Where avatars really are, told to wolfvoice.
//
// The viewer reports its own position on the voice data channel ("sp", llvoicewebrtc.cpp:1226-1230),
// so a modified viewer could claim to stand anywhere in its room and listen from there. The viewer's
// own code expects the server to hold it to "the actual position of the avatar ... sent from the
// simulator to the voice server" (llvoicewebrtc.cpp:1185-1188, :1209-1215). This sends it: about once
// a second, the positions of this simulator's ROOT agents that are in voice, in one batch per
// simulator (method wolf.positions; wolfvoice src/truth.rs clamps the viewer's claim to them).
//
// Built for scale (hundreds of regions, most of them empty at any moment):
//   * nothing is sent from a simulator with no root agent in voice;
//   * wolfvoice answers each batch with the agents it actually has a spatial session for ("want"),
//     and only those are sent every second;
//   * a newly arrived root agent, or one who just opened voice through this region, is asked about
//     once at the next tick; and every 30 s all root agents are asked about once, to catch anyone who
//     opened voice somewhere else first;
//   * an agent sent last time that is no longer a root agent here is reported "gone" once, so
//     wolfvoice stops holding them to this region at once (a teleport or crossing).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Scenes;

namespace WebRtcVoice
{
    public partial class WebRtcVoiceRegionModule
    {
        private const int FEED_INTERVAL_MS = 1000;
        private const int FEED_TIMEOUT_MS = 5000;
        private static readonly TimeSpan FEED_PROBE_EVERY = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FEED_PAUSE_ON_OLD_SERVER = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan FEED_ERROR_LOG_EVERY = TimeSpan.FromMinutes(1);
        // wolfvoice main.rs MAX_POSITIONS: the most entries one request may carry.
        private const int FEED_MAX = 4096;
        private const string METHOD_POSITIONS = "wolf.positions";

        private bool m_FeedOn;
        private string m_FeedUri = string.Empty;
        private readonly object m_FeedLock = new object();
        private HashSet<UUID> m_FeedWant = new HashSet<UUID>();
        private readonly HashSet<UUID> m_FeedNew = new HashSet<UUID>();
        private Dictionary<UUID, UUID> m_FeedSent = new Dictionary<UUID, UUID>();
        private DateTime m_FeedNextProbe = DateTime.MinValue;
        private DateTime m_FeedPausedUntil = DateTime.MinValue;
        private DateTime m_FeedLastErrorLog = DateTime.MinValue;
        private CancellationTokenSource m_FeedStop;

        /// <summary>On when this simulator's spatial voice is the wolfvoice relay
        /// (SpatialVoiceService = WebRtcVoice.dll:WebRtcVoiceServiceConnector) and [WebRtcVoice]
        /// PositionFeed is not false.</summary>
        private void InitPositionFeed(IConfig cfg)
        {
            string spatial = cfg.GetString("SpatialVoiceService", string.Empty);
            m_FeedUri = cfg.GetString("WebRtcVoiceServerURI", string.Empty);
            m_FeedOn = cfg.GetBoolean("PositionFeed", true)
                    && spatial.Contains("WebRtcVoiceServiceConnector", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(m_FeedUri);
            if (m_FeedOn)
                m_log.InfoFormat("{0}: avatar positions go to the voice server once a second while anyone here is in voice", logHeader);
        }

        private void StartPositionFeed()
        {
            CancellationToken stop;
            lock (m_FeedLock)
            {
                if (!m_FeedOn || m_FeedStop != null) return;
                m_FeedStop = new CancellationTokenSource();
                stop = m_FeedStop.Token;
            }
            Task.Run(() => FeedLoop(stop));
        }

        private void StopPositionFeed()
        {
            lock (m_FeedLock)
            {
                m_FeedStop?.Cancel();
                m_FeedStop = null;
            }
        }

        /// <summary>Ask wolfvoice about this agent at the next tick.</summary>
        private void FeedNote(UUID agent)
        {
            if (!m_FeedOn) return;
            lock (m_FeedLock) m_FeedNew.Add(agent);
        }

        private void OnMakeRootAgentFeed(ScenePresence sp)
        {
            if (!sp.IsNPC) FeedNote(sp.UUID);
        }

        private async Task FeedLoop(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(FEED_INTERVAL_MS, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                try
                {
                    await FeedTick(stop).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    FeedError("position feed failed: " + e.Message);
                }
            }
        }

        private void FeedError(string text)
        {
            DateTime now = DateTime.UtcNow;
            if (now - m_FeedLastErrorLog < FEED_ERROR_LOG_EVERY) return;
            m_FeedLastErrorLog = now;
            m_log.WarnFormat("{0}: {1}", logHeader, text);
        }

        /// <summary>The velocity that moves the avatar: a seated one goes with its seat (a vehicle).</summary>
        private static Vector3 AvatarVelocity(ScenePresence sp)
        {
            SceneObjectPart seat = sp.ParentPart;
            if (sp.ParentID != 0 && seat?.ParentGroup != null)
                return seat.ParentGroup.Velocity;
            return sp.Velocity;
        }

        private async Task FeedTick(CancellationToken stop)
        {
            DateTime now = DateTime.UtcNow;
            if (now < m_FeedPausedUntil) return;
            // wolfvoice refuses wolf.positions without the relay key (main.rs); nothing to do without one.
            string key = RpcKey;
            if (key.Length == 0) return;

            bool probe;
            HashSet<UUID> want;
            HashSet<UUID> fresh;
            lock (m_FeedLock)
            {
                probe = now >= m_FeedNextProbe;
                if (probe) m_FeedNextProbe = now + FEED_PROBE_EVERY;
                want = m_FeedWant;
                fresh = new HashSet<UUID>(m_FeedNew);
                m_FeedNew.Clear();
            }

            Scene[] scenes;
            lock (m_Scenes) scenes = m_Scenes.ToArray();
            OSDArray positions = new OSDArray();
            Dictionary<UUID, UUID> sent = new Dictionary<UUID, UUID>();
            foreach (Scene s in scenes)
            {
                // Global metres, the frame the viewer's "sp" is in (getPositionGlobal, llvoicewebrtc.cpp:1093).
                double ox = s.RegionInfo.WorldLocX;
                double oy = s.RegionInfo.WorldLocY;
                UUID sceneId = s.RegionInfo.RegionID;
                s.ForEachRootScenePresence(delegate (ScenePresence sp)
                {
                    if (sp.IsNPC || positions.Count >= FEED_MAX) return;
                    UUID a = sp.UUID;
                    if (!probe && !want.Contains(a) && !fresh.Contains(a)) return;
                    Vector3 p = sp.AbsolutePosition;
                    Vector3 v = AvatarVelocity(sp);
                    positions.Add(new OSDMap
                    {
                        { "a", OSD.FromString(a.ToString()) },
                        { "s", OSD.FromString(sceneId.ToString()) },
                        { "p", new OSDArray { OSD.FromReal(ox + p.X), OSD.FromReal(oy + p.Y), OSD.FromReal(p.Z) } },
                        { "v", new OSDArray { OSD.FromReal(v.X), OSD.FromReal(v.Y), OSD.FromReal(v.Z) } },
                    });
                    sent[a] = sceneId;
                });
            }

            OSDArray gone = new OSDArray();
            lock (m_FeedLock)
            {
                foreach (KeyValuePair<UUID, UUID> kv in m_FeedSent)
                {
                    if (!sent.TryGetValue(kv.Key, out UUID nowScene) || nowScene != kv.Value)
                    {
                        if (gone.Count >= FEED_MAX) break;
                        gone.Add(new OSDMap
                        {
                            { "a", OSD.FromString(kv.Key.ToString()) },
                            { "s", OSD.FromString(kv.Value.ToString()) },
                        });
                    }
                }
                m_FeedSent = sent;
            }
            if (positions.Count == 0 && gone.Count == 0) return;

            OSDMap prm = new OSDMap
            {
                { "key", OSD.FromString(key) },
                { "positions", positions },
                { "gone", gone },
            };
            OSDMap res = await WolfVoiceHttp.PostJsonRpcAsync(m_FeedUri, METHOD_POSITIONS, prm, FEED_TIMEOUT_MS, stop).ConfigureAwait(false);
            if (res.TryGetValue("error", out OSD err))
            {
                string text = err.AsString();
                if (text.Contains("unknown method", StringComparison.Ordinal))
                {
                    // A voice server from before 2026-10-23: try again later, quietly.
                    m_FeedPausedUntil = DateTime.UtcNow + FEED_PAUSE_ON_OLD_SERVER;
                    m_log.InfoFormat("{0}: the voice server does not take positions yet; trying again in 10 minutes", logHeader);
                }
                else
                {
                    FeedError("position feed: " + text);
                }
                return;
            }
            HashSet<UUID> nextWant = new HashSet<UUID>();
            if (res.TryGetValue("want", out OSD w) && w is OSDArray arr)
            {
                foreach (OSD o in arr)
                    if (UUID.TryParse(o.AsString(), out UUID id)) nextWant.Add(id);
            }
            lock (m_FeedLock) m_FeedWant = nextWant;
        }
    }
}
