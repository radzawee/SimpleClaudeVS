using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.Options
{
    /// <summary>Tools &gt; Options &gt; Claude &gt; General. Values live in <see cref="Settings"/>.</summary>
    public class GeneralOptions : DialogPage
    {
        [Browsable(false)]
        public ClaudeSettings Settings { get; } = new ClaudeSettings();

        [Category("Claude CLI")]
        [DisplayName("Claude executable")]
        [Description("Path to claude.exe (Claude Code CLI). Leave as 'claude' to look it up on PATH and in the default install locations.")]
        public string ClaudePath { get => Settings.ClaudePath; set => Settings.ClaudePath = value; }

        [Category("Claude CLI")]
        [DisplayName("Use subscription login only")]
        [Description("Removes ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN from the environment of the CLI process so your Claude subscription (claude /login) is used rather than pay-as-you-go API billing.")]
        public bool ForceSubscriptionAuth { get => Settings.ForceSubscriptionAuth; set => Settings.ForceSubscriptionAuth = value; }

        [Category("Claude CLI")]
        [DisplayName("Timeout (seconds)")]
        [Description("Maximum time a single Claude request may take.")]
        public int TimeoutSeconds { get => Settings.TimeoutSeconds; set => Settings.TimeoutSeconds = value; }

        [Category("Commit messages")]
        [DisplayName("Model")]
        [Description("Model alias or full name used for commit messages (e.g. haiku, sonnet, opus). Empty = CLI default.")]
        public string CommitModel { get => Settings.CommitModel; set => Settings.CommitModel = value; }

        [Category("Commit messages")]
        [DisplayName("Use Conventional Commits")]
        [Description("Format messages as 'type(scope): summary' (feat, fix, refactor, ...).")]
        public bool UseConventionalCommits { get => Settings.UseConventionalCommits; set => Settings.UseConventionalCommits = value; }

        [Category("Commit messages")]
        [DisplayName("Include body")]
        [Description("Add a short body explaining what and why, after the summary line.")]
        public bool IncludeBody { get => Settings.IncludeBody; set => Settings.IncludeBody = value; }

        [Category("Commit messages")]
        [DisplayName("Extra instructions")]
        [Description("Additional instructions, e.g. 'Prefix with the Jira ticket from the branch name' or 'Write in Polish'.")]
        public string CommitInstructions { get => Settings.CommitInstructions; set => Settings.CommitInstructions = value; }

        [Category("Commit messages")]
        [DisplayName("Max diff characters")]
        [Description("Diffs longer than this are truncated before being sent to Claude.")]
        public int MaxDiffChars { get => Settings.MaxDiffChars; set => Settings.MaxDiffChars = value; }

        [Category("Chat")]
        [DisplayName("Model")]
        [Description("Model used in the Claude Chat window and selection commands. Empty = CLI default.")]
        public string ChatModel { get => Settings.ChatModel; set => Settings.ChatModel = value; }

        [Category("Chat")]
        [DisplayName("Allowed tools")]
        [Description("Built-in Claude Code tools the chat may use, comma separated. Default is read-only (Read,Grep,Glob). Empty = no tools.")]
        public string ChatTools { get => Settings.ChatTools; set => Settings.ChatTools = value; }
    }
}
