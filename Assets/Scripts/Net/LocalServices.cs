using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        InstallingDependencies,
        StartingRelay,
        Ready,
        Failed,
    }

    /// <summary>
    /// Runs a relay on this machine, for development only.
    /// </summary>
    /// <remarks>
    /// OFF BY DEFAULT, and deliberately so. Hosts connect out to the deployed service
    /// (see RelayClient and HostingPlan.md), which is what makes hosting require no setup
    /// at all — there is nothing to install, nothing to configure and nothing listening.
    ///
    /// This exists for the local edit-test loop, where deploying on every change would be
    /// absurd. Turning it on is only half the job: RelayClient must also be pointed at the
    /// local instance, by putting `ws://localhost:3000` in a relay-url.txt beside the
    /// executable. Enabling this alone starts a relay that nothing connects to.
    ///
    /// That plain-http origin is not a secure context, so motion sensors stay dark on it.
    /// For a loop that does unlock them, deploy/local-https.ps1 writes both that file and a
    /// relay-env.txt, and this class picks up whichever of the two it finds.
    ///
    /// This class used to also spawn a cloudflared tunnel and scrape the assigned hostname
    /// out of its stderr, because a quick tunnel offers no machine-readable channel for it.
    /// A fixed service address removed the need for both.
    /// </remarks>
    public class LocalServices : SingletonBase<LocalServices>
    {
        /// <summary>
        /// Optional per-machine environment for the relay child, beside relay-url.txt.
        /// Gitignored. Written by deploy/local-https.ps1; absent otherwise.
        /// </summary>
        private const string EnvFileName = "relay-env.txt";

        [Tooltip("Start a relay on this machine. Development only — you must also point " +
                 "RelayClient at it with a relay-url.txt containing ws://localhost:3000.")]
        [SerializeField] private bool launchOnStart = true;

        [Tooltip("Fallback only — the port from the relay-url.txt override wins when there is one.")]
        [SerializeField] private int relayPort = 3000;

        [Tooltip("Echo relay output into the Unity console.")]
        [SerializeField] private bool verboseLogging;

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

        /// <summary>
        /// Where the relay actually lives, derived from the same relay-url.txt RelayClient
        /// reads so the two can never disagree about it.
        /// </summary>
        /// <remarks>
        /// This used to be hardcoded to http://127.0.0.1:3000, which quietly broke the reuse
        /// path below for any relay not on that address: a relay started by hand on another
        /// port looked absent, so this spawned a second one that nothing ever connected to.
        ///
        /// It probes the name rather than loopback because that is the address the certificate
        /// is issued for when the relay is holding one. Probing 127.0.0.1 over https would
        /// need a certificate-validation override, and there is not one anywhere in this
        /// codebase — worth keeping that way.
        /// </remarks>
        private string Origin => _origin ??= ResolveOrigin();

        private string HealthUrl => $"{Origin}/health";

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();
        }

        private void Start()
        {
            if (!launchOnStart)
            {
                SetState(ServiceState.Idle, "Local relay off — using the deployed service.");
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

            yield return StartRelay(relayDir);
            if (State == ServiceState.Failed) yield break;

            SetState(ServiceState.Ready, $"Local relay at {Origin}.");
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

            // Populated from this process's environment, so these are additions to it.
            // Available only because UseShellExecute is false.
            var overrides = ReadRelayEnvironment();
            foreach (var entry in overrides) info.Environment[entry.Key] = entry.Value;

            if (overrides.Count > 0)
            {
                // Names only — one of these is a path to a private key.
                Debug.Log($"[LocalServices] {EnvFileName} applied: " +
                          string.Join(", ", overrides.ConvertAll(e => e.Key)));
            }

            try
            {
                var process = new Process { StartInfo = info, EnableRaisingEvents = true };

                // Both streams are drained. A redirected stream that nobody reads fills
                // its pipe buffer and blocks the child permanently — a chatty child
                // reaches that within seconds.
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
