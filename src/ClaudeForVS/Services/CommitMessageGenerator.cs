using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Options;

namespace ClaudeForVS.Services
{
    internal sealed class CommitContext
    {
        public string RepoRoot { get; set; }
        public string Branch { get; set; }
        public string Diff { get; set; }
        public string Stat { get; set; }
        public string UntrackedFiles { get; set; }
        public string RecentLog { get; set; }
        /// <summary>True when the diff is the staged changes; false when nothing was staged and all working tree changes were used.</summary>
        public bool StagedOnly { get; set; }
        public bool Truncated { get; set; }
        public bool IsEmpty => string.IsNullOrWhiteSpace(Diff) && string.IsNullOrWhiteSpace(UntrackedFiles);
    }

    internal static class CommitMessageGenerator
    {
        public static async Task<CommitContext> CollectAsync(string repoRoot, int maxDiffChars, CancellationToken ct)
        {
            var ctx = new CommitContext { RepoRoot = repoRoot, StagedOnly = true };

            ctx.Branch = (await Git.RunAsync(repoRoot, "rev-parse --abbrev-ref HEAD", ct).ConfigureAwait(false)).Output?.Trim();
            var log = await Git.RunAsync(repoRoot, "log -n 12 --no-merges --pretty=format:%s", ct).ConfigureAwait(false);
            ctx.RecentLog = log.Success ? log.Output.Trim() : "";

            var staged = await Git.RunAsync(repoRoot, "diff --cached --no-ext-diff -M", ct).ConfigureAwait(false);
            if (!staged.Success) throw new InvalidOperationException("git diff failed: " + staged.Error.Trim());

            if (!string.IsNullOrWhiteSpace(staged.Output))
            {
                ctx.Diff = staged.Output;
                ctx.Stat = (await Git.RunAsync(repoRoot, "diff --cached --stat", ct).ConfigureAwait(false)).Output;
            }
            else
            {
                // Nothing staged: describe everything in the working tree (HEAD may not exist in a fresh repo).
                ctx.StagedOnly = false;
                var all = await Git.RunAsync(repoRoot, "diff HEAD --no-ext-diff -M", ct).ConfigureAwait(false);
                if (!all.Success) all = await Git.RunAsync(repoRoot, "diff --no-ext-diff -M", ct).ConfigureAwait(false);
                ctx.Diff = all.Output;
                ctx.Stat = (await Git.RunAsync(repoRoot, "diff HEAD --stat", ct).ConfigureAwait(false)).Output;
                ctx.UntrackedFiles = (await Git.RunAsync(repoRoot, "ls-files --others --exclude-standard", ct).ConfigureAwait(false)).Output?.Trim();
            }

            if (ctx.Diff != null && ctx.Diff.Length > maxDiffChars)
            {
                ctx.Diff = ctx.Diff.Substring(0, maxDiffChars);
                ctx.Truncated = true;
            }
            return ctx;
        }

        public static string BuildPrompt(CommitContext ctx, ClaudeSettings options)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You write git commit messages. Write the commit message for the changes below.");
            sb.AppendLine();
            sb.AppendLine("Rules:");
            if (options.UseConventionalCommits)
                sb.AppendLine("- First line: Conventional Commits format `type(optional-scope): summary` (types: feat, fix, refactor, perf, docs, test, build, ci, chore, style).");
            sb.AppendLine("- First line: imperative mood, at most 72 characters, no trailing period.");
            if (options.IncludeBody)
                sb.AppendLine("- If the change is not trivial, add a blank line and a short body (wrapped at 72 chars) explaining what changed and why. Use `-` bullets for multiple points. Do not narrate the diff line by line.");
            else
                sb.AppendLine("- Output only the single summary line, no body.");
            sb.AppendLine("- Match the language and conventions of the recent commit messages if they follow a clear style.");
            sb.AppendLine("- Output ONLY the raw commit message. No preamble, no explanation, no Markdown code fences, no quotes.");
            if (!string.IsNullOrWhiteSpace(options.CommitInstructions))
                sb.AppendLine("- " + options.CommitInstructions.Trim());
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(ctx.Branch)) sb.AppendLine("Current branch: " + ctx.Branch);
            if (!string.IsNullOrWhiteSpace(ctx.RecentLog))
            {
                sb.AppendLine("Recent commit messages (for style):");
                sb.AppendLine(ctx.RecentLog);
            }
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(ctx.Stat))
            {
                sb.AppendLine("<stat>");
                sb.AppendLine(ctx.Stat.TrimEnd());
                sb.AppendLine("</stat>");
            }
            if (!string.IsNullOrWhiteSpace(ctx.UntrackedFiles))
            {
                sb.AppendLine("<new_untracked_files>");
                sb.AppendLine(ctx.UntrackedFiles);
                sb.AppendLine("</new_untracked_files>");
            }
            sb.AppendLine("<diff>");
            sb.AppendLine(ctx.Diff?.TrimEnd());
            if (ctx.Truncated) sb.AppendLine("[... diff truncated ...]");
            sb.AppendLine("</diff>");
            return sb.ToString();
        }

        public static async Task<string> GenerateAsync(CommitContext ctx, Action<string> onDelta, CancellationToken ct)
        {
            var options = ClaudePackage.Options;
            var result = await ClaudeCli.RunAsync(new ClaudeRequest
            {
                Prompt = BuildPrompt(ctx, options),
                WorkingDirectory = ctx.RepoRoot,
                Model = options.CommitModel,
                Tools = "",
                PersistSession = false,
            }, onDelta, ct).ConfigureAwait(false);

            if (result.IsError) throw new ClaudeCliException(result.Text);
            return Clean(result.Text);
        }

        /// <summary>Strips code fences or quotes the model may add despite instructions.</summary>
        internal static string Clean(string text)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Trim().Split('\n').ToList();
            if (lines.Count > 0 && lines[0].TrimStart().StartsWith("```")) lines.RemoveAt(0);
            if (lines.Count > 0 && lines[lines.Count - 1].Trim() == "```") lines.RemoveAt(lines.Count - 1);
            var message = string.Join("\n", lines).Trim();
            if (message.Length > 1 && message[0] == '"' && message[message.Length - 1] == '"')
                message = message.Substring(1, message.Length - 2);
            return message.Replace("\n", Environment.NewLine);
        }
    }
}
