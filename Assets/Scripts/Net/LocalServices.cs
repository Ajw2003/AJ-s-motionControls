using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Managers;
using Singleton;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Net
{
    /// <summary>How far along the local relay launch is. Drives what the TV shows.</summary>
    public enum ServiceState
    {
        Idle,
        ObtainingCertificate,
        ProvisioningNode,
        InstallingDependencies,
        StartingRelay,
        Ready,
        Failed,
    }

    /// <summary>
    /// Runs the relay on this machine. This is how the game is hosted — not a dev convenience.
    /// </summary>
    /// <remarks>
    /// ON BY DEFAULT, and there is nothing to fall back to: the hosted Fly.io relay this used
    /// to be an alternative to was deliberately torn down. Bootstrap's startLocalServices
    /// creates this, and RelayClient takes its address from SecureSocketUrl rather than
    /// needing a relay-url.txt — that file is now only the manual escape hatch for pointing a
    /// build at a relay somebody started by hand.
    ///
    /// HTTPS is not optional in practice. Phone motion-sensor APIs only unlock on a secure
    /// origin, so the certificate step (CertificateProvider, MagicDomain) runs before the
    /// relay and hands it TLS material through the environment.
    ///
    /// This class used to also spawn a cloudflared tunnel and scrape the assigned hostname
    /// out of its stderr, because a quick tunnel offers no machine-readable channel for it.
    /// A fixed service address removed the need for both.
    /// </remarks>
    public class LocalServices : SingletonBase<LocalServices>
    {
        /// <summary>
        /// Optional per-machine environment for the relay child, beside relay-url.txt.
        /// Gitignored, and absent on almost every machine — a hand-written escape hatch for
        /// pinning TLS material or a port, not something any script writes any more.
        /// </summary>
        private const string EnvFileName = "relay-env.txt";

        [Tooltip("Start the relay on this machine. Off leaves phones with nothing to connect " +
                 "to unless a relay-url.txt points RelayClient at one started by hand.")]
        [SerializeField] private bool launchOnStart = true;

        [Tooltip("Fallback only — the port from the relay-url.txt override wins when there is one.")]
        [SerializeField] private int relayPort = 3000;

        [Tooltip("Echo relay output into the Unity console.")]
        [SerializeField] private bool verboseLogging;

        [Header("HTTPS")]
        [Tooltip("Obtain a certificate and serve the controller over HTTPS. Required for phone " +
                 "motion sensors — an insecure origin makes them unavailable. Turn off only for " +
                 "a desktop-only loop where no phone is involved.")]
        [SerializeField] private bool useHttps = true;

        [Tooltip("Port for the HTTPS relay. Ignored when HTTPS is off.")]
        [SerializeField] private int securePort = 8443;

        [Header("Timeouts (seconds)")]
        [SerializeField] private float dependencyInstallTimeout = 180f;
        [SerializeField] private float relayStartTimeout = 30f;

        public ServiceState State { get; private set; } = ServiceState.Idle;

        /// <summary>One line for the TV describing what is happening right now.</summary>
        public string StatusMessage { get; private set; } = "Starting up…";

        /// <summary>On failure, what the person standing at the PC should actually do about it.</summary>
        public string FailureHint { get; private set; }

        public event Action<ServiceState> StateChanged;

        private Process _relay;
        private bool _ownsRelay;
        private ChildProcessGroup _group;

        private readonly ConcurrentQueue<string> _output = new();

        private string _origin;

        private CertificateProvider.Result _certificate;

        /// <summary>
        /// The name the certificate covers, once one has been obtained — MagicDomain.HostName
        /// (e.g. play.skiprocking.ca), fixed regardless of this machine's address. Null when
        /// HTTPS is off or the certificate step failed.
        /// </summary>
        /// <remarks>
        /// This is what a phone must use. The raw IP would not match the certificate, and a
        /// name mismatch on a phone reads as a configuration mistake rather than the naming
        /// rule it actually is.
        /// </remarks>
        public string CertificateHostName => _certificate.Ok ? _certificate.HostName : null;

        /// <summary>The wss:// address a phone should be pointed at, or null when HTTPS is off.</summary>
        public string SecureSocketUrl =>
            _certificate.Ok ? $"wss://{_certificate.HostName}:{securePort}" : null;

        /// <summary>
        /// Where the relay actually lives, derived from the same relay-url.txt RelayClient
        /// reads so the two can never disagree about it.
        /// </summary>
        /// <remarks>
        /// This used to be hardcoded to http://127.0.0.1:3000, which quietly broke the reuse
        /// path below for any relay not on that address: a relay started by hand on another
        /// port looked absent, so this spawned a second one that nothing ever connected to.
        ///
        /// This is the address shown to a human (log messages, the Fail() hint) and handed to
        /// a phone (SecureSocketUrl, JoinUrlFor) — it is NOT what LocalServices probes its own
        /// process with. See ProbeHealthUrl for why those had to split.
        /// </remarks>
        private string Origin => _origin ??= ResolveOrigin();

        private string HealthUrl => $"{Origin}/health";

        /// <summary>
        /// The address ProbeHealth/ProbeDnsBound actually connect to — loopback, once a
        /// certificate is in play, never the public hostname in HealthUrl.
        /// </summary>
        /// <remarks>
        /// In Hotspot mode the certificate's address (192.168.137.1) is not reachable on
        /// this host until Hotspot tethering has actually started — which happens only after
        /// this class reaches Ready. Probing the public hostname here deadlocks forever:
        /// LocalServices waits on its own health check to reach Ready, HotspotService waits
        /// on Ready before starting tethering, and the address the health check needs does
        /// not exist until tethering has started. Loopback sidesteps the whole cycle — it is
        /// always reachable regardless of hotspot state, because this is really just asking
        /// "is the process I just spawned still alive," not a network question at all.
        ///
        /// A phone never sees this URL — SecureSocketUrl/JoinUrlFor still hand out the real
        /// hostname, validated normally by every phone that connects to it. Loopback HTTPS
        /// needs its own certificate handling since the cert's SAN never includes
        /// "127.0.0.1"; see AcceptAnyCertificate for why that is scoped tightly enough to be
        /// safe here and nowhere else.
        /// </remarks>
        private string ProbeHealthUrl => useHttps && _certificate.Ok
            ? $"https://127.0.0.1:{securePort}/health"
            : HealthUrl;

        /// <summary>
        /// Accepts any certificate. Scoped to LocalServices' own loopback probe of the relay
        /// process it just spawned — a same-machine liveness check, not a network trust
        /// decision, and not reachable from anywhere else. Nothing phone-facing ever uses
        /// this; every real client still validates the relay's actual certificate normally.
        /// </summary>
        private sealed class AcceptAnyCertificate : CertificateHandler
        {
            protected override bool ValidateCertificate(byte[] certificateData) => true;
        }

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();
        }

        private void Start()
        {
            if (!launchOnStart)
            {
                SetState(ServiceState.Idle, "Local relay off — nothing is hosting the game.");
                return;
            }

            _group = ChildProcessGroup.Create();
            StartCoroutine(LaunchSequence());
        }

        private void Update()
        {
            // Process output arrives on threadpool threads; logging it from here keeps
            // everything that touches Unity on the main thread.
            while (_output.TryDequeue(out var line))
            {
                if (verboseLogging) Debug.Log($"[LocalServices] {line}");
            }
        }

        // ------------------------------------------------------------------ sequence

        private IEnumerator LaunchSequence()
        {
            var relayDir = ResolveRelayDirectory();
            if (relayDir == null)
            {
                Fail("Can't find the relay.",
                     "Expected a 'relay' folder next to Assets/. LocalServices resolves it " +
                     "relative to Application.dataPath.");
                yield break;
            }

            // Before the relay, because the relay reads the certificate paths out of its
            // environment at startup and has no way to pick them up afterwards.
            if (useHttps)
            {
                yield return ObtainCertificate();
                if (State == ServiceState.Failed) yield break;
            }

            yield return StartRelay(relayDir);
            if (State == ServiceState.Failed) yield break;

            SetState(ServiceState.Ready, useHttps && _certificate.Ok
                ? $"Relay at {SecureSocketUrl}."
                : $"Local relay at {Origin}.");
        }

        /// <summary>
        /// The one address a hotspot host is ever reachable at. Forced rather than detected —
        /// the hotspot's own AP adapter has no default gateway of its own (it hands out this
        /// same address as a gateway to its clients but has none configured on itself), so any
        /// attempt to detect it by sweeping network interfaces would fail or guess wrong every
        /// time. With Hotspot the only mode, this is simply a constant rather than something
        /// to compute per launch — see CertificateProvider.ValidateHostAddress, which is all
        /// that is left to check once nothing needs discovering.
        /// </summary>
        private const string HotspotHostAddress = "192.168.137.1";

        private IEnumerator ObtainCertificate()
        {
            SetState(ServiceState.ObtainingCertificate, "Checking the certificate…");

            CertificateProvider.Result result = default;
            yield return CertificateProvider.Ensure(HotspotHostAddress, r => result = r);

            if (!result.Ok)
            {
                Fail(result.Error, result.Detail);
                yield break;
            }

            _certificate = result;

            // The origin is derived from the certificate's name from here on, so the page,
            // the socket and the certificate cannot drift onto different hosts.
            _origin = $"https://{result.HostName}:{securePort}";
        }

        private IEnumerator StartRelay(string relayDir)
        {
            // Reuse a relay that is already listening rather than fighting it for the
            // port. This is what keeps `npm start` in a terminal a valid dev loop, and
            // it means a stale relay from a previous run is harmless rather than fatal.
            var alreadyRunning = false;
            yield return ProbeHealth(ok => alreadyRunning = ok);

            if (alreadyRunning)
            {
                _ownsRelay = false;
                SetState(ServiceState.StartingRelay, "Using the relay already running.");
                Debug.Log($"[LocalServices] Relay already listening at {Origin} — reusing it.");
                yield break;
            }

            SetState(ServiceState.ProvisioningNode, "Checking for Node.js…");

            NodeProvisioner.Result nodeResult = default;
            yield return NodeProvisioner.Ensure(
                r => nodeResult = r,
                p => SetState(ServiceState.ProvisioningNode,
                    $"Downloading Node.js (first run only)… {p * 100f:0}%"));

            if (!nodeResult.Ok)
            {
                Fail(nodeResult.Error, nodeResult.Detail);
                yield break;
            }

            if (nodeResult.Downloaded)
            {
                Debug.Log($"[LocalServices] Downloaded a portable Node.js runtime to {nodeResult.NodePath}.");
            }

            var node = nodeResult.NodePath;

            if (!Directory.Exists(Path.Combine(relayDir, "node_modules")))
            {
                SetState(ServiceState.InstallingDependencies, "First run — installing relay dependencies…");
                yield return InstallDependencies(relayDir, node);
                if (State == ServiceState.Failed) yield break;
            }

            SetState(ServiceState.StartingRelay, "Starting the relay…");

            _relay = Spawn(node, "server.js", relayDir);
            if (_relay == null)
            {
                Fail("Could not start the relay.", "See the console for the underlying error.");
                yield break;
            }

            _ownsRelay = true;

            var deadline = Time.realtimeSinceStartup + relayStartTimeout;
            var healthy = false;

            while (!healthy && Time.realtimeSinceStartup < deadline)
            {
                if (_relay.HasExited)
                {
                    Fail($"The relay exited immediately (code {_relay.ExitCode}).",
                         $"Something else may be holding the port in {Origin}. Check the console output.");
                    yield break;
                }

                yield return ProbeHealth(ok => healthy = ok);
                if (!healthy) yield return new WaitForSecondsRealtime(0.25f);
            }

            if (!healthy)
            {
                Fail("The relay never answered its health check.",
                     $"It started but nothing is answering at {HealthUrl}.");
            }
        }

        private IEnumerator InstallDependencies(string relayDir, string node)
        {
            // Running npm's own CLI script through the same node.exe we already resolved
            // (system or downloaded) means npm itself never has to be found separately —
            // it ships inside every Node install, system or portable, at this same
            // relative path. Only falls back to the cmd.exe shim if that layout is somehow
            // missing (an unusual Node install), rather than failing outright.
            var npmCli = NodeProvisioner.NpmCliPathFor(node);
            var install = npmCli != null
                ? Spawn(node, $"\"{npmCli}\" install --omit=dev --no-audit --no-fund", relayDir)
                : Spawn("cmd.exe", "/c npm install --omit=dev --no-audit --no-fund", relayDir);

            if (install == null)
            {
                Fail("Could not run npm install.", $"Run it yourself in {relayDir} and relaunch.");
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + dependencyInstallTimeout;
            while (!install.HasExited && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(0.25f);
            }

            if (!install.HasExited)
            {
                ProcessUtil.TryKill(install);
                Fail("npm install timed out.", $"Run it yourself in {relayDir} and relaunch.");
                yield break;
            }

            if (install.ExitCode != 0)
            {
                Fail($"npm install failed (exit code {install.ExitCode}).",
                     $"Run it yourself in {relayDir} to see why.");
            }
        }

        /// <summary>Single GET against /health. Invokes the callback with the result either way.</summary>
        private IEnumerator ProbeHealth(Action<bool> onResult)
        {
            using var request = UnityWebRequest.Get(ProbeHealthUrl);
            request.timeout = 2;

            // UnityWebRequest.Dispose() does not dispose a custom certificateHandler — this
            // runs on a 0.25s poll during startup, so an unfreed one here would add up.
            using var handler = useHttps && _certificate.Ok ? new AcceptAnyCertificate() : null;
            if (handler != null) request.certificateHandler = handler;

            yield return request.SendWebRequest();
            onResult(request.result == UnityWebRequest.Result.Success);
        }

        [Serializable]
        private struct HealthDns
        {
            public bool enabled;
            // Wire name from server.js's /health handler is "bound", not "isBound".
            // JsonUtility does not fail or even warn on an unmatched field name — it just
            // leaves this at its default (false) — so a mismatch here reads as a permanent,
            // silent DNS-bind failure regardless of what the relay actually reports.
            public bool bound;
        }

        [Serializable]
        private struct HealthPayload
        {
            public bool ok;
            public HealthDns dns;
        }

        /// <summary>
        /// Probes /health and reports whether the DNS responder has actually finished
        /// binding — a plain health-endpoint success only proves the HTTP listener is up.
        /// </summary>
        /// <remarks>
        /// relay/server.js initiates the UDP :53 bind inside its httpServer.listen()
        /// callback but does not await it before that callback returns, so /health can
        /// answer before the bind has resolved one way or the other. HotspotService needs
        /// the stronger guarantee before starting tethering, since the responder must win a
        /// race against ICS for :53 — this turns that inferred timing into a confirmed one.
        /// </remarks>
        public IEnumerator ProbeDnsBound(Action<bool> onResult)
        {
            using var request = UnityWebRequest.Get(ProbeHealthUrl);
            request.timeout = 2;

            using var handler = useHttps && _certificate.Ok ? new AcceptAnyCertificate() : null;
            if (handler != null) request.certificateHandler = handler;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onResult(false);
                yield break;
            }

            try
            {
                var payload = JsonUtility.FromJson<HealthPayload>(request.downloadHandler.text);
                onResult(payload.ok && payload.dns.bound);
            }
            catch
            {
                onResult(false);
            }
        }

        // ------------------------------------------------------------------ processes

        private Process Spawn(string fileName, string arguments, string workingDirectory)
        {
            var env = new List<KeyValuePair<string, string>>();

            // The certificate goes on first so relay-env.txt can still override it. That
            // ordering is the point: a developer pinning a hand-made certificate or a
            // different port must win over anything derived automatically, or the escape
            // hatch is not one.
            if (_certificate.Ok)
            {
                env.Add(new KeyValuePair<string, string>("TLS_CERT", _certificate.CertPath));
                env.Add(new KeyValuePair<string, string>("TLS_KEY", _certificate.KeyPath));
                env.Add(new KeyValuePair<string, string>("PORT", securePort.ToString()));
                Debug.Log($"[LocalServices] Serving HTTPS for {_certificate.HostName} on :{securePort}.");
            }

            // Hotspot is the only mode there is now, so this always applies. Env-gated on the
            // relay side too: server.js's DNS branch does nothing unless DNS_RESPOND_IP is set.
            env.Add(new KeyValuePair<string, string>("DNS_RESPOND_IP", HotspotHostAddress));
            env.Add(new KeyValuePair<string, string>("DNS_ALLOW", "192.168.137.0/24"));
            // DNS_NAME, not DNS_SUFFIX — the certificate is single-name now
            // (MagicDomain.HostName), so dns.js is told to answer exactly this name
            // rather than a wildcard suffix. See relay/dns.js's `name` option and
            // docs/plans/magic-domain-replacement.md Phase 1 for why a wildcard is no
            // longer needed once the hotspot's address is a fixed constant.
            env.Add(new KeyValuePair<string, string>("DNS_NAME", MagicDomain.HostName));
            Debug.Log("[LocalServices] DNS responder will bind on 192.168.137.0/24.");

            // Before the overrides, like the certificate, so relay-env.txt can still win.
            var shareInternet = MatchSettingsStore.Load().ShareInternet;
            if (shareInternet) env.Add(new KeyValuePair<string, string>("SHARE_INTERNET", "1"));
            Debug.Log($"[LocalServices] DNS mode: {(shareInternet ? "sharing internet (real lookups allowed)" : "game-only")}.");

            var overrides = ReadRelayEnvironment();
            env.AddRange(overrides);

            if (overrides.Count > 0)
            {
                // Names only — one of these is a path to a private key.
                Debug.Log($"[LocalServices] {EnvFileName} applied: " +
                          string.Join(", ", overrides.ConvertAll(e => e.Key)));
            }

            return ProcessUtil.Spawn(fileName, arguments, workingDirectory, env, _group, _output);
        }

        private void Shutdown()
        {
            // A relay we merely borrowed belongs to whoever started it.
            if (_ownsRelay) ProcessUtil.TryKill(_relay);
            _relay = null;

            // Closes the job object, which terminates anything the kills above missed.
            _group?.Dispose();
            _group = null;
        }

        /// <summary>
        /// Must override rather than declare: SingletonBase.OnApplicationQuit is virtual
        /// and sets the flag that stops Instance resurrecting a destroyed singleton
        /// during teardown. A private method of the same name would hide it, Unity would
        /// deliver the message here only, and that flag would never be set.
        /// </summary>
        protected override void OnApplicationQuit()
        {
            base.OnApplicationQuit();
            Shutdown();
        }

        protected override void OnDestroy()
        {
            Shutdown();
            base.OnDestroy();
        }

        // ------------------------------------------------------------------ resolution

        /// <summary>
        /// The override file's origin, or loopback on the inspector port when there is none.
        /// </summary>
        private string ResolveOrigin()
        {
            var overridden = RelayClient.ReadOverride();
            if (overridden != null)
            {
                var origin = RelayClient.DeriveOrigin(overridden);
                if (origin != null) return origin;
            }

            return $"http://127.0.0.1:{relayPort}";
        }

        /// <summary>
        /// Per-machine environment for the relay child, one KEY=VALUE per line, blank lines
        /// and # comments ignored. Absent on almost every machine, and absent means the child
        /// starts exactly as it always has.
        /// </summary>
        /// <remarks>
        /// Deliberately generic. This class stays ignorant of what the variables mean, which
        /// is what keeps configuration the relay only needs in development — TLS material, a
        /// different port — out of a component that also runs in a shipped build.
        /// </remarks>
        private static List<KeyValuePair<string, string>> ReadRelayEnvironment()
        {
            var result = new List<KeyValuePair<string, string>>();

            try
            {
                var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", EnvFileName));
                if (!File.Exists(path)) return result;

                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    var split = line.IndexOf('=');
                    if (split <= 0) continue;

                    result.Add(new KeyValuePair<string, string>(
                        line[..split].Trim(), line[(split + 1)..].Trim()));
                }
            }
            catch (Exception e)
            {
                // Same reasoning as the URL override: unreadable config must not stop a boot.
                Debug.LogWarning($"[LocalServices] Could not read {EnvFileName}: {e.Message}");
            }

            return result;
        }

        /// <summary>
        /// Locates the relay folder. Works out to the same expression in both cases:
        /// in the editor dataPath is &lt;project&gt;/Assets, and in a player build it is
        /// &lt;exe folder&gt;/&lt;Product&gt;_Data, so the parent is the right place both times.
        /// </summary>
        private static string ResolveRelayDirectory()
        {
            var candidate = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "relay"));
            return File.Exists(Path.Combine(candidate, "server.js")) ? candidate : null;
        }

        // ------------------------------------------------------------------ state

        private void SetState(ServiceState state, string message)
        {
            State = state;
            StatusMessage = message;
            StateChanged?.Invoke(state);
        }

        private void Fail(string message, string hint)
        {
            FailureHint = hint;
            SetState(ServiceState.Failed, message);
            Debug.LogError($"[LocalServices] {message}\n{hint}");
        }
    }
}
