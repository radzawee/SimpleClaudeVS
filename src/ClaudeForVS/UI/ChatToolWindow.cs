using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.UI
{
    [Guid(PackageIds.ChatWindowGuidString)]
    public sealed class ChatToolWindow : ToolWindowPane
    {
        public ChatToolWindow() : base(null)
        {
            Caption = "Claude";
            BitmapImageMoniker = KnownMonikers.Comment;
            Content = new ChatControl();
        }

        internal ChatControl Chat => (ChatControl)Content;
    }
}
