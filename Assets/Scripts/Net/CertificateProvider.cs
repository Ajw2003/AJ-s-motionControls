using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace Net
{
    /// <summary>
    /// Obtains the TLS material the relay needs, with no human involved.
    /// </summary>
    /// <remarks>
    /// Phone motion sensors require a secure context, a secure context requires a publicly
    /// trusted certificate, and a certificate requires a name — for a machine on a private
    /// address that is a circle with no obvious exit. local-ip.sh breaks it by publishing a
    /// real wildcard for *.local-ip.sh together with DNS that maps IP-encoded labels back to
    /// the address they encode, so 10.0.0.213 becomes 10-0-0-213.local-ip.sh and resolves.
    ///
    /// Its private key is published deliberately. That is what makes this possible and also
    /// what caps what it may ever be used for: the certificate proves nothing about who is
    /// serving, it only unlocks the sensor APIs. Nothing sensitive may travel over it.
    /// SecurityReview.md section 1 is the long version.
    ///
    /// This replaces deploy/local-https.ps1, which did the same work but required somebody to
    /// remember to run it. The failure that motivated the change: the script's own chain check
    /// calls a file deleted in 1cd7647, so re-running it after the cached certificate expires
    /// fails in a way that looks nothing like "your certificate expired".
    ///
    /// Renewal is structural rather than scheduled. Every launch checks, and a 90-day
    /// certificate cannot lapse between two launches of a game nobody has opened in three
    /// months, because opening it is the check.
    /// </remarks>
    public static class CertificateProvider
    {
        private const string CertUrl = "https://local-ip.sh/server.pem";
        private const string KeyUrl = "https://local-ip.sh/server.key";
        private const string CacheDirName = "local-https";
        private const string Suffix = ".local-ip.sh";

        /// <summary>
        /// Renew this far ahead of expiry. Large enough that a party is never the moment the
        /// certificate runs out, small enough not to hammer local-ip.sh on every launch.
        /// </summary>
        private const int RenewWithinDays = 14;

        private const int TimeoutSeconds = 20;

        /// <summary>Interfaces that own a gateway but are never the way to reach a phone.</summary>
        private static readonly Regex VirtualAdapter = new Regex(
            "vEthernet|VMware|VirtualBox|Hyper-V|Loopback|TAP-|TeamViewer|ZeroTier|Tailscale|Npcap",
            RegexOptions.IgnoreCase);

        public struct Result
        {
            public bool Ok;
            public string Address;    // 10.0.0.213
            public string HostName;   // 10-0-0-213.local-ip.sh
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
        /// <param name="addressOverride">
        /// Skips detection. For machines with more than one plausible interface, where
        /// guessing wrong fails at the phone in a way indistinguishable from a broken
        /// certificate.
        /// </param>
        public static IEnumerator Ensure(string addressOverride, Action<Result> onResult)
        {
            var address = ResolveLanAddress(addressOverride, out var addressError, out var addressDetail);
            if (address == null)
            {
                onResult(new Result { Ok = false, Error = addressError, Detail = addressDetail });
                yield break;
            }

            var hostName = ToHostName(address);
            var certPath = Path.Combine(CacheDir, "server.pem");
            var keyPath = Path.Combine(CacheDir, "server.key");

            var cached = Inspect(certPath, keyPath);

            if (cached.Usable && cached.DaysLeft > RenewWithinDays)
            {
                Debug.Log($"[CertificateProvider] Cached certificate valid until " +
                          $"{cached.NotAfter:yyyy-MM-dd} ({cached.DaysLeft} days) — no download needed.");
                onResult(Success(address, hostName, certPath, keyPath, cached.NotAfter));
                yield break;
            }

            var reason = !cached.Usable ? cached.Reason : $"expires in {cached.DaysLeft} days";
            Debug.Log($"[CertificateProvider] Fetching from local-ip.sh ({reason}).");

            string downloadError = null;
            yield return Download(certPath, keyPath, e => downloadError = e);

            if (downloadError != null)
            {
                // A host with no internet is an ordinary case, not an error, and the cached
                // copy is good for the rest of its 90 days. Only a stale cache AND an
                // unreachable service is fatal.
                if (cached.Usable)
                {
                    Debug.LogWarning($"[CertificateProvider] Download failed, using the cached " +
                                     $"certificate ({cached.DaysLeft} days left): {downloadError}");
                    onResult(Success(address, hostName, certPath, keyPath, cached.NotAfter));
                    yield break;
                }

                onResult(new Result
                {
                    Ok = false,
                    Error = "Could not get a certificate.",
                    Detail = $"local-ip.sh is unreachable and there is no usable cached copy " +
                             $"({cached.Reason}). Connect to a network and restart. Underlying " +
                             $"error: {downloadError}",
                });
                yield break;
            }

            var fresh = Inspect(certPath, keyPath);
            if (!fresh.Usable)
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

        /// <summary>10.0.0.213 -&gt; 10-0-0-213.local-ip.sh.</summary>
        /// <remarks>
        /// The wildcard covers exactly one label, so the dashes are not cosmetic:
        /// 10-0-0-213.local-ip.sh is covered and 10.0.0.213.local-ip.sh is not.
        /// </remarks>
        public static string ToHostName(string address) =>
            address.Replace('.', '-') + Suffix;

        /// <summary>
        /// Finds the address a phone on this network would use to reach this machine.
        /// </summary>
        /// <remarks>
        /// The gateway test is what discards the 169.254.x APIPA addresses that unplugged
        /// NICs, Bluetooth and virtual switches leave lying around — a plain sweep of every
        /// unicast address returns those too, and they look entirely plausible.
        ///
        /// More than one candidate is not automatically an error: a docked laptop with
        /// Ethernet and Wi-Fi both up is an ordinary setup, and the routing table already
        /// knows which one Windows would actually use. That address breaks the tie. Only
        /// when the preferred route matches none of the candidates — or can't be determined
        /// at all — does this fall back to refusing to guess, which is a deliberate
        /// inheritance from the PowerShell version: a wrong address here fails at the phone
        /// in exactly the same way a broken certificate does, and that is the confusion
        /// worth spending an error message to avoid.
        /// </remarks>
        public static string ResolveLanAddress(string overrideAddress, out string error, out string detail)
        {
            error = null;
            detail = null;

            if (!string.IsNullOrWhiteSpace(overrideAddress))
            {
                var trimmed = overrideAddress.Trim();
                if (!System.Net.IPAddress.TryParse(trimmed, out var parsed) ||
                    parsed.AddressFamily != AddressFamily.InterNetwork)
                {
                    error = "The configured address is not an IPv4 address.";
                    detail = $"Got '{trimmed}'.";
                    return null;
                }

                Debug.Log($"[CertificateProvider] Using configured address {trimmed}.");
                return trimmed;
            }

            var candidates = new List<KeyValuePair<string, string>>(); // alias -> address

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (VirtualAdapter.IsMatch(nic.Name) || VirtualAdapter.IsMatch(nic.Description)) continue;

                IPInterfaceProperties properties;
                try { properties = nic.GetIPProperties(); }
                catch { continue; }   // A NIC can disappear mid-enumeration.

                var hasGateway = false;
                foreach (var gateway in properties.GatewayAddresses)
                {
                    if (gateway?.Address == null) continue;
                    if (gateway.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (gateway.Address.ToString() == "0.0.0.0") continue;
                    hasGateway = true;
                    break;
                }
                if (!hasGateway) continue;

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast?.Address == null) continue;
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    var text = unicast.Address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;

                    candidates.Add(new KeyValuePair<string, string>(nic.Name, text));
                    break;
                }
            }

            if (candidates.Count == 0)
            {
                error = "This machine is not on a network.";
                detail = "No interface has an IPv4 default gateway. Join a Wi-Fi network or " +
                         "plug in, then restart.";
                return null;
            }

            if (candidates.Count > 1)
            {
                var preferred = PreferredRouteAddress();
                foreach (var c in candidates)
                {
                    if (c.Value != preferred) continue;
                    Debug.Log($"[CertificateProvider] {candidates.Count} candidates; the routing " +
                              $"table prefers {preferred} ({c.Key}).");
                    return preferred;
                }

                var listed = new List<string>();
                foreach (var c in candidates) listed.Add($"{c.Value} ({c.Key})");

                error = "More than one network to choose from.";
                detail = "Guessing wrong here fails exactly like a broken certificate, so it " +
                         "is not guessed. Set the address explicitly. Candidates: " +
                         string.Join(", ", listed);
                return null;
            }

            Debug.Log($"[CertificateProvider] LAN address {candidates[0].Value} on {candidates[0].Key}.");
            return candidates[0].Value;
        }

        /// <summary>
        /// Asks the routing table which local address Windows would actually use to reach
        /// the outside world, to break a tie between several plausible interfaces.
        /// </summary>
        /// <remarks>
        /// Connecting a UDP socket transmits no packets — it only resolves the route — so
        /// this asks the question directly instead of guessing from interface metrics, which
        /// .NET does not expose fully enough to rank interfaces by. 203.0.113.0/24 is
        /// TEST-NET-3, reserved for documentation and never contacted.
        ///
        /// Returns null if the route can't be resolved at all (no default route reachable),
        /// which the caller treats the same as a preference matching no candidate: fall back
        /// to refusing to guess.
        /// </remarks>
        private static string PreferredRouteAddress()
        {
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.Connect("203.0.113.1", 9);
                return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CertificateProvider] Could not resolve a preferred route: {e.Message}");
                return null;
            }
        }

        // -------------------------------------------------------------- cache state

        private struct CacheState
        {
            public bool Usable;
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
                        Reason = $"cached certificate does not cover *{Suffix}",
                    };

                return new CacheState { Usable = true, NotAfter = cert.NotAfter, DaysLeft = daysLeft };
            }
            catch (Exception e)
            {
                return new CacheState { Reason = $"cached certificate unreadable: {e.Message}" };
            }
        }

        /// <summary>
        /// Confirms the SAN really covers the wildcard, which catches a truncated or swapped
        /// download that would otherwise only surface as a name mismatch on somebody's phone.
        /// </summary>
        private static bool CoversWildcard(X509Certificate2 cert)
        {
            foreach (var extension in cert.Extensions)
            {
                if (extension.Oid?.Value != "2.5.29.17") continue;   // subjectAltName
                var names = extension.Format(false);
                return names != null && names.IndexOf("*" + Suffix, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        // ---------------------------------------------------------------- download

        /// <summary>
        /// Fetches both files, writing each only once it has arrived complete.
        /// </summary>
        /// <remarks>
        /// Downloading straight over the cache would turn a dropped connection into a
        /// corrupted cache — and the fallback path exists precisely for hosts whose
        /// connection is unreliable, so that is not a rare combination.
        /// </remarks>
        private static IEnumerator Download(string certPath, string keyPath, Action<string> onError)
        {
            Directory.CreateDirectory(CacheDir);

            byte[] certBytes = null;
            byte[] keyBytes = null;
            string failure = null;

            yield return Fetch(CertUrl, (data, error) => { certBytes = data; failure ??= error; });
            if (failure == null)
                yield return Fetch(KeyUrl, (data, error) => { keyBytes = data; failure ??= error; });

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
