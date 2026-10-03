using System;
using System.Collections;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace Net
{
    // Obtains the TLS material the relay needs, with no human involved. doc-ref 3e74 docs/4-systems/HostingAndCertificates.md
    public static class CertificateProvider
    {
        private const string CacheDirName = "local-https";

        /// <summary>
        /// Filenames the seed certificate is shipped under in StreamingAssets, and the names
        /// the cache always uses regardless of where a copy came from — downloaded, cached, or
        /// seeded. Keeping one pair of names for all three sources is what lets Inspect() stay
        /// ignorant of provenance.
        /// </summary>
        private const string CertFileName = "server.pem";
        private const string KeyFileName = "server.key";

        /// <summary>
        /// Renew this far ahead of expiry. Large enough that a party is never the moment the
        /// certificate runs out, small enough not to hammer local-ip.sh on every launch.
        /// </summary>
        private const int RenewWithinDays = 14;

        private const int TimeoutSeconds = 20;

        public struct Result
        {
            public bool Ok;
            public string Address;    // 10.0.0.213, or 192.168.137.1 in Hotspot mode
            public string HostName;   // MagicDomain.HostName, e.g. play.skiprocking.ca
            public string CertPath;
            public string KeyPath;
            public DateTime NotAfter;

            /// <summary>Short, and written to be shown on a television.</summary>
            public string Error;

            /// <summary>The part a developer needs and a player does not.</summary>
            public string Detail;
        }

        private static string CacheDir =>
            Path.Combine(Application.persistentDataPath, CacheDirName);

        /// <summary>
        /// Ensures a usable certificate exists and reports where it landed.
        /// </summary>
        /// <param name="address">
        /// The host address, always the fixed Hotspot address now — see
        /// LocalServices.HotspotHostAddress. Validated rather than trusted blindly, since a
        /// malformed value here fails at the phone in a way indistinguishable from a broken
        /// certificate.
        /// </param>
        public static IEnumerator Ensure(string address, Action<Result> onResult)
        {
            if (!ValidateHostAddress(address, out var addressError, out var addressDetail))
            {
                onResult(new Result { Ok = false, Error = addressError, Detail = addressDetail });
                yield break;
            }

            // Fixed, not derived from the address. The certificate is single-name now
            // (MagicDomain.HostName, e.g. play.skiprocking.ca), not a wildcard keyed to
            // whatever address this machine happens to have — see MagicDomain's remarks for
            // why that stopped being necessary once Hotspot's address became a constant.
            var hostName = MagicDomain.HostName;
            var certPath = Path.Combine(CacheDir, CertFileName);
            var keyPath = Path.Combine(CacheDir, KeyFileName);

            var cached = Inspect(certPath, keyPath);

            if (cached.IsUsable && cached.DaysLeft > RenewWithinDays)
            {
                Debug.Log($"[CertificateProvider] Cached certificate valid until " +
                          $"{cached.NotAfter:yyyy-MM-dd} ({cached.DaysLeft} days) — no download needed.");
                onResult(Success(address, hostName, certPath, keyPath, cached.NotAfter));
                yield break;
            }

            var reason = !cached.IsUsable ? cached.Reason : $"expires in {cached.DaysLeft} days";
            Debug.Log($"[CertificateProvider] Fetching from {MagicDomain.CertUrls.Count} " +
                      $"configured source(s) ({reason}).");

            string downloadError = null;
            yield return Download(certPath, keyPath, e => downloadError = e);

            if (downloadError != null)
            {
                // A host with no internet is an ordinary case, not an error, and the cached
                // copy is good for the rest of its 90 days. Only a stale cache AND an
                // unreachable service is fatal.
                if (cached.IsUsable)
                {
                    Debug.LogWarning($"[CertificateProvider] Download failed, using the cached " +
                                     $"certificate ({cached.DaysLeft} days left): {downloadError}");
                    onResult(Success(address, hostName, certPath, keyPath, cached.NotAfter));
                    yield break;
                }

                // Neither a usable cache nor a successful download — the state that used to be
                // unconditionally fatal (a fresh install on a day the CDN is unreachable could
                // never start at all). There is no real certificate to fall back to yet — the
                // renewal pipeline (.github/workflows/renew-cert.yml) has not necessarily ever
                // run — so this only helps once something has actually been placed in
                // StreamingAssets; on a build with no seed shipped, this is a no-op and the
                // fatal Result below is reported exactly as before.
                if (TrySeedCache(certPath, keyPath))
                {
                    var seeded = Inspect(certPath, keyPath);
                    if (seeded.IsUsable)
                    {
                        Debug.LogWarning($"[CertificateProvider] Download failed and there was " +
                                         $"no usable cache — copied the bundled seed certificate " +
                                         $"instead ({seeded.DaysLeft} days left): {downloadError}");
                        onResult(Success(address, hostName, certPath, keyPath, seeded.NotAfter));
                        yield break;
                    }

                    // The seed exists but doesn't pass Inspect() (expired, wrong name, corrupt
                    // — a bad seed shipped by mistake). Fall through to the fatal Result rather
                    // than serving a certificate that will only fail again at the phone.
                    Debug.LogWarning($"[CertificateProvider] Seed certificate present but " +
                                     $"unusable: {seeded.Reason}");
                }

                onResult(new Result
                {
                    Ok = false,
                    Error = "Could not get a certificate.",
                    Detail = $"The configured certificate source(s) are unreachable, there is no " +
                             $"usable cached copy ({cached.Reason}), and no seed certificate is " +
                             $"available either. Connect to a network and restart. Underlying " +
                             $"error: {downloadError}",
                });
                yield break;
            }

            var fresh = Inspect(certPath, keyPath);
            if (!fresh.IsUsable)
            {
                onResult(new Result
                {
                    Ok = false,
                    Error = "The downloaded certificate is unusable.",
                    Detail = fresh.Reason,
                });
                yield break;
            }

            Debug.Log($"[CertificateProvider] Certificate valid until {fresh.NotAfter:yyyy-MM-dd} " +
                      $"({fresh.DaysLeft} days).");
            onResult(Success(address, hostName, certPath, keyPath, fresh.NotAfter));
        }

        private static Result Success(string address, string hostName, string certPath,
                                      string keyPath, DateTime notAfter) =>
            new Result
            {
                Ok = true,
                Address = address,
                HostName = hostName,
                CertPath = certPath,
                KeyPath = keyPath,
                NotAfter = notAfter,
            };

        // ------------------------------------------------------------------ address

        // Confirms the address handed in is a well-formed IPv4 address. doc-ref 12b7 docs/4-systems/HostingAndCertificates.md
        public static bool ValidateHostAddress(string address, out string error, out string detail)
        {
            error = null;
            detail = null;

            var trimmed = address?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                error = "No host address configured.";
                detail = "Expected an IPv4 address (LocalServices.HotspotHostAddress).";
                return false;
            }

            if (!System.Net.IPAddress.TryParse(trimmed, out var parsed) ||
                parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                error = "The configured address is not an IPv4 address.";
                detail = $"Got '{trimmed}'.";
                return false;
            }

            return true;
        }

        // -------------------------------------------------------------- cache state

        private struct CacheState
        {
            public bool IsUsable;
            public DateTime NotAfter;
            public int DaysLeft;
            public string Reason;   // why not usable
        }

        /// <summary>
        /// Reads the cached pair and decides whether it can still be used.
        /// </summary>
        /// <remarks>
        /// An expired borrowed certificate presents on the phone as a vague page-load failure
        /// that reads like a configuration mistake, so it is caught here where it can be said
        /// plainly instead.
        /// </remarks>
        private static CacheState Inspect(string certPath, string keyPath)
        {
            if (!File.Exists(certPath) || !File.Exists(keyPath))
                return new CacheState { Reason = "no cached certificate" };

            try
            {
                var pem = File.ReadAllText(certPath);
                var match = Regex.Match(pem,
                    "-----BEGIN CERTIFICATE-----(?<b64>[\\s\\S]*?)-----END CERTIFICATE-----");

                if (!match.Success)
                    return new CacheState { Reason = "the cached server.pem holds no certificate" };

                var der = Convert.FromBase64String(Regex.Replace(match.Groups["b64"].Value, "\\s", ""));

                // Deliberately not X509Certificate2.CreateFromPemFile — that is .NET 5+ and
                // Unity's Mono does not have it. Decoding the armour by hand is the portable
                // route and is what the PowerShell version did too.
                var cert = new X509Certificate2(der);

                var daysLeft = (int)(cert.NotAfter - DateTime.Now).TotalDays;
                if (daysLeft < 0)
                    return new CacheState
                    {
                        NotAfter = cert.NotAfter,
                        DaysLeft = daysLeft,
                        Reason = $"cached certificate expired on {cert.NotAfter:yyyy-MM-dd}",
                    };

                if (!CoversWildcard(cert))
                    return new CacheState
                    {
                        NotAfter = cert.NotAfter,
                        DaysLeft = daysLeft,
                        Reason = $"cached certificate does not cover {MagicDomain.HostName}",
                    };

                return new CacheState { IsUsable = true, NotAfter = cert.NotAfter, DaysLeft = daysLeft };
            }
            catch (Exception e)
            {
                return new CacheState { Reason = $"cached certificate unreadable: {e.Message}" };
            }
        }

        // Confirms the SAN really covers the configured hostname, which catches a truncated or swapped download that... doc-ref 2f35 docs/4-systems/HostingAndCertificates.md
        private static bool CoversWildcard(X509Certificate2 cert)
        {
            foreach (var extension in cert.Extensions)
            {
                if (extension.Oid?.Value != "2.5.29.17") continue;   // subjectAltName
                var names = extension.Format(false);
                return names != null &&
                       names.IndexOf(MagicDomain.HostName, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        // ---------------------------------------------------------------- download

        // Fetches both files, writing each only once it has arrived complete. doc-ref 0584 docs/4-systems/HostingAndCertificates.md
        private static IEnumerator Download(string certPath, string keyPath, Action<string> onError)
        {
            Directory.CreateDirectory(CacheDir);

            var certUrls = MagicDomain.CertUrls;
            var keyUrls = MagicDomain.KeyUrls;
            var pairCount = Math.Min(certUrls.Count, keyUrls.Count);

            if (pairCount == 0)
            {
                onError("no certificate source is configured");
                yield break;
            }

            byte[] certBytes = null;
            byte[] keyBytes = null;
            string failure = null;

            for (var i = 0; i < pairCount; i++)
            {
                failure = null;
                yield return Fetch(certUrls[i], (data, error) => { certBytes = data; failure ??= error; });
                if (failure == null)
                    yield return Fetch(keyUrls[i], (data, error) => { keyBytes = data; failure ??= error; });

                if (failure == null) break; // this pair worked, no need to try the rest

                Debug.LogWarning($"[CertificateProvider] Source {i + 1}/{pairCount} failed: {failure}");
            }

            if (failure != null) { onError(failure); yield break; }

            try
            {
                WriteAtomic(certPath, certBytes);
                WriteAtomic(keyPath, keyBytes);
            }
            catch (Exception e)
            {
                onError($"could not write the certificate cache: {e.Message}");
                yield break;
            }

            onError(null);
        }

        // Copies the certificate bundled in StreamingAssets into the cache, so a brand-new install still reaches Read... doc-ref 8019 docs/4-systems/HostingAndCertificates.md
        private static bool TrySeedCache(string certPath, string keyPath)
        {
            var seedCert = Path.Combine(Application.streamingAssetsPath, CertFileName);
            var seedKey = Path.Combine(Application.streamingAssetsPath, KeyFileName);

            if (!File.Exists(seedCert) || !File.Exists(seedKey))
                return false; // nothing shipped — the ordinary case until renew-cert.yml has run

            try
            {
                Directory.CreateDirectory(CacheDir);
                WriteAtomic(certPath, File.ReadAllBytes(seedCert));
                WriteAtomic(keyPath, File.ReadAllBytes(seedKey));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CertificateProvider] Found a seed certificate but could not " +
                                 $"copy it into the cache: {e.Message}");
                return false;
            }
        }

        private static IEnumerator Fetch(string url, Action<byte[], string> onDone)
        {
            using var request = UnityWebRequest.Get(url);
            request.timeout = TimeoutSeconds;
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onDone(null, $"{url}: {request.error}");
                yield break;
            }

            var data = request.downloadHandler.data;
            if (data == null || data.Length == 0)
            {
                onDone(null, $"{url}: empty response");
                yield break;
            }

            onDone(data, null);
        }

        private static void WriteAtomic(string path, byte[] data)
        {
            var temp = path + ".partial";
            File.WriteAllBytes(temp, data);

            // File.Move refuses to overwrite on the .NET Framework profile Unity ships, so
            // the old copy goes first. The window this opens is covered by the .partial file
            // still being on disk if the move fails.
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }
}
