using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Networking;

namespace Net
{
    // Finds a usable Node.js runtime, downloading a portable copy when this machine has none — the thing that was... doc-ref 7972 docs/4-systems/HostingAndCertificates.md
    public static class NodeProvisioner
    {
        /// <summary>
        /// Pinned rather than "latest" — a moving download target is untestable and a
        /// version bump here is a deliberate, reviewable change.
        /// </summary>
        private const string NodeVersion = "22.11.0";

        private const string CacheDirName = "node-runtime";
        private const int TimeoutSeconds = 180;

        public struct Result
        {
            public bool Ok;
            public string NodePath;
            public bool Downloaded;

            /// <summary>Short, shown on the TV.</summary>
            public string Error;

            /// <summary>The part a developer needs and a player does not.</summary>
            public string Detail;
        }

        private static string InstallDirName => $"node-v{NodeVersion}-win-x64";

        private static string CacheDir =>
            Path.Combine(Application.persistentDataPath, CacheDirName);

        private static string DownloadedNodeExe =>
            Path.Combine(CacheDir, InstallDirName, "node.exe");

        /// <summary>
        /// Resolves a node.exe to run, downloading one if nothing else is available.
        /// Reports progress through <paramref name="onProgress"/> (0..1, download only).
        /// </summary>
        public static IEnumerator Ensure(Action<Result> onResult, Action<float> onProgress = null)
        {
            // A previous download already sitting in the cache — the common case after the
            // first launch on a machine, and it costs nothing to check first.
            if (File.Exists(DownloadedNodeExe))
            {
                onResult(new Result { Ok = true, NodePath = DownloadedNodeExe, Downloaded = false });
                yield break;
            }

            var onPath = ProcessUtil.ResolveExecutable("node", new[] { @"C:\Program Files\nodejs" });
            if (onPath != null)
            {
                onResult(new Result { Ok = true, NodePath = onPath, Downloaded = false });
                yield break;
            }

            if (Application.platform != RuntimePlatform.WindowsEditor &&
                Application.platform != RuntimePlatform.WindowsPlayer)
            {
                onResult(new Result
                {
                    Ok = false,
                    Error = "Node.js not found.",
                    Detail = "Automatic download is only implemented for Windows. " +
                             "Install Node 20 or newer from nodejs.org and relaunch.",
                });
                yield break;
            }

            yield return Download(onProgress, result => onResult(result));
        }

        private static IEnumerator Download(Action<float> onProgress, Action<Result> onResult)
        {
            Directory.CreateDirectory(CacheDir);

            var url = $"https://nodejs.org/dist/v{NodeVersion}/node-v{NodeVersion}-win-x64.zip";
            var zipPath = Path.Combine(CacheDir, "node.zip.partial");

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET))
            {
                request.downloadHandler = new DownloadHandlerFile(zipPath) { removeFileOnAbort = true };
                request.timeout = TimeoutSeconds;

                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    onProgress?.Invoke(request.downloadProgress);
                    yield return null;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    onResult(new Result
                    {
                        Ok = false,
                        Error = "Could not download Node.js.",
                        Detail = $"GET {url} failed: {request.error}. Install Node 20 or newer " +
                                 "from nodejs.org yourself, or connect to the internet and retry.",
                    });
                    yield break;
                }
            }

            Result extractResult;
            try
            {
                ExtractZip(zipPath, CacheDir);
                File.Delete(zipPath);

                extractResult = File.Exists(DownloadedNodeExe)
                    ? new Result { Ok = true, NodePath = DownloadedNodeExe, Downloaded = true }
                    : new Result
                    {
                        Ok = false,
                        Error = "The downloaded Node.js runtime is unusable.",
                        Detail = $"Expected node.exe at {DownloadedNodeExe} after extraction; it " +
                                 "was not there. The zip layout may have changed upstream.",
                    };
            }
            catch (Exception e)
            {
                extractResult = new Result
                {
                    Ok = false,
                    Error = "Could not unpack the downloaded Node.js runtime.",
                    Detail = e.Message,
                };
            }

            onResult(extractResult);
        }

        /// <summary>
        /// Extracts entry-by-entry through the plain <see cref="ZipArchive"/> constructor
        /// rather than the <c>ZipFile</c> convenience methods (OpenRead,
        /// ExtractToDirectory) — those have a history of being missing from Unity's
        /// trimmed .NET Standard reference assemblies even where they work at runtime.
        /// <see cref="ZipArchive"/> itself has no such history.
        /// </summary>
        private static void ExtractZip(string zipPath, string destinationDir)
        {
            using var stream = File.OpenRead(zipPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                // Zip entries use '/' regardless of platform.
                var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var targetPath = Path.GetFullPath(Path.Combine(destinationDir, relativePath));

                // A malicious or corrupt zip could try to escape destinationDir with "../"
                // segments — refuse rather than write outside the cache.
                if (!targetPath.StartsWith(Path.GetFullPath(destinationDir) + Path.DirectorySeparatorChar))
                    throw new InvalidDataException($"Zip entry escapes the extraction directory: {entry.FullName}");

                if (entry.Name.Length == 0) // a directory entry
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                var targetDir = Path.GetDirectoryName(targetPath);
                if (targetDir != null) Directory.CreateDirectory(targetDir);

                using var entryStream = entry.Open();
                using var fileStream = File.Create(targetPath);
                entryStream.CopyTo(fileStream);
            }
        }

        /// <summary>
        /// The path npm's own CLI script lives at next to a given node.exe, on the standard
        /// Windows layout both a system install and <see cref="Ensure"/>'s download use —
        /// null when it is not there, so the caller can fall back to invoking npm as its
        /// own shim instead.
        /// </summary>
        public static string NpmCliPathFor(string nodePath)
        {
            var dir = Path.GetDirectoryName(nodePath);
            if (dir == null) return null;

            var npmCli = Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js");
            return File.Exists(npmCli) ? npmCli : null;
        }
    }
}
