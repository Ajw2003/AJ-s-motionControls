using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Singleton;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Net
{
    /// <summary>How far along the one-click launch is. Drives what the TV shows.</summary>
    public enum ServiceState
    {
        Idle,
        InstallingDependencies,
        StartingRelay,
        OpeningTunnel,
        Ready,
        Failed,
    }

    /// <summary>
    /// Brings up everything a phone needs in order to join, as a side effect of pressing
    /// Play: the relay process and a public HTTPS tunnel to it.
    /// </summary>
    /// <remarks>
    /// The point is that hosting a game is one action. Previously it was three terminals
    /// and a hand-copied URL, which is fine for the person who built it and a non-starter
    /// for a party.
    ///
    /// One tunnel is enough for both jobs because the relay serves the controller page
    /// from the same origin it accepts WebSockets on (see server.js). The page therefore
    /// arrives over HTTPS — which the motion sensors require — and upgrades to wss:// on
    /// the same host, so there is no mixed-content problem to fight.
    ///
    /// The host itself does NOT go through the tunnel. RelayClient connects to
    /// ws://localhost:3000 directly, because routing the PC's own traffic through
    /// Cloudflare and back would add a datacenter round trip to every aim frame for no
    /// benefit. Only the phones need the public address.
    /// </remarks>
    public class LocalServices : SingletonBase<LocalServices>
    {
        [Tooltip("Bring up the relay and tunnel on Play. Turn off if you run them yourself in a terminal.")]
        [SerializeField] private bool launchOnStart = true;

        [Tooltip("Must match the port in RelayClient's relayUrl.")]
        [SerializeField] private int relayPort = 3000;

        [Tooltip("Echo relay and tunnel output into the Unity console.")]
        [SerializeField] private bool verboseLogging;

        [Header("Timeouts (seconds)")]
        [SerializeField] private float dependencyInstallTimeout = 180f;
        [SerializeField] private float relayStartTimeout = 30f;
        [SerializeField] private float tunnelStartTimeout = 60f;

        public ServiceState State { get; private set; } = ServiceState.Idle;

        /// <summary>Public HTTPS origin for the controller page. Null until the tunnel is up.</summary>
        public string PublicUrl { get; private set; }

        /// <summary>One line for the TV describing what is happening right now.</summary>
        public string StatusMessage { get; private set; } = "Starting up…";

        /// <summary>On failure, what the person standing at the PC should actually do about it.</summary>
        public string FailureHint { get; private set; }

        public int RelayPort => relayPort;

        public event Action<ServiceState> StateChanged;

        private Process _relay;
        private Process _tunnel;
        private bool _ownsRelay;
        private ChildProcessGroup _group;

        private readonly ConcurrentQueue<string> _output = new();

        // cloudflared prints the assigned hostname inside a banner box on stderr; there
        // is no machine-readable channel for it on a quick tunnel, so it gets scraped.
        private static readonly Regex TunnelUrlPattern =
            new(@"https://[a-z0-9][a-z0-9\-]*\.trycloudflare\.com", RegexOptions.IgnoreCase);

        private string HealthUrl => $"http://127.0.0.1:{relayPort}/health";

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();
        }

        private void Start()
        {
            if (!launchOnStart)
            {
                SetState(ServiceState.Idle, "Auto-launch off — start the relay and tunnel yourself.");
                return;
            }

            _group = ChildProcessGroup.Create();
            StartCoroutine(LaunchSequence());
        }

        private void Update()
        {
            // Process output arrives on threadpool threads; everything that touches Unity
            // or mutates state happens here instead.
            while (_output.TryDequeue(out var line))
            {
                if (verboseLogging) Debug.Log($"[LocalServices] {line}");

                if (PublicUrl != null) continue;

                var match = TunnelUrlPattern.Match(line);
                if (match.Success)
                {
                    PublicUrl = match.Value;
                    Debug.Log($"[LocalServices] Tunnel open at {PublicUrl}");
                }
            }
        }

        /// <summary>The address a phone should open, with the room code pre-filled.</summary>
        public string JoinUrlFor(string roomCode)
        {
            if (string.IsNullOrEmpty(PublicUrl)) return null;
            return string.IsNullOrEmpty(roomCode) ? PublicUrl : $"{PublicUrl}/?room={roomCode}";
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

            yield return StartRelay(relayDir);
            if (State == ServiceState.Failed) yield break;

            yield return StartTunnel();
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
                Debug.Log($"[LocalServices] Relay already listening on :{relayPort} — reusing it.");
                yield break;
            }

            if (!Directory.Exists(Path.Combine(relayDir, "node_modules")))
            {
                SetState(ServiceState.InstallingDependencies, "First run — installing relay dependencies…");
                yield return InstallDependencies(relayDir);
                if (State == ServiceState.Failed) yield break;
            }

            SetState(ServiceState.StartingRelay, "Starting the relay…");

            var node = ResolveExecutable("node", new[] { @"C:\Program Files\nodejs" });
            if (node == null)
            {
                Fail("Node.js not found.",
                     "Install Node 20 or newer from nodejs.org and relaunch. " +
                     "LocalServices searched PATH and C:\\Program Files\\nodejs.");
                yield break;
            }

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
                         $"Something else may be holding port {relayPort}. Check the console output.");
                    yield break;
                }

                yield return ProbeHealth(ok => healthy = ok);
                if (!healthy) yield return new WaitForSecondsRealtime(0.25f);
            }

            if (!healthy)
            {
                Fail("The relay never answered its health check.",
                     $"It started but nothing is responding on port {relayPort}.");
            }
        }

        private IEnumerator StartTunnel()
        {
            SetState(ServiceState.OpeningTunnel, "Opening the public tunnel…");

            var cloudflared = ResolveExecutable("cloudflared", new[]
            {
                @"C:\Program Files (x86)\cloudflared",
                @"C:\Program Files\cloudflared",
                // winget drops a shim here, which is on PATH only after a new sign-in.
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Microsoft", "WinGet", "Links"),
            });

            if (cloudflared == null)
            {
                Fail("cloudflared not found.",
                     "Install it with:  winget install Cloudflare.cloudflared\n" +
                     "Then relaunch. Players cannot join without an HTTPS address — phone " +
                     "motion sensors refuse to work on a plain http:// page.");
                yield break;
            }

            // --no-autoupdate: an update mid-party would restart the tunnel and hand out
            // a different hostname, silently invalidating every QR code on screen.
            _tunnel = Spawn(cloudflared,
                            $"tunnel --no-autoupdate --url http://localhost:{relayPort}",
                            Path.GetDirectoryName(cloudflared));

            if (_tunnel == null)
            {
                Fail("Could not start cloudflared.", "See the console for the underlying error.");
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + tunnelStartTimeout;

            while (PublicUrl == null && Time.realtimeSinceStartup < deadline)
            {
                if (_tunnel.HasExited)
                {
                    Fail($"cloudflared exited before giving us a URL (code {_tunnel.ExitCode}).",
                         "Quick tunnels are rate limited — wait a minute and try again, or " +
                         "check your internet connection. Turn on Verbose Logging to see its output.");
                    yield break;
                }

                yield return null;
            }

            if (PublicUrl == null)
            {
                Fail("The tunnel did not come up in time.",
                     "cloudflared is running but never printed a trycloudflare.com address. " +
                     "Turn on Verbose Logging to see what it is saying.");
                yield break;
            }

            SetState(ServiceState.Ready, "Ready — phones can join.");
        }

        private IEnumerator InstallDependencies(string relayDir)
        {
            // npm is a .cmd shim on Windows, which CreateProcess cannot launch directly.
            var install = Spawn("cmd.exe", "/c npm install --no-audit --no-fund", relayDir);
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
                TryKill(install);
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
            using var request = UnityWebRequest.Get(HealthUrl);
            request.timeout = 2;
            yield return request.SendWebRequest();
            onResult(request.result == UnityWebRequest.Result.Success);
        }

        // ------------------------------------------------------------------ processes

        private Process Spawn(string fileName, string arguments, string workingDirectory)
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                var process = new Process { StartInfo = info, EnableRaisingEvents = true };

                // Both streams are drained. A redirected stream that nobody reads fills
                // its pipe buffer and blocks the child permanently — cloudflared is
                // chatty enough to hit that within seconds.
                process.OutputDataReceived += (_, e) => { if (e.Data != null) _output.Enqueue(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) _output.Enqueue(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                _group?.Adopt(process);

                Debug.Log($"[LocalServices] Started {Path.GetFileName(fileName)} {arguments} (pid {process.Id})");
                return process;
            }
            catch (Exception e)
            {
                Debug.LogError($"[LocalServices] Failed to start {fileName}: {e.Message}");
                return null;
            }
        }

        private static void TryKill(Process process)
        {
            if (process == null) return;

            try
            {
                if (!process.HasExited) process.Kill();
                process.WaitForExit(2000);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LocalServices] Could not stop pid: {e.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        private void Shutdown()
        {
            TryKill(_tunnel);
            _tunnel = null;

            // A relay we merely borrowed belongs to whoever started it.
            if (_ownsRelay) TryKill(_relay);
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
        /// Locates the relay folder. Works out to the same expression in both cases:
        /// in the editor dataPath is &lt;project&gt;/Assets, and in a player build it is
        /// &lt;exe folder&gt;/&lt;Product&gt;_Data, so the parent is the right place both times.
        /// </summary>
        private static string ResolveRelayDirectory()
        {
            var candidate = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "relay"));
            return File.Exists(Path.Combine(candidate, "server.js")) ? candidate : null;
        }

        /// <summary>
        /// Finds an executable on PATH, then in a list of known install locations.
        /// The fallbacks matter: an installer that edits PATH does not change the
        /// environment of an already-running Unity, so a freshly installed tool is
        /// invisible until a restart that nobody realises they need.
        /// </summary>
        private static string ResolveExecutable(string name, IEnumerable<string> extraDirectories)
        {
            var extensions = Application.platform == RuntimePlatform.WindowsEditor ||
                             Application.platform == RuntimePlatform.WindowsPlayer
                ? new[] { ".exe", ".cmd", ".bat", "" }
                : new[] { "" };

            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator);

            foreach (var directory in JoinDirectories(pathDirs, extraDirectories))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;

                foreach (var extension in extensions)
                {
                    string full;
                    try
                    {
                        full = Path.Combine(directory.Trim(), name + extension);
                    }
                    catch (ArgumentException)
                    {
                        break; // a malformed PATH entry; skip the whole directory
                    }

                    if (File.Exists(full)) return full;
                }
            }

            return null;
        }

        private static IEnumerable<string> JoinDirectories(IEnumerable<string> first, IEnumerable<string> second)
        {
            foreach (var item in first) yield return item;
            if (second == null) yield break;
            foreach (var item in second) yield return item;
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
