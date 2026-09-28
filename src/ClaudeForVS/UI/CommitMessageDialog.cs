using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ClaudeForVS.Services;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.UI
{
    /// <summary>Shows the generated message; the user can edit it, copy it, or commit directly.</summary>
    internal sealed class CommitMessageDialog : DialogWindow
    {
        private readonly string _repoRoot;
        private readonly TextBox _message;
        private readonly TextBlock _header;
        private readonly TextBlock _status;
        private readonly CheckBox _stageAll;
        private readonly Button _regenerate, _copy, _commit;
        private CommitContext _context;
        private CancellationTokenSource _cts;

        public CommitMessageDialog(string repoRoot)
        {
            _repoRoot = repoRoot;
            Title = "Claude – Commit Message";
            Width = 720;
            Height = 460;
            MinWidth = 420;
            MinHeight = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            HasMinimizeButton = false;
            ShowInTaskbar = false;

            _header = Theme.Label("Collecting changes…", dim: true);
            _message = Theme.TextBox(multiline: true);
            _message.FontSize = 13;
            _status = Theme.Label("", dim: true);
            _status.Margin = new Thickness(0, 6, 0, 0);
            _stageAll = Theme.CheckBox("Stage all changes (git add -A) before committing");
            _stageAll.Visibility = Visibility.Collapsed;

            _regenerate = Theme.Button("Regenerate", (s, e) => Start());
            _copy = Theme.Button("Copy", (s, e) => CopyToClipboard());
            _commit = Theme.Button("Commit", (s, e) => ThreadHelper.JoinableTaskFactory.RunAsync(CommitAsync).FileAndForget("ClaudeForVS/Commit"), isDefault: false);
            var close = Theme.Button("Close", (s, e) => Close());
            close.IsCancel = true;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_regenerate);
            buttons.Children.Add(_copy);
            buttons.Children.Add(_commit);
            buttons.Children.Add(close);

            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
            DockPanel.SetDock(buttons, Dock.Right);
            footer.Children.Add(buttons);
            footer.Children.Add(_stageAll);

            var root = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(_header, Dock.Top);
            DockPanel.SetDock(footer, Dock.Bottom);
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_header);
            root.Children.Add(footer);
            root.Children.Add(_status);
            root.Children.Add(_message);
            Content = Theme.Surface(new Border { Child = root });

            Loaded += (s, e) => Start();
            Closed += (s, e) => _cts?.Cancel();
        }

        private void Start()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => GenerateAsync(token)).FileAndForget("ClaudeForVS/GenerateCommitMessage");
        }

        private async System.Threading.Tasks.Task GenerateAsync(CancellationToken token)
        {
            SetBusy(true, "Collecting changes…");
            _message.Text = "";
            try
            {
                var options = ClaudePackage.Options;
                if (_context == null)
                {
                    _context = await CommitMessageGenerator.CollectAsync(_repoRoot, options.MaxDiffChars, token);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    _header.Text = $"{_repoRoot}   ·   branch {_context.Branch}   ·   " +
                                   (_context.StagedOnly ? "staged changes" : "nothing staged – using all working tree changes") +
                                   (_context.Truncated ? " (diff truncated)" : "");
                    _stageAll.Visibility = _context.StagedOnly ? Visibility.Collapsed : Visibility.Visible;
                    _stageAll.IsChecked = !_context.StagedOnly;
                }

                if (_context.IsEmpty)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    SetBusy(false, "There are no changes to commit.");
                    _commit.IsEnabled = _copy.IsEnabled = false;
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                SetBusy(true, $"Asking Claude ({DisplayModel(options.CommitModel)})…");

                var done = false;
                var message = await CommitMessageGenerator.GenerateAsync(_context, delta =>
                {
                    ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (!done && !token.IsCancellationRequested) _message.AppendText(delta);
                    }).FileAndForget("ClaudeForVS/Stream");
                }, token);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                done = true;
                _message.Text = message;
                _message.Focus();
                _message.CaretIndex = _message.Text.Length;
                SetBusy(false, ClaudeCli.LastUsage?.ToShortString() ?? "");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetBusy(false, "Error: " + ex.Message);
            }
        }

        private void CopyToClipboard()
        {
            if (string.IsNullOrWhiteSpace(_message.Text)) return;
            Clipboard.SetText(_message.Text.Trim());
            _status.Text = "Copied. Paste it into the Git Changes message box (Ctrl+V).";
        }

        private async System.Threading.Tasks.Task CommitAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var text = _message.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            var stageAll = _stageAll.Visibility == Visibility.Visible && _stageAll.IsChecked == true;
            SetBusy(true, "Committing…");
            var result = await Git.CommitAsync(_repoRoot, text, stageAll);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (result.Success)
            {
                VsContext.SetStatus("Claude: committed – " + FirstLine(text));
                Close();
            }
            else
            {
                SetBusy(false, "git commit failed: " + (result.Error + result.Output).Trim());
            }
        }

        private void SetBusy(bool busy, string status)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _status.Text = status;
            _regenerate.IsEnabled = !busy;
            _commit.IsEnabled = !busy;
            _copy.IsEnabled = !busy;
            _message.IsReadOnly = busy;
        }

        private static string DisplayModel(string model) => string.IsNullOrWhiteSpace(model) ? "default model" : model;

        private static string FirstLine(string text)
        {
            var i = text.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? text : text.Substring(0, i);
        }
    }
}
