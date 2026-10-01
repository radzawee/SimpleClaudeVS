using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ClaudeForVS.Options;
using ClaudeForVS.Services;
using ClaudeForVS.UI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeForVS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Claude for Visual Studio", "Use your Claude subscription in Visual Studio via the Claude Code CLI.", "1.1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ChatToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids.SolutionExplorer, Orientation = ToolWindowOrientation.Right)]
    [ProvideOptionPage(typeof(GeneralOptions), "Claude", "General", 0, 0, true)]
    [Guid(PackageIds.PackageGuidString)]
    public sealed class ClaudePackage : AsyncPackage
    {
        private static ClaudePackage _instance;

        internal static ClaudeSettings Options =>
            ((GeneralOptions)_instance?.GetDialogPage(typeof(GeneralOptions)))?.Settings ?? new ClaudeSettings();

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            _instance = this;
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var commands = (OleMenuCommandService)await GetServiceAsync(typeof(IMenuCommandService));
            Add(commands, PackageIds.GenerateCommitMessage, (s, e) => GenerateCommitMessage());
            Add(commands, PackageIds.OpenChat, (s, e) => JoinableTaskFactory.RunAsync(async () => await ShowChatAsync()).FileAndForget("ClaudeForVS/OpenChat"));
            Add(commands, PackageIds.RefreshUsage, (s, e) => JoinableTaskFactory.RunAsync(CheckUsageAsync).FileAndForget("ClaudeForVS/Usage"));
            Add(commands, PackageIds.ExplainSelection, (s, e) => SendSelection("Explain what this code does, how it works, and anything non-obvious about it."));
            Add(commands, PackageIds.ReviewSelection, (s, e) => SendSelection("Review this code. List concrete bugs, edge cases and risky patterns first (with line references), then brief suggestions. Skip praise."));
            Add(commands, PackageIds.AskAboutSelection, (s, e) => SendSelection(null));

            ClaudeCli.UsageUpdated += usage => JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                VsContext.SetStatus(usage.ToShortString());
            }).FileAndForget("ClaudeForVS/UsageStatus");
        }

        private static void Add(OleMenuCommandService service, int id, EventHandler handler) =>
            service.AddCommand(new MenuCommand(handler, new CommandID(PackageIds.CommandSet, id)));

        private void GenerateCommitMessage()
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var start = VsContext.GetWorkingDirectory();
                var repo = await Git.FindRepositoryRootAsync(start);
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                if (repo == null)
                {
                    ShowMessage(start == null
                        ? "Open a solution or folder that is inside a git repository first."
                        : $"'{start}' is not inside a git repository.");
                    return;
                }
                new CommitMessageDialog(repo).ShowModal();
            }).FileAndForget("ClaudeForVS/GenerateCommitMessage");
        }

        private void SendSelection(string instruction)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var sel = VsContext.GetSelection();
                if (sel == null) { ShowMessage("Open a code file first."); return; }

                var window = await ShowChatAsync();
                if (window == null) return;

                var name = Path.GetFileName(sel.FilePath);
                var range = $"{name}:{sel.StartLine}-{sel.EndLine}";
                var code = $"File: {sel.FilePath} (lines {sel.StartLine}-{sel.EndLine})\n```\n{sel.Text}\n```";

                if (instruction == null)
                {
                    // "Ask about selection": attach the code to the next message the user types.
                    window.Chat.AttachContext(code, range);
                    return;
                }
                window.Chat.Send(instruction + "\n\n" + code, $"[{range}] {instruction}");
            }).FileAndForget("ClaudeForVS/Selection");
        }

        private async System.Threading.Tasks.Task<ChatToolWindow> ShowChatAsync() =>
            (ChatToolWindow)await ShowToolWindowAsync(typeof(ChatToolWindow), 0, true, DisposalToken);

        private async Task CheckUsageAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            VsContext.SetStatus("Claude: checking subscription usage…");
            try
            {
                // Any request returns a rate_limit_event; use the cheapest model and no tools.
                await ClaudeCli.RunAsync(new ClaudeRequest { Prompt = "Reply with: ok", Model = "haiku", Tools = "" }, null, DisposalToken);
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var usage = ClaudeCli.LastUsage;
                ShowMessage(usage == null
                    ? "The CLI did not report subscription limits. You may be using an API key rather than a Claude subscription."
                    : Describe(usage), OLEMSGICON.OLEMSGICON_INFO);
            }
            catch (Exception ex)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowMessage(ex.Message);
            }
        }

        private static string Describe(UsageSnapshot u)
        {
            string Line(string name, double? value, DateTimeOffset? resets) =>
                value.HasValue ? $"{name}: {value.Value:P0} used" + (resets.HasValue ? $", resets {resets.Value.ToLocalTime():ddd yyyy-MM-dd HH:mm}" : "") : $"{name}: n/a";

            return "Claude subscription usage\n\n" +
                   Line("Current 5-hour session", u.FiveHourUtilization, u.FiveHourResetsAt) + "\n" +
                   Line("Weekly (7-day)", u.SevenDayUtilization, u.SevenDayResetsAt) + "\n\n" +
                   $"Status: {u.Status}";
        }

        private void ShowMessage(string text, OLEMSGICON icon = OLEMSGICON.OLEMSGICON_WARNING)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.ShowMessageBox(this, text, "Claude", icon, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}
