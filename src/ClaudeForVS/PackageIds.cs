using System;

namespace ClaudeForVS
{
    /// <summary>Must stay in sync with the symbols in ClaudePackage.vsct.</summary>
    internal static class PackageIds
    {
        public const string PackageGuidString = "9ac50cca-4a81-45b1-8000-0e5f84c72c59";
        public const string CommandSetGuidString = "d0fd17ea-46e8-4f7e-a585-baba894a097c";
        public const string ChatWindowGuidString = "39786547-e6b4-431a-988f-7011d232d893";

        public static readonly Guid CommandSet = new Guid(CommandSetGuidString);

        public const int GenerateCommitMessage = 0x0100;
        public const int OpenChat = 0x0101;
        public const int RefreshUsage = 0x0102;
        public const int ExplainSelection = 0x0200;
        public const int ReviewSelection = 0x0201;
        public const int AskAboutSelection = 0x0202;
    }
}
