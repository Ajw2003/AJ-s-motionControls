using System;
using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
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
    ///
    /// Name resolution has two halves and this class gates on both, because they fail
    /// independently and look nothing alike. The responder above is how a PHONE resolves
    /// MagicDomain.HostName; VerifyHostNameResolves is how the HOST does, via the hosts entry
    /// deploy/setup-offline.ps1 writes. Phones working while the host does not is the normal
    /// shape of this being broken.
    /// </remarks>
    public class HotspotService : SingletonBase<HotspotService>
    {
        [Tooltip("Seconds to wait for hotspot-control.ps1 -Action Start before giving up.")]
        [SerializeField] private float startTimeout = 30f;

        [Tooltip("Seconds to wait for -Action Stop before killing it outright on the way out.")]
        [SerializeField] private float stopTimeout = 3f;

        /// <summary>
        /// The address Windows ICS always gives the hotspot, and therefore the only answer
        /// <see cref="MagicDomain.HostName"/> may resolve to on this machine. Asserted the same
        /// way in LocalServices.HotspotHostAddress and deploy/setup-offline.ps1.
        /// </summary>
        private const string HotspotAddress = "192.168.137.1";

        /// <summary>
        /// How long to keep asking /health whether the DNS responder has finished binding.
        /// Short: the bind is a local UDP socket that either takes microseconds or fails
        /// outright, so this is covering a scheduling gap, not waiting on real work.
        /// </summary>
        private const float DnsBindTimeout = 5f;

        public HotspotState State { get; private set; } = HotspotState.NotApplicable;

        /// <summary>One line for the TV describing what is happening right now.</summary>
        public string StatusMessage { get; private set; }

        /// <summary>On failure, what the person standing at the PC should actually do about it.</summary>
        public string FailureHint { get; private set; }

        /// <summary>Fresh per session and never derived from the room code — see ZeroSetupPlan.md 4.1.</summary>
        public string Ssid { get; private set; }
        public string Passphrase { get; private set; }

        public event Action<HotspotState> StateChanged;

        /// <summary>
        /// Non-empty when the hotspot is up but sharing one radio across two bands, which
        /// makes the 20Hz AIM stream arrive in bursts and the aim preview read as jerky and
        /// delayed. Rendered by JoinScreen; never fatal, because a laggy game still beats no
        /// game. See docs/plans/aim-preview-lag-on-shared-radio.md.
        /// </summary>
        public string RadioSharingWarning { get; private set; }

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

            // Which band the access point asked for, what the host's own station link is on,
            // and whether those could be matched. See Get-StationBandName in
            // hotspot/hotspot-control.ps1 for why a mismatch costs the aim preview.
            public string band;
            public string stationBand;
            public bool bandMatched;
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

            StartCoroutine(BeginTethering());
        }

        private IEnumerator BeginTethering()
        {
            SetState(HotspotState.Starting, "Confirming the local DNS responder…");

            // Polled, not asked once. server.js starts the :53 bind inside its listen()
            // callback without awaiting it, so /health can answer — which is all
            // LocalServices waits for before reporting Ready — while dns.bound is still
            // false for another tick or two. A single probe turns that ordinary startup
            // gap into a hard failure roughly whenever the machine is busy.
            var dnsBound = false;
            var dnsDeadline = Time.realtimeSinceStartup + DnsBindTimeout;
            while (!dnsBound && Time.realtimeSinceStartup < dnsDeadline)
            {
                yield return LocalServices.Instance.ProbeDnsBound(ok => dnsBound = ok);
                if (!dnsBound) yield return new WaitForSecondsRealtime(0.25f);
            }

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

            if (!VerifyHostNameResolves()) yield break; // ditto

            var credentials = HotspotCredentialsStore.LoadOrCreate();
            Ssid = credentials.Ssid;
            Passphrase = credentials.Passphrase;

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

            LogLines(stderr);

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

            // Not a failure, so this is checked after the fatal warning above rather than
            // beside it. bandMatched is false when the host's station link is on a band the
            // access point cannot follow it onto (6 GHz), or when the adapter refused the
            // band outright. Either way one radio is serving two channels, it time-slices,
            // and the phones feel it long before anyone thinks to blame Wi-Fi.
            //
            // Note this reports what was ASKED for. The driver places the access point, and
            // Windows exposes no way to read back the channel it chose - so the wording says
            // "may", and tools/measure-hotspot-jitter.ps1 is how you find out for certain.
            RadioSharingWarning = result.Value.bandMatched
                ? null
                : $"Your Wi-Fi ({result.Value.stationBand}) is sharing one radio with the " +
                  $"game's hotspot ({BandLabel(result.Value.band)}), so controls may feel " +
                  "laggy. Disconnect from Wi-Fi for a smooth game.";

            if (RadioSharingWarning != null) Debug.LogWarning($"[HotspotService] {RadioSharingWarning}");

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
        /// this: the cheap unelevated -DryRun below reports nothing left to change.
        /// </remarks>
        private IEnumerator EnsureOfflineSetup(string powershell, Action<bool> onResult)
        {
            var scriptPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "deploy", "setup-offline.ps1"));
            if (!File.Exists(scriptPath))
            {
                Fail("Can't find deploy/setup-offline.ps1.", "Expected a 'deploy' folder next to Assets/.");
                onResult(false);
                yield break;
            }

            // Ask the script itself whether this machine is set up, rather than re-implementing
            // the question here. -DryRun writes nothing and needs no elevation, and answers via
            // its exit code: 0 already correct, 3 setup needed, anything else a real problem.
            //
            // This used to ask a cheaper proxy instead — "does the RockSkippingDisableForwarding
            // scheduled task exist?" — and that proxy is what broke Hotspot mode on every machine
            // set up before the magic-domain replacement. The task was registered in the
            // local-ip.sh era and is still there, so setup was skipped, so the hosts entry for
            // MagicDomain.HostName was never added, so the host resolved its own relay via public
            // DNS to a parked address and retried against it forever. A proxy that stops implying
            // what it used to imply fails silently, which is the worst way for this to fail.
            var probeOutput = new ConcurrentQueue<string>();
            var probeArgs = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                            $"\"{scriptPath}\" -DryRun";
            var probe = ProcessUtil.Spawn(powershell, probeArgs, Path.GetDirectoryName(scriptPath),
                null, _group, probeOutput);
            if (probe == null)
            {
                Fail("Could not check one-time machine setup.", "See the console for the underlying error.");
                onResult(false);
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + startTimeout;
            while (!probe.HasExited && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(0.1f);
            }

            if (!probe.HasExited)
            {
                ProcessUtil.TryKill(probe);
                Fail("Checking one-time machine setup timed out.",
                     $"setup-offline.ps1 -DryRun did not finish within {startTimeout:F0}s. " +
                     "Run it yourself from the deploy folder to see where it stalls.");
                onResult(false);
                yield break;
            }

            const int alreadySetUp = 0;
            const int setupNeeded = 3;

            if (probe.ExitCode == alreadySetUp)
            {
                onResult(true);
                yield break;
            }

            if (probe.ExitCode != setupNeeded)
            {
                // Exit 1 means the script found something genuinely wrong that elevation would
                // not fix (no Node, a hosts file it cannot read). Echoing its own output is the
                // only way the reason reaches anyone — it went to a pipe, not a console.
                LogLines(probeOutput);
                Fail($"One-time machine setup cannot run (check exited {probe.ExitCode}).",
                     "setup-offline.ps1 -DryRun reported a problem it will not fix by elevating. " +
                     "See the console for what it said.");
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

        /// <summary>
        /// Confirms this machine resolves <see cref="MagicDomain.HostName"/> to the hotspot
        /// address, which is what the hosts entry setup-offline.ps1 writes is for.
        /// </summary>
        /// <remarks>
        /// The single most misleading failure in this design, and the reason it is checked
        /// explicitly rather than left to fail downstream. The host never consults the DNS
        /// responder on its own :53 — it takes no DHCP from itself — so it asks a public
        /// resolver instead, and play.skiprocking.ca is a real registered name whose public
        /// record is the registrar's parking page. A missing hosts entry therefore does not
        /// look like "unknown host". It looks like the relay is up but rejecting the
        /// connection, because Unity is talking to a stranger's web server on the internet.
        /// RelayClient retries that forever without ever saying anything is wrong.
        ///
        /// Run after EnsureOfflineSetup, so the entry it writes is already in place, and
        /// before tethering, so a wrong answer is reported instead of a hotspot nobody can
        /// use. Deliberately not gated on the address existing yet — the hosts file is
        /// consulted whether or not the hotspot adapter is up.
        /// </remarks>
        private bool VerifyHostNameResolves()
        {
            var hostName = MagicDomain.HostName;
            SetState(HotspotState.Starting, $"Checking this PC can resolve {hostName}…");

            IPAddress[] addresses;
            try
            {
                addresses = Dns.GetHostAddresses(hostName);
            }
            catch (Exception e)
            {
                Fail($"This PC cannot resolve {hostName}.",
                     $"The hosts entry pointing it at {HotspotAddress} is missing, and there is " +
                     $"no network to ask instead. Run deploy/setup-offline.ps1 as Administrator. " +
                     $"Underlying error: {e.Message}");
                return false;
            }

            foreach (var address in addresses)
            {
                if (address.ToString() == HotspotAddress) return true;
            }

            var got = addresses.Length == 0 ? "nothing" : string.Join(", ", (object[])addresses);
            Fail($"{hostName} resolves to the wrong address.",
                 $"Expected {HotspotAddress}, got {got} — that is the public record for this " +
                 $"domain, not this PC. The hosts entry is missing or stale. Run " +
                 $"deploy/setup-offline.ps1 as Administrator, then relaunch.");
            return false;
        }

        // ------------------------------------------------------------------ generation

        // ------------------------------------------------------------------ process plumbing

        private static string ResolveScriptPath()
        {
            var candidate = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "hotspot", "hotspot-control.ps1"));
            return File.Exists(candidate) ? candidate : null;
        }

        /// <summary>
        /// Drains a child's output into the console. Used for hotspot-control.ps1's stderr
        /// progress and for setup-offline.ps1 -DryRun's report — both went to a pipe rather
        /// than a console, so this is the only place a reader ever sees either.
        /// </summary>
        private static void LogLines(ConcurrentQueue<string> lines)
        {
            while (lines.TryDequeue(out var line)) Debug.Log($"[HotspotService] {line}");
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

        /// <summary>
        /// The WinRT enum name as something readable off a television.
        /// </summary>
        private static string BandLabel(string band) => band switch
        {
            "TwoPointFourGigahertz" => "2.4 GHz",
            "FiveGigahertz" => "5 GHz",
            _ => band
        };

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
