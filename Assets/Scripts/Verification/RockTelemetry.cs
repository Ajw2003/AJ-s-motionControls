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
    /// Records one CSV row per real throw, so the Authored/Derived question is settled by
    /// played matches rather than by argument.
    /// </summary>
    /// <remarks>
    /// This is the other half of <see cref="SkipCurve"/>, and the two measure different
    /// things on purpose. The offline sweep throws every rock at its own ideal face angle,
    /// because that is the only way to compare tunings against each other. Players do not
    /// throw at ideal angles, and that gap is exactly what decides whether a set that
    /// separates cleanly on paper separates in a living room — so this records the angle the
    /// bounce actually got, next to what the throw scored.
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
    /// Play the same group through one match on Authored and one on Derived (F4 switches the
    /// set between matches) and the two blocks of rows answer the question. See
    /// docs/plans/rock-choosing-system.md Part 6.
    /// </remarks>
    public class RockTelemetry : MonoBehaviour
    {
        private const string FileName = "rock-throws.csv";

        private const string Header =
            "timestamp,matchSeed,tuningSet,rockId,player,power,faceAngleDeg,skips,distance";

        /// <summary>Same resolution as MatchSettingsStore.ResolvePath and SkipCurve.ResultPath.</summary>
        public static string ResultPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));

        /// <summary>
        /// The launch half of the row, held until the throw ends.
        /// </summary>
        /// <remarks>
        /// The two events carry different halves of a throw and neither carries all of it:
        /// RockLaunchedEvent knows the power and the face angle, ThrowCompletedEvent knows
        /// whose it was and what it scored. Holding one field pair between them is safe
        /// because RockLauncher enforces one rock in the air at a time — a second launch
        /// cannot interleave with a run already flying.
        /// </remarks>
        private bool _hasLaunch;
        private float _power;
        private float _faceAngleDeg;

        private void Awake()
        {
            EventManager.Instance?.Subscribe<RockLaunchedEvent>(this, OnLaunched);
            EventManager.Instance?.Subscribe<ThrowCompletedEvent>(this, OnCompleted);
        }

        private void OnLaunched(RockLaunchedEvent e)
        {
            _hasLaunch = true;
            _power = e.Power;
            _faceAngleDeg = e.FaceAngleDeg;
        }

        private void OnCompleted(ThrowCompletedEvent e)
        {
            var rockId = string.Empty;
            var tuningSet = string.Empty;
            var seed = string.Empty;

            if (RockSelection.HasInstance)
            {
                var selection = RockSelection.Instance;
                var rock = selection.For(e.PlayerId);

                if (rock != null) rockId = rock.Id;
                tuningSet = selection.ActiveTuningSet.ToString();
                if (selection.LastRandomSeed.HasValue)
                {
                    seed = selection.LastRandomSeed.Value.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Blank rather than zero when a launch was somehow missed: a power of 0 is a
            // throw that happened, and a row that quietly invents one is worse than a row
            // that admits it does not know.
            var power = _hasLaunch ? _power.ToString("F3", CultureInfo.InvariantCulture) : string.Empty;
            var face = _hasLaunch ? _faceAngleDeg.ToString("F1", CultureInfo.InvariantCulture) : string.Empty;
            _hasLaunch = false;

            var row = string.Join(",",
                Cell(DateTime.Now.ToString("s", CultureInfo.InvariantCulture)),
                Cell(seed),
                Cell(tuningSet),
                Cell(rockId),
                Cell(e.PlayerId),
                Cell(power),
                Cell(face),
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
