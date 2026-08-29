using System;
using System.Globalization;
using System.IO;
using EventSystems;
using EventTypes.SkipEvents;
using Managers;
using UnityEngine;

namespace Verification
{
    /// <summary>
    /// Records one CSV row per real throw — the corpus of what players actually do with
    /// the throw-driven model, next to what SkipCurve measures offline.
    /// </summary>
    /// <remarks>
    /// This used to record the Authored/Derived comparison (docs/plans/rock-choosing-system.md
    /// Part 6); that apparatus is gone (docs/plans/Plan-ThrowDrivenSkipModel.md), and so is
    /// the `tuningSet` column. What is left is the honest thing to keep measuring: the raw
    /// signal a real swing produced (swingSpeed, spinRate) against the three axes the bounce
    /// now judges (pitchDeg, rollDeg, yawDeg) and what it scored. yawDeg is always 0 at
    /// launch — the velocity starts aligned with the stone's heading by construction — so
    /// this records the number that would actually separate a straight throw from one that
    /// curved, once something downstream tracks sideslip at landing rather than at release.
    ///
    /// Written beside skip-curve.txt (repo root in the Editor, next to the executable in a
    /// build), the same file-beside-the-exe convention as relay-url.txt and
    /// match-settings.json. Append-only and closed after every row: a session that crashes,
    /// or a host that alt-F4s out of a match, keeps every throw up to that point. Losing a
    /// corpus to a buffered write would cost a play session that cannot be re-run, because
    /// the people are what made it.
    ///
    /// Reads rather than publishes — it is an observer, and nothing in the game may depend
    /// on it running. Turning it off in Bootstrap changes no behaviour, only what is recorded.
    ///
    /// The 73 rows logged under the old model (commit 34932cc) are not comparable to
    /// anything this writes — see the "archive, don't delete" note on rock-throws.csv in
    /// the plan's Risks section. That file was archived, not appended to, when this changed.
    /// </remarks>
    public class RockTelemetry : MonoBehaviour
    {
        private const string FileName = "rock-throws.csv";

        private const string Header =
            "timestamp,matchSeed,rockId,player,swingSpeed,spinRate,pitchDeg,rollDeg,yawDeg,skips,distance";

        /// <summary>Same resolution as MatchSettingsStore.ResolvePath and SkipCurve.ResultPath.</summary>
        public static string ResultPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));

        /// <summary>
        /// The launch half of the row, held until the throw ends.
        /// </summary>
        /// <remarks>
        /// The two events carry different halves of a throw and neither carries all of it:
        /// RockLaunchedEvent knows the raw swing and the launch angles, ThrowCompletedEvent
        /// knows whose it was and what it scored. Holding these fields between them is safe
        /// because RockLauncher enforces one rock in the air at a time — a second launch
        /// cannot interleave with a run already flying.
        /// </remarks>
        private bool _hasLaunch;
        private float _swingSpeed;
        private float _spinRate;
        private float _pitchDeg;
        private float _rollDeg;

        private void Awake()
        {
            EventManager.Instance?.Subscribe<RockLaunchedEvent>(this, OnLaunched);
            EventManager.Instance?.Subscribe<ThrowCompletedEvent>(this, OnCompleted);
        }

        private void OnLaunched(RockLaunchedEvent e)
        {
            _hasLaunch = true;
            _swingSpeed = e.SwingSpeed;
            _spinRate = e.SpinRate;
            _pitchDeg = e.FaceAngleDeg;
            _rollDeg = e.RollDeg;
        }

        private void OnCompleted(ThrowCompletedEvent e)
        {
            var rockId = string.Empty;
            var seed = string.Empty;

            if (RockSelection.HasInstance)
            {
                var selection = RockSelection.Instance;
                var rock = selection.For(e.PlayerId);

                if (rock != null) rockId = rock.Id;
                if (selection.LastRandomSeed.HasValue)
                {
                    seed = selection.LastRandomSeed.Value.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Blank rather than zero when a launch was somehow missed: a swing speed of 0
            // is a throw that happened, and a row that quietly invents one is worse than a
            // row that admits it does not know.
            var swingSpeed = _hasLaunch ? _swingSpeed.ToString("F3", CultureInfo.InvariantCulture) : string.Empty;
            var spinRate = _hasLaunch ? _spinRate.ToString("F1", CultureInfo.InvariantCulture) : string.Empty;
            var pitch = _hasLaunch ? _pitchDeg.ToString("F1", CultureInfo.InvariantCulture) : string.Empty;
            var roll = _hasLaunch ? _rollDeg.ToString("F1", CultureInfo.InvariantCulture) : string.Empty;

            // Always 0 at launch — see the class remarks. Recorded explicitly rather than
            // left off, so the column exists and its meaning is documented, ready for
            // whatever eventually tracks sideslip at landing instead.
            var yaw = _hasLaunch ? "0.0" : string.Empty;
            _hasLaunch = false;

            var row = string.Join(",",
                Cell(DateTime.Now.ToString("s", CultureInfo.InvariantCulture)),
                Cell(seed),
                Cell(rockId),
                Cell(e.PlayerId),
                Cell(swingSpeed),
                Cell(spinRate),
                Cell(pitch),
                Cell(roll),
                Cell(yaw),
                Cell(e.TotalSkips.ToString(CultureInfo.InvariantCulture)),
                Cell(e.Distance.ToString("F2", CultureInfo.InvariantCulture)));

            Append(row);
        }

        private static void Append(string row)
        {
            try
            {
                var path = ResultPath;
                var needsHeader = !File.Exists(path) || new FileInfo(path).Length == 0;

                using var writer = new StreamWriter(path, append: true);
                if (needsHeader) writer.WriteLine(Header);
                writer.WriteLine(row);
            }
            catch (IOException e)
            {
                // A warning, never an exception out of an event handler: EventManager
                // delivers to every subscriber from one publish, and a throw here would take
                // the score and the turn rotation down with it over a locked file. Telemetry
                // failing must cost the recording and nothing else.
                Debug.LogWarning($"[RockTelemetry] Could not write {FileName}: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"[RockTelemetry] Could not write {FileName}: {e.Message}");
            }
        }

        /// <summary>
        /// Quotes a cell only when it needs it.
        /// </summary>
        /// <remarks>
        /// Player ids come off the wire, so they are the one column this cannot vouch for —
        /// a comma in one would silently shift every column after it and the file would look
        /// fine until someone tried to read it a week later.
        /// </remarks>
        private static string Cell(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;

            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        private void OnDestroy()
        {
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
        }
    }
}
