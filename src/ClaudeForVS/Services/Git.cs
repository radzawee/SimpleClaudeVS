using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Services
{
    internal sealed class GitResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public bool Success => ExitCode == 0;
    }

    internal static class Git
    {
        public static async Task<GitResult> RunAsync(string workingDirectory, string arguments, CancellationToken cancellationToken = default)
        {
            var psi = new ProcessStartInfo("git", "-c core.quotepath=false -c color.ui=never " + arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

            using (var process = new Process { StartInfo = psi })
            {
                try { process.Start(); }
                catch (Exception ex) { throw new InvalidOperationException("Could not run git. Is Git for Windows installed and on PATH? " + ex.Message, ex); }

                using (cancellationToken.Register(() => { try { process.Kill(); } catch { } }))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new GitResult { ExitCode = process.ExitCode, Output = await stdout.ConfigureAwait(false), Error = await stderr.ConfigureAwait(false) };
                }
            }
        }

        public static async Task<string> FindRepositoryRootAsync(string startDirectory)
        {
            if (string.IsNullOrEmpty(startDirectory) || !Directory.Exists(startDirectory)) return null;
            var result = await RunAsync(startDirectory, "rev-parse --show-toplevel").ConfigureAwait(false);
            if (!result.Success) return null;
            var root = result.Output.Trim();
            return string.IsNullOrEmpty(root) ? null : Path.GetFullPath(root);
        }

        public static async Task<GitResult> CommitAsync(string repoRoot, string message, bool stageAll)
        {
            if (stageAll)
            {
                var add = await RunAsync(repoRoot, "add -A").ConfigureAwait(false);
                if (!add.Success) return add;
            }

            var file = Path.Combine(Path.GetTempPath(), "claude-vs-commit-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(file, message.Replace("\r\n", "\n"), new UTF8Encoding(false));
            try
            {
                return await RunAsync(repoRoot, "commit --cleanup=strip -F " + ClaudeCli.QuoteArg(file)).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }
    }
}
