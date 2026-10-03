using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Net
{
    // Single source of truth for the hostname the certificate covers and where to download it from, replacing the... doc-ref 6fda docs/4-systems/HostingAndCertificates.md
    public static class MagicDomain
    {
        private const string FileName = "magic-domain.txt";

        /// <summary>
        /// The hostname a fresh install uses with no override file present. Must match the
        /// domain .github/workflows/renew-cert.yml issues for — the two are not wired
        /// together by any shared config, only by convention, so keep them in sync by hand if
        /// this ever changes.
        /// </summary>
        private const string DefaultHostName = "play.skiprocking.ca";

        // Where the renewed certificate lands. doc-ref 6147 docs/4-systems/HostingAndCertificates.md
        private static readonly string[] DefaultCertUrls =
        {
            "https://github.com/Ajw2003/rockskipping-cert/releases/download/current/server.pem",
        };

        private static readonly string[] DefaultKeyUrls =
        {
            "https://github.com/Ajw2003/rockskipping-cert/releases/download/current/server.key",
        };

        public static string HostName { get; private set; }
        public static IReadOnlyList<string> CertUrls { get; private set; }
        public static IReadOnlyList<string> KeyUrls { get; private set; }

        /// <summary>
        /// Loads the override once per process. Modelled directly on
        /// <see cref="Managers.MatchSettingsStore"/>'s own load: never throws, falls back to
        /// the compiled default on any read failure, so a missing or malformed file degrades
        /// to "as if it were never there" rather than blocking a launch.
        /// </summary>
        static MagicDomain()
        {
            (HostName, CertUrls, KeyUrls) = Load();
        }

        /// <summary>
        /// File format, one value per line, blank lines and <c>#</c> comments ignored:
        /// line 1 is the hostname; any further lines come in cert/key pairs (line 2 = a cert
        /// URL, line 3 = its matching key URL, and so on). An odd number of URL lines drops
        /// the trailing unmatched one rather than throwing, on the same "never block a
        /// launch over a hand-edited file" principle as the rest of this method.
        /// </summary>
        private static (string hostName, string[] certUrls, string[] keyUrls) Load()
        {
            try
            {
                var path = ResolvePath();
                if (!File.Exists(path))
                    return (DefaultHostName, DefaultCertUrls, DefaultKeyUrls);

                var lines = new List<string>();
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    lines.Add(line);
                }

                if (lines.Count == 0)
                    return (DefaultHostName, DefaultCertUrls, DefaultKeyUrls);

                var hostName = lines[0];

                var certUrls = new List<string>();
                var keyUrls = new List<string>();
                for (var i = 1; i < lines.Count; i += 2)
                {
                    if (i + 1 >= lines.Count) break; // odd one out, dropped rather than half-applied
                    certUrls.Add(lines[i]);
                    keyUrls.Add(lines[i + 1]);
                }

                // No URL pairs supplied — repoint the hostname only, keep the compiled URLs.
                // Lets a developer test against a different domain without also standing up a
                // mirror of the certificate for it.
                if (certUrls.Count == 0)
                    return (hostName, DefaultCertUrls, DefaultKeyUrls);

                Debug.Log($"[MagicDomain] {FileName} override: host={hostName}, " +
                          $"{certUrls.Count} cert URL(s).");
                return (hostName, certUrls.ToArray(), keyUrls.ToArray());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MagicDomain] Could not read {FileName}: {e.Message}");
                return (DefaultHostName, DefaultCertUrls, DefaultKeyUrls);
            }
        }

        /// <summary>
        /// Same resolution MatchSettingsStore/RelayClient/LocalServices already use:
        /// Application.dataPath is Assets/ in the editor and &lt;exe&gt;_Data in a build, so
        /// its parent is beside the executable both times.
        /// </summary>
        private static string ResolvePath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));
    }
}
