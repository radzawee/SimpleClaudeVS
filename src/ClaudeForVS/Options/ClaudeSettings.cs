namespace ClaudeForVS.Options
{
    /// <summary>
    /// Plain settings object used by the services. <see cref="GeneralOptions"/> (the Tools &gt; Options page)
    /// stores its values here, which keeps the services independent of the VS shell.
    /// </summary>
    public sealed class ClaudeSettings
    {
        public string ClaudePath { get; set; } = "claude";
        public bool ForceSubscriptionAuth { get; set; } = true;
        public int TimeoutSeconds { get; set; } = 300;

        public string CommitModel { get; set; } = "haiku";
        public bool UseConventionalCommits { get; set; } = true;
        public bool IncludeBody { get; set; } = true;
        public string CommitInstructions { get; set; } = "";
        public int MaxDiffChars { get; set; } = 80000;

        public string ChatModel { get; set; } = "";
        public string ChatTools { get; set; } = "Read,Grep,Glob";
    }
}
