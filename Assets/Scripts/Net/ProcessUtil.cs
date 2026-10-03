using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Net
{
    // Child-process plumbing shared by the long-running relay (drained continuously via Update, expected to outli... doc-ref 9fef docs/4-systems/HostingAndCertificates.md
    public static class ProcessUtil
    {
        // Starts a process with the given environment overrides layered on top of this process's own environment, and... doc-ref 844e docs/4-systems/HostingAndCertificates.md
        public static Process Spawn(string fileName, string arguments, string workingDirectory,
            IEnumerable<KeyValuePair<string, string>> environment,
            ChildProcessGroup group, ConcurrentQueue<string> stdout, ConcurrentQueue<string> stderr = null)
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

            if (environment != null)
            {
                foreach (var entry in environment) info.Environment[entry.Key] = entry.Value;
            }

            var errorSink = stderr ?? stdout;

            try
            {
                var process = new Process { StartInfo = info, EnableRaisingEvents = true };

                // Both streams are drained. A redirected stream that nobody reads fills
                // its pipe buffer and blocks the child permanently — a chatty child
                // reaches that within seconds.
                process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout?.Enqueue(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorSink?.Enqueue(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                group?.Adopt(process);

                Debug.Log($"[ProcessUtil] Started {Path.GetFileName(fileName)} {arguments} (pid {process.Id})");
                return process;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ProcessUtil] Failed to start {fileName}: {e.Message}");
                return null;
            }
        }

        public static void TryKill(Process process)
        {
            if (process == null) return;

            try
            {
                if (!process.HasExited) process.Kill();
                process.WaitForExit(2000);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ProcessUtil] Could not stop pid: {e.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        /// <summary>
        /// Finds an executable on PATH, then in a list of known install locations.
        /// The fallbacks matter: an installer that edits PATH does not change the
        /// environment of an already-running Unity, so a freshly installed tool is
        /// invisible until a restart that nobody realises they need.
        /// </summary>
        public static string ResolveExecutable(string name, IEnumerable<string> extraDirectories)
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
    }
}
