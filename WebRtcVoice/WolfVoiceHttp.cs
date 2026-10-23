// By Shadow Wolf and Lone Wolf, Wolf Territories Grid and IntelligentWolf Ltd.
// Copyright 2026 IntelligentWolf Ltd. Added to the os-webrtc-janus addon; distributed under the same
// MPL 2.0 licence as the rest of this addon.
//
// [HARDENING 2026-10-23] The region -> wolfvoice JSON-RPC, with the voice server's TLS certificate
// actually verified, and the shared keys read from their files (and re-read when they change).
//
// Why a client of its own: every other outbound HTTP in OpenSim goes through WebUtil's shared
// SocketsHttpHandler, whose certificate callback is set from [Startup] NoVerifyCertChain /
// NoVerifyCertHostname (WebUtil.cs:114-245). Both default to TRUE (BaseOpenSimServer.cs:85-86),
// and with both true the callback ignores chain AND name errors (WebUtil.cs:200-207) - so anyone
// able to intercept the region's traffic could answer as the voice server, read the relay key
// and every room credential, and hand viewers their own DTLS fingerprint. Changing those globals
// would change every other HTTP call the simulator makes. This client is used ONLY for the
// voice server URL and always validates: the certificate chain against the system trust store
// and the host name, exactly as a browser would.
//
// The request/answer shape is the one WebRtcVoiceServiceConnector.JsonRpcRequest had with
// WebUtil.PostToService(uri, request, 10000, true): a JSON-RPC 2.0 object POSTed with
// Content-Type application/json-rpc (WebUtil.cs:407-408) and "Connection: close"
// (WebUtil.cs:422); the reply's "result" map, or {"error": text} for every failure.

using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using log4net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace WebRtcVoice
{
    public static class WolfVoiceHttp
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[WOLFVOICE HTTP]";

        private static readonly HttpClient s_client = CreateClient();
        private static DateTime s_lastTlsLog = DateTime.MinValue;

        private static HttpClient CreateClient()
        {
            SocketsHttpHandler h = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(3),
            };
            // Revocation: the same NoCheck WebUtil uses (WebUtil.cs:132/152); Let's Encrypt no longer
            // runs OCSP, and a CRL fetch on the voice path would add a network dependency.
            h.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
            h.SslOptions.RemoteCertificateValidationCallback = ValidateCertificate;
            return new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>Accepts exactly what the platform validated: a chain to a trusted root and a
        /// certificate for the host in the URL. Anything else is refused and said in the log (at most
        /// once a minute), so a broken certificate is visible instead of voice just stopping.</summary>
        public static bool ValidateCertificate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
                return true;
            DateTime now = DateTime.UtcNow;
            if (now - s_lastTlsLog > TimeSpan.FromMinutes(1))
            {
                s_lastTlsLog = now;
                m_log.ErrorFormat("{0} refused the voice server's TLS certificate ({1}; subject {2}) - voice is off until it validates",
                        LogHeader, errors, cert?.Subject ?? "none");
            }
            return false;
        }

        /// <summary>The request as text for a log line, with params.key replaced, so the relay key is
        /// never written to a log (the old connector logged the whole request, key included).</summary>
        public static string Redacted(OSDMap request)
        {
            try
            {
                OSDMap copy = (OSDMap)OSDParser.DeserializeJson(OSDParser.SerializeJsonString(request));
                if (copy.TryGetValue("params", out OSD p) && p is OSDMap pm && pm.ContainsKey("key"))
                    pm["key"] = OSD.FromString("(redacted)");
                return OSDParser.SerializeJsonString(copy);
            }
            catch (Exception)
            {
                return "(unprintable request)";
            }
        }

        private static OSDMap Error(string text)
        {
            return new OSDMap { { "error", OSD.FromString(text) } };
        }

        private static HttpRequestMessage Build(string uri, string method, OSDMap pParams, out OSDMap request)
        {
            request = new OSDMap
            {
                { "jsonrpc", OSD.FromString("2.0") },
                { "id", OSD.FromString(UUID.Random().ToString()) },
                { "method", OSD.FromString(method) },
                { "params", pParams }
            };
            HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Post, uri);
            ByteArrayContent body = new ByteArrayContent(OSDParser.SerializeJsonToBytes(request));
            body.Headers.TryAddWithoutValidation("Content-Type", "application/json-rpc");
            msg.Content = body;
            msg.Headers.ExpectContinue = false;
            msg.Headers.ConnectionClose = true;
            return msg;
        }

        /// <summary>The "result" map of a JSON-RPC answer, or {"error": ...} - the same texts the
        /// connector produced before (WebRtcVoiceServiceConnector.cs JsonRpcRequest).</summary>
        public static OSDMap Interpret(string method, string uri, string text)
        {
            OSD parsed;
            try
            {
                parsed = OSDParser.DeserializeJson(text);
            }
            catch (Exception)
            {
                parsed = null;
            }
            if (parsed is not OSDMap response)
                return Error(String.Format("JsonRpc request '{0}' to {1} returned an invalid response: {2}",
                        method, uri, text.Length > 200 ? text.Substring(0, 200) : text));
            if (response.TryGetValue("error", out OSD err))
                return Error(String.Format("JsonRpc request '{0}' to {1} returned an error: {2}",
                        method, uri, OSDParser.SerializeJsonString(err)));
            if (!response.TryGetValue("result", out OSD res) || res is not OSDMap resultmap)
                return Error(String.Format("JsonRpc request '{0}' to {1} returned result as non-OSDMap: {2}",
                        method, uri, OSDParser.SerializeJsonString(response)));
            return resultmap;
        }

        /// <summary>Blocking call (use from a pool thread, as the connector does inside Task.Run).</summary>
        public static OSDMap PostJsonRpc(string uri, string method, OSDMap pParams, int timeoutMs, bool messageDetails)
        {
            OSDMap request = null;
            try
            {
                using (CancellationTokenSource cts = new CancellationTokenSource(timeoutMs))
                using (HttpRequestMessage msg = Build(uri, method, pParams, out request))
                {
                    if (messageDetails) m_log.DebugFormat("{0}: request: {1}", LogHeader, Redacted(request));
                    OSDMap answer;
                    using (HttpResponseMessage res = s_client.Send(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                    {
                        if (!res.IsSuccessStatusCode)
                        {
                            answer = Error(String.Format("JsonRpc request '{0}' to {1} failed: HTTP {2}", method, uri, (int)res.StatusCode));
                        }
                        else
                        {
                            using (StreamReader reader = new StreamReader(res.Content.ReadAsStream(cts.Token), Encoding.UTF8))
                            {
                                string text = reader.ReadToEnd();
                                if (messageDetails) m_log.DebugFormat("{0}: response: {1}", LogHeader, text);
                                answer = Interpret(method, uri, text);
                            }
                        }
                    }
                    // Logged as the connector always did (its texts carry no key).
                    if (answer.TryGetValue("error", out OSD err))
                        m_log.ErrorFormat("{0}: {1}", LogHeader, err.AsString());
                    return answer;
                }
            }
            catch (Exception e)
            {
                // The exception text only: never the request, which carries the relay key.
                m_log.ErrorFormat("{0}: JsonRpc request '{1}' to {2} failed: {3}", LogHeader, method, uri, e.Message);
                return Error(e.Message);
            }
        }

        /// <summary>Non-blocking call, for the position feed.</summary>
        public static async Task<OSDMap> PostJsonRpcAsync(string uri, string method, OSDMap pParams, int timeoutMs, CancellationToken stop)
        {
            try
            {
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(stop))
                using (HttpRequestMessage msg = Build(uri, method, pParams, out OSDMap _))
                {
                    cts.CancelAfter(timeoutMs);
                    using (HttpResponseMessage res = await s_client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        if (!res.IsSuccessStatusCode)
                            return Error(String.Format("JsonRpc request '{0}' to {1} failed: HTTP {2}", method, uri, (int)res.StatusCode));
                        string text = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                        return Interpret(method, uri, text);
                    }
                }
            }
            catch (Exception e)
            {
                return Error(e.Message);
            }
        }
    }

    /// <summary>
    /// [HARDENING 2026-10-23] A secret kept in a file (the relay key, the credential key): read at
    /// start, and looked at again at most once a minute, so a key installed or replaced on the host
    /// is picked up without restarting the region. Missing or unreadable = empty.
    /// </summary>
    public sealed class WolfVoiceKeyFile
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly TimeSpan CHECK_EVERY = TimeSpan.FromSeconds(60);

        private readonly object m_lock = new object();
        private readonly string m_path;
        private string m_value = string.Empty;
        private DateTime m_written = DateTime.MinValue;
        private DateTime m_nextCheck = DateTime.MinValue;

        public WolfVoiceKeyFile(string path)
        {
            m_path = path ?? string.Empty;
            lock (m_lock) Refresh(DateTime.UtcNow);
        }

        public string Path { get { return m_path; } }

        public string Value
        {
            get
            {
                lock (m_lock)
                {
                    DateTime now = DateTime.UtcNow;
                    if (now >= m_nextCheck) Refresh(now);
                    return m_value;
                }
            }
        }

        private void Refresh(DateTime now)
        {
            m_nextCheck = now + CHECK_EVERY;
            try
            {
                if (m_path.Length == 0 || !File.Exists(m_path))
                {
                    if (m_value.Length > 0)
                        m_log.WarnFormat("[WOLFVOICE KEY]: {0} is gone; that key is no longer used", m_path);
                    m_value = string.Empty;
                    m_written = DateTime.MinValue;
                    return;
                }
                DateTime written = File.GetLastWriteTimeUtc(m_path);
                if (written == m_written) return;
                string v = File.ReadAllText(m_path).Trim();
                if (m_written != DateTime.MinValue || m_value.Length > 0)
                    m_log.InfoFormat("[WOLFVOICE KEY]: {0} changed; the new key is in use", m_path);
                m_value = v;
                m_written = written;
            }
            catch (Exception e)
            {
                // Keep what we had: a transient read error must not switch voice off.
                m_log.WarnFormat("[WOLFVOICE KEY]: could not read {0}: {1}", m_path, e.Message);
            }
        }
    }
}
