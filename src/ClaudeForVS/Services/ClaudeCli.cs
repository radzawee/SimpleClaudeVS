using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeForVS.Services
{
    internal sealed class ClaudeRequest
    {
        /// <summary>Full prompt. Sent over stdin so it never has to survive command-line quoting.</summary>
        public string Prompt { get; set; } = "";
        public string WorkingDirectory { get; set; }
        public string Model { get; set; }
        /// <summary>Comma separated built-in tool names; empty or null disables all tools.</summary>
        public string Tools { get; set; }
        /// <summary>Continue an earlier conversation.</summary>
        public string ResumeSessionId { get; set; }
        /// <summary>False for one-shot requests (commit messages) so they do not clutter `claude --resume`.</summary>
        public bool PersistSession { get; set; }
    }

    internal sealed class ClaudeResult
    {
        public string Text { get; set; } = "";
        public string SessionId { get; set; }
        public bool IsError { get; set; }
        public double? CostUsd { get; set; }
        public long? DurationMs { get; set; }
    }

    /// <summary>Subscription rate-limit windows as reported by the CLI's rate_limit_event.</summary>
    internal sealed class UsageSnapshot
    {
        public string Status { get; set; }
        public double? FiveHourUtilization { get; set; }
        public DateTimeOffset? FiveHourResetsAt { get; set; }
        public double? SevenDayUtilization { get; set; }
        public DateTimeOffset? SevenDayResetsAt { get; set; }
        public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.Now;

        public string ToShortString()
        {
            var parts = new List<string>();
            if (FiveHourUtilization.HasValue) parts.Add($"5h {FiveHourUtilization.Value:P0}{Resets(FiveHourResetsAt)}");
            if (SevenDayUtilization.HasValue) parts.Add($"7d {SevenDayUtilization.Value:P0}{Resets(SevenDayResetsAt)}");
            if (parts.Count == 0) return "Claude usage: unknown";
            var text = "Claude usage: " + string.Join(" · ", parts);
            if (!string.IsNullOrEmpty(Status) && Status != "allowed") text += $" ({Status})";
            return text;
        }

        private static string Resets(DateTimeOffset? at)
        {
            if (!at.HasValue) return "";
            var local = at.Value.ToLocalTime();
            return local.Date == DateTime.Today ? $" (resets {local:HH:mm})" : $" (resets {local:ddd HH:mm})";
        }
    }

    internal sealed class ClaudeCliException : Exception
    {
        public ClaudeCliException(string message) : base(message) { }
    }

    /// <summary>
    /// Runs the Claude Code CLI in print mode. The CLI authenticates with the user's
    /// Claude subscription (claude /login), so no API key is needed.
    /// </summary>
    internal static class ClaudeCli
    {
        public static UsageSnapshot LastUsage { get; private set; }
        public static event Action<UsageSnapshot> UsageUpdated;

        /// <param name="onActivity">Called with a short description whenever Claude uses a tool (e.g. "Read · Program.cs").</param>
        public static async Task<ClaudeResult> RunAsync(ClaudeRequest request, Action<string> onTextDelta, CancellationToken cancellationToken, Action<string> onActivity = null)
        {
            var options = ClaudePackage.Options;
            var exe = ResolveExecutable(options.ClaudePath);

            var args = new List<string>
            {
                "-p",
                "--output-format", "stream-json",
                "--verbose",
                "--include-partial-messages",
                "--permission-prompts", "none",
            };
            args.Add("--tools");
            args.Add(string.IsNullOrWhiteSpace(request.Tools) ? "" : request.Tools.Replace(" ", ""));
            if (!string.IsNullOrWhiteSpace(request.Model)) { args.Add("--model"); args.Add(request.Model.Trim()); }
            if (!string.IsNullOrEmpty(request.ResumeSessionId)) { args.Add("--resume"); args.Add(request.ResumeSessionId); }
            if (!request.PersistSession) args.Add("--no-session-persistence");

            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Directory.Exists(request.WorkingDirectory)
                    ? request.WorkingDirectory
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };

            var argLine = string.Join(" ", args.Select(QuoteArg));
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                psi.Arguments = "/d /s /c \"" + QuoteArg(exe) + " " + argLine + "\"";
            }
            else
            {
                psi.FileName = exe;
                psi.Arguments = argLine;
            }

            if (options.ForceSubscriptionAuth)
            {
                psi.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
                psi.EnvironmentVariables.Remove("ANTHROPIC_AUTH_TOKEN");
            }

            var result = new ClaudeResult();
            var streamed = new StringBuilder();
            var stderr = new StringBuilder();
            string finalResult = null;

            using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds))))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    throw new ClaudeCliException(
                        $"Could not start the Claude CLI ('{exe}'): {ex.Message}\n\n" +
                        "Install Claude Code (https://claude.com/claude-code), run `claude` once in a terminal and /login with your subscription, " +
                        "or set the executable path in Tools > Options > Claude.");
                }

                using (linked.Token.Register(() => { try { if (!process.HasExited) KillTree(process); } catch { } }))
                {
                    var stderrTask = Task.Run(async () =>
                    {
                        string line;
                        while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                            stderr.AppendLine(line);
                    });

                    // .NET Framework has no StandardInputEncoding, so write UTF-8 bytes directly.
                    var bytes = new UTF8Encoding(false).GetBytes(request.Prompt ?? "");
                    await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                    process.StandardInput.Close();

                    string lineOut;
                    while ((lineOut = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (lineOut.Length == 0 || lineOut[0] != '{') continue;
                        JObject msg;
                        try { msg = JObject.Parse(lineOut); } catch { continue; }

                        switch ((string)msg["type"])
                        {
                            case "system":
                                result.SessionId = (string)msg["session_id"] ?? result.SessionId;
                                break;
                            case "stream_event":
                                var ev = msg["event"];
                                if ((string)ev?["type"] == "content_block_delta" && (string)ev["delta"]?["type"] == "text_delta")
                                {
                                    var text = (string)ev["delta"]["text"];
                                    streamed.Append(text);
                                    onTextDelta?.Invoke(text);
                                }
                                else if ((string)ev?["type"] == "message_start" && streamed.Length > 0 && !EndsWithBlankLine(streamed))
                                {
                                    // New assistant turn after tool use: keep turns visually separated.
                                    streamed.Append("\n\n");
                                    onTextDelta?.Invoke("\n\n");
                                }
                                break;
                            case "assistant":
                                if (onActivity != null && msg["message"]?["content"] is JArray content)
                                {
                                    foreach (var block in content.Where(b => (string)b["type"] == "tool_use"))
                                        onActivity(DescribeToolUse((string)block["name"], block["input"]));
                                }
                                break;
                            case "rate_limit_event":
                                PublishUsage(msg["rate_limit_info"]);
                                break;
                            case "result":
                                result.SessionId = (string)msg["session_id"] ?? result.SessionId;
                                result.IsError = (bool?)msg["is_error"] ?? false;
                                result.CostUsd = (double?)msg["total_cost_usd"];
                                result.DurationMs = (long?)msg["duration_ms"];
                                finalResult = (string)msg["result"];
                                break;
                        }
                    }

                    await stderrTask.ConfigureAwait(false);
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                if (timeout.IsCancellationRequested) throw new ClaudeCliException($"Claude did not answer within {options.TimeoutSeconds} s.");

                if (finalResult == null && streamed.Length == 0)
                {
                    var err = stderr.ToString().Trim();
                    throw new ClaudeCliException(string.IsNullOrEmpty(err)
                        ? $"The Claude CLI exited with code {process.ExitCode} without a response. Run `claude` in a terminal to check that you are logged in."
                        : err);
                }
            }

            result.Text = !string.IsNullOrEmpty(finalResult) ? finalResult : streamed.ToString();
            return result;
        }

        private static void PublishUsage(JToken info)
        {
            if (info == null) return;
            var windows = info["unifiedWindows"];
            var snapshot = new UsageSnapshot
            {
                Status = (string)info["status"],
                FiveHourUtilization = (double?)windows?["five_hour"]?["utilization"],
                FiveHourResetsAt = FromUnix((long?)windows?["five_hour"]?["resetsAt"]),
                SevenDayUtilization = (double?)windows?["seven_day"]?["utilization"],
                SevenDayResetsAt = FromUnix((long?)windows?["seven_day"]?["resetsAt"]),
            };
            if (windows == null && (string)info["rateLimitType"] == "five_hour")
                snapshot.FiveHourResetsAt = FromUnix((long?)info["resetsAt"]);

            LastUsage = snapshot;
            UsageUpdated?.Invoke(snapshot);
        }

        private static string DescribeToolUse(string name, JToken input)
        {
            var target = (string)input?["file_path"] ?? (string)input?["pattern"] ?? (string)input?["path"] ?? (string)input?["command"];
            if (string.IsNullOrEmpty(target)) return name;
            if ((string)input?["file_path"] != null)
            {
                try { target = Path.GetFileName(target); } catch (ArgumentException) { }
            }
            if (target.Length > 60) target = target.Substring(0, 57) + "…";
            return $"{name} · {target}";
        }

        private static DateTimeOffset? FromUnix(long? seconds) =>
            seconds.HasValue ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : (DateTimeOffset?)null;

        private static bool EndsWithBlankLine(StringBuilder sb) =>
            sb.Length >= 2 && sb[sb.Length - 1] == '\n' && sb[sb.Length - 2] == '\n';

        internal static string ResolveExecutable(string configured)
        {
            configured = string.IsNullOrWhiteSpace(configured) ? "claude" : configured.Trim().Trim('"');
            if (Path.IsPathRooted(configured)) return configured;

            var names = Path.HasExtension(configured)
                ? new[] { configured }
                : new[] { configured + ".exe", configured + ".cmd", configured };

            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).ToList();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // VS may have been started before Claude Code was installed, so PATH can be stale.
            dirs.Add(Path.Combine(home, ".local", "bin"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            dirs.Add(Path.Combine(home, ".claude", "local"));

            foreach (var dir in dirs.Where(d => !string.IsNullOrWhiteSpace(d)))
            {
                foreach (var name in names)
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim().Trim('"'), name);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch (ArgumentException) { }
                }
            }
            return configured;
        }

        private static void KillTree(Process process)
        {
            using (var kill = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {process.Id}") { CreateNoWindow = true, UseShellExecute = false }))
                kill?.WaitForExit(5000);
        }

        /// <summary>Quotes one argument per the MSVCRT / CommandLineToArgvW rules.</summary>
        internal static string QuoteArg(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '\n' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); }
                else { sb.Append('\\', backslashes); }
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            return sb.Append('"').ToString();
        }
    }
}
