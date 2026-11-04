using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace UnityCliConnector
{
    /// <summary>
    /// Compiles an exec snippet with csc in a subprocess, off the editor main thread.
    ///
    /// The compile is most of an exec's cost (measured 2.5–4.4 s of wall time per call on a
    /// backgrounded editor, with the main thread stalled for nearly all of it), so waiting for
    /// it is what froze the editor for the length of every exec. Everything here is process and
    /// file work only; the caller keeps the parts that need the main thread (Unity API access,
    /// reference discovery, Assembly.Load, invoke).
    /// </summary>
    internal static class ExecCompiler
    {
        const int StreamDrainGraceMs = 1000;

        internal readonly struct Result
        {
            internal readonly bool Ok;
            internal readonly byte[] Bytes;
            /// Raw compiler output when the compile failed; null when it timed out.
            internal readonly string Diagnostics;
            internal readonly bool TimedOut;

            Result(bool ok, byte[] bytes, string diagnostics, bool timedOut)
            {
                Ok = ok;
                Bytes = bytes;
                Diagnostics = diagnostics;
                TimedOut = timedOut;
            }

            internal static Result Success(byte[] bytes) => new Result(true, bytes, null, false);
            internal static Result Failure(string diagnostics) => new Result(false, null, diagnostics, false);
            internal static Result Timeout() => new Result(false, null, null, true);
        }

        /// <param name="id">File-name stem for this compile. Names must stay unique: two
        /// compiles sharing one output file would race on it.</param>
        internal static async Task<Result> CompileAsync(string source, IReadOnlyList<string> references,
            string cscPath, string dotnetPath, string id, int timeoutMs)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (references == null) throw new ArgumentNullException(nameof(references));

            var utf8 = new UTF8Encoding(false);
            var tmpDir = Path.Combine(Path.GetTempPath(), "unity-cli-exec");
            Directory.CreateDirectory(tmpDir);

            var srcFile = Path.Combine(tmpDir, id + ".cs");
            var outFile = Path.Combine(tmpDir, id + ".dll");
            var rspFile = Path.Combine(tmpDir, id + ".rsp");

            try
            {
                File.WriteAllText(srcFile, source, utf8);
                File.WriteAllText(rspFile, BuildResponseFile(outFile, srcFile, references), utf8);

                var rspArg = $"@\"{rspFile}\"";
                string exe, args;
                if (cscPath != null && cscPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    exe = dotnetPath;
                    args = $"exec \"{cscPath}\" {rspArg}";
                }
                else
                {
                    exe = cscPath;
                    args = rspArg;
                }

                var run = await RunProcessAsync(exe, args, timeoutMs).ConfigureAwait(false);
                if (run.TimedOut) return Result.Timeout();
                if (run.ExitCode != 0)
                    return Result.Failure(string.IsNullOrEmpty(run.Stderr) ? run.Stdout : run.Stderr);

                return Result.Success(File.ReadAllBytes(outFile));
            }
            finally
            {
                try { File.Delete(srcFile); } catch (IOException) { }
                try { File.Delete(outFile); } catch (IOException) { }
                try { File.Delete(rspFile); } catch (IOException) { }
            }
        }

        static string BuildResponseFile(string outFile, string srcFile, IReadOnlyList<string> references)
        {
            var rsp = new StringBuilder();
            rsp.AppendLine("-target:library");
            rsp.AppendLine($"-out:\"{outFile}\"");
            rsp.AppendLine("-nologo");
            rsp.AppendLine("-nowarn:0105,1701,1702");
            rsp.AppendLine("-langversion:latest");
            rsp.AppendLine($"\"{srcFile}\"");
            for (var i = 0; i < references.Count; i++)
                rsp.AppendLine($"-r:\"{references[i]}\"");
            return rsp.ToString();
        }

        readonly struct ProcessRun
        {
            internal readonly int ExitCode;
            internal readonly string Stdout;
            internal readonly string Stderr;
            internal readonly bool TimedOut;

            internal ProcessRun(int exitCode, string stdout, string stderr, bool timedOut)
            {
                ExitCode = exitCode;
                Stdout = stdout;
                Stderr = stderr;
                TimedOut = timedOut;
            }
        }

        static async Task<ProcessRun> RunProcessAsync(string exe, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using (var proc = Process.Start(psi))
            {
                // Both streams are drained concurrently on the thread pool: a compiler that
                // fills a pipe buffer while the other side waits on process exit deadlocks,
                // and Mono's async reads over redirected process streams have historically
                // been unreliable, so these are blocking reads parked off the main thread.
                var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
                var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());
                // A read still pending when the deadline passes faults against the disposed
                // process; an unobserved fault would surface as an UnobservedTaskException.
                ObserveFault(stdoutTask);
                ObserveFault(stderrTask);

                var exited = ExitedTask(proc);
                var timedOut = !ReferenceEquals(await Task.WhenAny(exited, Task.Delay(timeoutMs)).ConfigureAwait(false), exited);
                if (timedOut) TryKill(proc);

                // Give the readers a moment to finish after the kill before the process is
                // disposed under them; a lingering one is detached and its fault observed.
                await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(StreamDrainGraceMs))
                    .ConfigureAwait(false);

                var stdout = Finished(stdoutTask) ? stdoutTask.Result : string.Empty;
                var stderr = Finished(stderrTask) ? stderrTask.Result : string.Empty;
                var exitCode = timedOut ? -1 : SafeExitCode(proc);
                return new ProcessRun(exitCode, stdout, stderr, timedOut);
            }
        }

        static Task ExitedTask(Process proc)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (proc.HasExited)
            {
                tcs.TrySetResult(true);
                return tcs.Task;
            }

            proc.EnableRaisingEvents = true;
            proc.Exited += (_, __) => tcs.TrySetResult(true);
            // The process can exit between the check above and the subscription.
            if (proc.HasExited) tcs.TrySetResult(true);
            return tcs.Task;
        }

        static void TryKill(Process proc)
        {
            try
            {
                proc.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
                // Already gone, or not killable here; the timeout is still the answer.
            }
        }

        static int SafeExitCode(Process proc)
        {
            try
            {
                return proc.ExitCode;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
                return -1;
            }
        }

        static bool Finished(Task<string> task) => task.IsCompleted && !task.IsFaulted && !task.IsCanceled;

        static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
