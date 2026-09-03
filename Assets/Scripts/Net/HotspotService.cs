using System;
using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Singleton;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Net
{
    /// <summary>How far along starting this PC's Mobile Hotspot is. Drives what the TV shows.</summary>
    public enum HotspotState
    {
        /// <summary>Mode is Lan, or LocalServices isn't running at all — nothing to do here.</summary>
        NotApplicable,
        Starting,
        Ready,
        Failed,
    }

    /// <summary>
    /// Drives Windows Mobile Hotspot for Hotspot mode via hotspot/hotspot-control.ps1 — a
    /// short-lived child process, since Unity/Mono cannot call the WinRT tethering APIs
    /// directly. See docs/plans/network-mode-selection.md.
    /// </summary>
    /// <remarks>
    /// Waits for LocalServices to reach Ready, and specifically for its DNS responder to have
    /// actually finished binding :53 (LocalServices.ProbeDnsBound — Ready alone only proves the
    /// HTTP listener is up), before starting tethering. The responder must hold :53 before ICS
    /// starts, or phones on the hotspot cannot resolve the game's address at all.
    /// </remarks>
    public class HotspotService : SingletonBase<HotspotService>
    {
        [Tooltip("Seconds to wait for hotspot-control.ps1 -Action Start before giving up.")]
        [SerializeField] private float startTimeout = 30f;

        [Tooltip("Seconds to wait for -Action Stop before killing it outright on the way out.")]
        [SerializeField] private float stopTimeout = 3f;

        // Excludes visually ambiguous characters (0/O, 1/l/I) — the passphrase may need to be
        // typed by hand if a QR scan fails.
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

        public HotspotState State { get; private set; } = HotspotState.NotApplicable;

        /// <summary>One line for the TV describing what is happening right now.</summary>
        public string StatusMessage { get; private set; }

        /// <summary>On failure, what the person standing at the PC should actually do about it.</summary>
        public string FailureHint { get; private set; }

        /// <summary>Fresh per session and never derived from the room code — see ZeroSetupPlan.md 4.1.</summary>
        public string Ssid { get; private set; }
        public string Passphrase { get; private set; }

        public event Action<HotspotState> StateChanged;

        private bool _ownedTethering;
        private ChildProcessGroup _group;

        [Serializable]
        private struct ScriptResult
        {
            public bool ok;
            public string ssid;
            public string passphrase;
            public string warning;
            public string error;
            public string message;
        }

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();
        }

        private void Start()
        {
            _group = ChildProcessGroup.Create();

            if (!LocalServices.HasInstance) return; // no local relay, nothing to tether for

            LocalServices.Instance.StateChanged += OnLocalServicesStateChanged;
            OnLocalServicesStateChanged(LocalServices.Instance.State); // in case it's already settled
        }

        private void OnLocalServicesStateChanged(ServiceState state)
        {
            if (state == ServiceState.Failed)
            {
                LocalServices.Instance.StateChanged -= OnLocalServicesStateChanged;
                SetState(HotspotState.NotApplicable, "Local relay failed — hotspot not started.");
                return;
            }

            if (state != ServiceState.Ready) return; // still settling

            LocalServices.Instance.StateChanged -= OnLocalServicesStateChanged;

            if (LocalServices.Instance.Mode != NetworkMode.Hotspot)
            {
                SetState(HotspotState.NotApplicable, "LAN mode — no hotspot needed.");
                return;
            }

            StartCoroutine(BeginTethering());
        }

        private IEnumerator BeginTethering()
        {
            SetState(HotspotState.Starting, "Confirming the local DNS responder…");

            var dnsBound = false;
            yield return LocalServices.Instance.ProbeDnsBound(ok => dnsBound = ok);

            if (!dnsBound)
            {
                Fail("The local DNS responder never bound.",
                     "Port 53 may already be in use by something else, or the responder " +
                     "failed to start. Restart and try again.");
                yield break;
            }

            var scriptPath = ResolveScriptPath();
            if (scriptPath == null)
            {
                Fail("Can't find hotspot-control.ps1.",
                     "Expected a 'hotspot' folder next to Assets/.");
                yield break;
            }

            var powershell = ProcessUtil.ResolveExecutable("powershell",
                new[] { @"C:\Windows\System32\WindowsPowerShell\v1.0" });
            if (powershell == null)
            {
                Fail("powershell.exe not found.",
                     "Expected Windows PowerShell 5.1 on PATH or at its default location.");
                yield break;
            }

            var setupOk = false;
            yield return EnsureOfflineSetup(powershell, ok => setupOk = ok);
            if (!setupOk) yield break; // EnsureOfflineSetup already called Fail with the specific reason

            Ssid = GenerateSsid();
            Passphrase = GeneratePassphrase();

            SetState(HotspotState.Starting, "Starting the hotspot…");

            var stdout = new ConcurrentQueue<string>();
            var stderr = new ConcurrentQueue<string>();
            var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                       $"-Action Start -Ssid \"{Ssid}\" -Passphrase \"{Passphrase}\"";

            var process = ProcessUtil.Spawn(powershell, args, Path.GetDirectoryName(scriptPath),
                null, _group, stdout, stderr);
            if (process == null)
            {
                Fail("Could not start hotspot-control.ps1.", "See the console for the underlying error.");
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + startTimeout;
            while (!process.HasExited && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(0.1f);
            }

            LogStderr(stderr);

            if (!process.HasExited)
            {
                ProcessUtil.TryKill(process);
                Fail("hotspot-control.ps1 timed out.",
                     "Windows may be showing a permission prompt off-screen.");
                yield break;
            }

            var result = ParseResult(stdout);
            if (result == null || !result.Value.ok)
            {
                var error = result?.error ?? "no-output";
                var message = result?.message ?? "hotspot-control.ps1 produced no parseable result.";
                Fail($"Could not start the hotspot ({error}).", message);
                yield break;
            }

            if (!string.IsNullOrEmpty(result.Value.warning))
            {
                // The hotspot came up but forwarding-disable could not be confirmed — that
                // means it is NOT isolated. Treated as a failure rather than a soft caveat,
                // because running an open hotspot silently defeats the reason this exists.
                // Best-effort tear-down rather than leave it running unconfirmed.
                _ownedTethering = true;
                StopTetheringBlocking();
                _ownedTethering = false;
                Fail("The hotspot started but IP forwarding could not be confirmed disabled.",
                     result.Value.warning);
                yield break;
            }

            _ownedTethering = true;
            SetState(HotspotState.Ready, $"Hotspot \"{Ssid}\" ready.");
        }

        /// <summary>
        /// Registers the one-time, elevated, per-machine prerequisites Hotspot mode needs —
        /// the same three things deploy/setup-offline.ps1 has always set up by hand (the
        /// forwarding-disable scheduled task, firewall rules, and the host's own hosts entry)
        /// — automatically, the first time this machine ever reaches this point, instead of
        /// requiring someone to find and run that script themselves first.
        /// </summary>
        /// <remarks>
        /// Windows itself still requires one UAC prompt — there is no way around that for an
        /// operation this privileged, and there should not be — but it now happens in place,
        /// triggered by pressing Start, rather than being a separate manual step nobody is
        /// told about until hotspot-control.ps1 fails four steps later with a cryptic
        /// scheduled-task error. Every later Play session on this machine skips straight past
        /// this: the cheap unelevated check below finds the task already registered.
        /// </remarks>
        private IEnumerator EnsureOfflineSetup(string powershell, Action<bool> onResult)
        {
            // Cheap and unelevated — just asks whether deploy/setup-offline.ps1's own
            // Sync-ForwardingTask step has ever run on this machine. Checking for the task
            // specifically, not some other marker, keeps this in lockstep with what
            // setup-offline.ps1 itself considers "already set up" (see its own
            // Get-ScheduledTask probe).
            var checkStdout = new ConcurrentQueue<string>();
            var checkArgs = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                "\"if (Get-ScheduledTask -TaskName 'RockSkippingDisableForwarding' " +
                "-ErrorAction SilentlyContinue) { 'present' }\"";
            var checkProcess = ProcessUtil.Spawn(powershell, checkArgs, Application.dataPath,
                null, _group, checkStdout);
            if (checkProcess == null)
            {
                Fail("Could not check one-time machine setup.", "See the console for the underlying error.");
                onResult(false);
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + startTimeout;
            while (!checkProcess.HasExited && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(0.1f);
            }

            var present = false;
            while (checkStdout.TryDequeue(out var line))
            {
                if (line.Trim() == "present") present = true;
            }

            if (present)
            {
                onResult(true);
                yield break;
            }

            var scriptPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "deploy", "setup-offline.ps1"));
            if (!File.Exists(scriptPath))
            {
                Fail("Can't find deploy/setup-offline.ps1.", "Expected a 'deploy' folder next to Assets/.");
                onResult(false);
                yield break;
            }

            SetState(HotspotState.Starting,
                "First-time setup — click Yes on the Administrator prompt to finish configuring " +
                "this PC for offline hosting…");

            // cmd.exe wraps the elevated call purely so its output can be redirected to a file:
            // Verb=runas requires ShellExecute, which cannot redirect stdio the way
            // ProcessUtil.Spawn's non-elevated processes do, so a log file is the only way to
            // see what setup-offline.ps1 actually did if this fails.
            var logPath = Path.Combine(Application.persistentDataPath, "setup-offline.log");
            var elevatedArgs = $"/c \"\"{powershell}\" -NoProfile -NonInteractive " +
                $"-ExecutionPolicy Bypass -File \"{scriptPath}\" > \"{logPath}\" 2>&1\"";

            Process elevated = null;
            try
            {
                elevated = new Process
                {
                    StartInfo = new ProcessStartInfo("cmd.exe", elevatedArgs)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden,
                    },
                };
                elevated.Start();
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED — clicked No
            {
                Fail("Setup needs Administrator.",
                     "Click Yes on the prompt next time — offline hosting can't configure this " +
                     "machine without it. Nothing was changed.");
                onResult(false);
                yield break;
            }
            catch (Exception e)
            {
                Fail("Could not start the elevated one-time setup.", e.Message);
                onResult(false);
                yield break;
            }

            // setup-offline.ps1 touches the scheduled-task store and the firewall, which is
            // slower than the ordinary hotspot-control.ps1 round trip this class otherwise
            // times against — give it more room rather than share startTimeout.
            deadline = Time.realtimeSinceStartup + startTimeout * 3f;
            while (!elevated.HasExited && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(0.2f);
            }

            if (!elevated.HasExited)
            {
                Fail("The one-time setup is taking too long.",
                     "Check for a UAC prompt or console window on the taskbar and finish it, " +
                     "or restart and try again.");
                onResult(false);
                yield break;
            }

            if (elevated.ExitCode != 0)
            {
                Fail($"One-time machine setup failed (exit {elevated.ExitCode}).",
                     $"See {logPath} for what setup-offline.ps1 reported.");
                onResult(false);
                yield break;
            }

            onResult(true);
        }

        // ------------------------------------------------------------------ generation

        private static string GenerateSsid() => $"Rock Skipping {RandomAlphanumeric(4)}";

        /// <summary>WPA2 allows 8-63 chars; 12 is comfortably strong and still typeable.</summary>
        private static string GeneratePassphrase() => RandomAlphanumeric(12);

        private static string RandomAlphanumeric(int length)
        {
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);

            var chars = new char[length];
            for (var i = 0; i < length; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
            return new string(chars);
        }

        // ------------------------------------------------------------------ process plumbing

        private static string ResolveScriptPath()
        {
            var candidate = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "hotspot", "hotspot-control.ps1"));
            return File.Exists(candidate) ? candidate : null;
        }

        private static void LogStderr(ConcurrentQueue<string> stderr)
        {
            while (stderr.TryDequeue(out var line)) Debug.Log($"[HotspotService] {line}");
        }

        private static ScriptResult? ParseResult(ConcurrentQueue<string> stdout)
        {
            string last = null;
            while (stdout.TryDequeue(out var line))
            {
                if (!string.IsNullOrWhiteSpace(line)) last = line;
            }

            if (last == null) return null;

            try
            {
                return JsonUtility.FromJson<ScriptResult>(last);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HotspotService] Could not parse hotspot-control.ps1 output: " +
                                 $"{e.Message}\n{last}");
                return null;
            }
        }

        /// <summary>
        /// Synchronous by design, matching LocalServices.Shutdown() — OnApplicationQuit/
        /// OnDestroy cannot reliably drive a coroutine to completion, and only stopping what
        /// we started here (mirrors LocalServices._ownsRelay) means a hotspot this instance
        /// never brought up is never touched.
        /// </summary>
        private void StopTetheringBlocking()
        {
            var scriptPath = ResolveScriptPath();
            if (scriptPath == null) return;

            var powershell = ProcessUtil.ResolveExecutable("powershell",
                new[] { @"C:\Windows\System32\WindowsPowerShell\v1.0" });
            if (powershell == null) return;

            var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" -Action Stop";
            var process = ProcessUtil.Spawn(powershell, args, Path.GetDirectoryName(scriptPath),
                null, _group, null);
            if (process == null) return;

            try
            {
                if (!process.WaitForExit((int)(stopTimeout * 1000)))
                {
                    Debug.LogWarning("[HotspotService] Stop timed out; killing it. Tethering may " +
                                     "briefly still be on until Windows notices the process died.");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HotspotService] Error waiting for hotspot-control.ps1 stop: {e.Message}");
            }
            finally
            {
                ProcessUtil.TryKill(process); // no-op if it already exited cleanly
            }
        }

        private void Shutdown()
        {
            if (_ownedTethering)
            {
                _ownedTethering = false;
                StopTetheringBlocking();
            }

            // Closes the job object, which terminates anything the kill above missed.
            _group?.Dispose();
            _group = null;
        }

        /// <summary>
        /// Must override rather than declare: SingletonBase.OnApplicationQuit is virtual and
        /// sets the flag that stops Instance resurrecting a destroyed singleton during
        /// teardown. See LocalServices for the same reasoning.
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

        // ------------------------------------------------------------------ state

        private void SetState(HotspotState state, string message)
        {
            State = state;
            StatusMessage = message;
            StateChanged?.Invoke(state);
        }

        private void Fail(string message, string hint)
        {
            FailureHint = hint;
            SetState(HotspotState.Failed, message);
            Debug.LogError($"[HotspotService] {message}\n{hint}");
        }
    }
}
