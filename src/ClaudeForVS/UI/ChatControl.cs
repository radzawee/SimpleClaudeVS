using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeForVS.Services;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.UI
{
    /// <summary>Chat UI hosted in the Claude tool window. Each conversation maps to one resumable CLI session.</summary>
    internal sealed class ChatControl : UserControl
    {
        private readonly TextBox _transcript;
        private readonly TextBox _input;
        private readonly TextBlock _usage;
        private readonly TextBlock _status;
        private readonly Button _send;
        private readonly CheckBox _includeFile;
        private string _sessionId;
        private CancellationTokenSource _cts;

        public ChatControl()
        {
            _usage = Theme.Label(ClaudeCli.LastUsage?.ToShortString() ?? "Claude usage: send a message to update", dim: true);
            _usage.Margin = new Thickness(0);
            _usage.VerticalAlignment = VerticalAlignment.Center;
            var newChat = Theme.Button("New chat", (s, e) => NewChat());

            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(newChat, Dock.Right);
            top.Children.Add(newChat);
            top.Children.Add(_usage);

            _transcript = Theme.TextBox(multiline: true, readOnly: true);
            _transcript.Text = Welcome;

            _input = Theme.TextBox(multiline: true);
            _input.MinHeight = 60;
            _input.MaxHeight = 200;
            _input.PreviewKeyDown += OnInputKeyDown;

            _includeFile = Theme.CheckBox("Include active file");
            _status = Theme.Label("", dim: true);
            _status.Margin = new Thickness(8, 0, 8, 0);
            _status.VerticalAlignment = VerticalAlignment.Center;
            _send = Theme.Button("Send", (s, e) => OnSendClicked());

            var bottomBar = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(_send, Dock.Right);
            DockPanel.SetDock(_includeFile, Dock.Left);
            bottomBar.Children.Add(_send);
            bottomBar.Children.Add(_includeFile);
            bottomBar.Children.Add(_status);

            var inputArea = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(bottomBar, Dock.Bottom);
            inputArea.Children.Add(bottomBar);
            inputArea.Children.Add(_input);

            var root = new DockPanel { Margin = new Thickness(8) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(inputArea, Dock.Bottom);
            root.Children.Add(top);
            root.Children.Add(inputArea);
            root.Children.Add(_transcript);
            Content = Theme.Surface(new Border { Child = root });
            Theme.Surface(this);

            // Loaded/Unloaded fire each time the window is docked, hidden or shown.
            Loaded += (s, e) =>
            {
                ClaudeCli.UsageUpdated -= OnUsageUpdated;
                ClaudeCli.UsageUpdated += OnUsageUpdated;
                if (ClaudeCli.LastUsage != null) _usage.Text = ClaudeCli.LastUsage.ToShortString();
            };
            Unloaded += (s, e) => ClaudeCli.UsageUpdated -= OnUsageUpdated;
        }

        private const string Welcome =
            "Chat with Claude using your Claude subscription (via the Claude Code CLI).\n" +
            "The conversation runs in your solution folder; by default Claude can read files there but not change them.\n" +
            "Enter = send, Shift+Enter = new line.\n\n";

        public bool IsBusy => _cts != null;

        /// <summary>Sends a prompt. <paramref name="display"/> is what is shown in the transcript (the prompt may carry a large code block).</summary>
        public void Send(string prompt, string display = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (IsBusy) { _status.Text = "Claude is still answering – press Stop first."; return; }
            ThreadHelper.JoinableTaskFactory.RunAsync(() => SendAsync(prompt, display ?? prompt)).FileAndForget("ClaudeForVS/Chat");
        }

        private async System.Threading.Tasks.Task SendAsync(string prompt, string display)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var options = ClaudePackage.Options;

            var fullPrompt = new StringBuilder(prompt);
            if (_attached != null)
            {
                fullPrompt.Append("\n\n").Append(_attached);
                display = $"[{_attachedLabel}] " + display;
                _attached = _attachedLabel = null;
            }
            else if (_includeFile.IsChecked == true)
            {
                var sel = VsContext.GetSelection();
                if (sel != null)
                {
                    fullPrompt.Insert(0, $"Currently open file: {sel.FilePath}\n```\n{sel.Text}\n```\n\n");
                    display = $"[{Path.GetFileName(sel.FilePath)}] " + display;
                }
            }

            var cwd = VsContext.GetWorkingDirectory();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _send.Content = "Stop";
            _status.Text = "Claude is thinking…";

            Append($"\n▶ You\n{display.Trim()}\n\n◆ Claude\n");

            try
            {
                var result = await ClaudeCli.RunAsync(new ClaudeRequest
                {
                    Prompt = fullPrompt.ToString(),
                    WorkingDirectory = cwd,
                    Model = options.ChatModel,
                    Tools = options.ChatTools,
                    ResumeSessionId = _sessionId,
                    PersistSession = true,
                }, delta =>
                {
                    ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        Append(delta);
                    }).FileAndForget("ClaudeForVS/ChatStream");
                }, token);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _sessionId = result.SessionId ?? _sessionId;
                if (result.IsError) Append("\n[error] " + result.Text);
                Append("\n");
                _status.Text = "";
            }
            catch (OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Append("\n[stopped]\n");
                _status.Text = "";
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Append("\n[error] " + ex.Message + "\n");
                _status.Text = "";
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _cts?.Dispose();
                _cts = null;
                _send.Content = "Send";
            }
        }

        private void OnSendClicked()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (IsBusy) { _cts.Cancel(); return; }
            var text = _input.Text.Trim();
            if (text.Length == 0) return;
            _input.Clear();
            Send(text);
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                if (!IsBusy) OnSendClicked();
            }
        }

        private void NewChat()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _cts?.Cancel();
            _sessionId = null;
            _transcript.Text = Welcome;
            _input.Focus();
        }

        private void Append(string text)
        {
            _transcript.AppendText(text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
            _transcript.ScrollToEnd();
        }

        private void OnUsageUpdated(UsageSnapshot usage)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _usage.Text = usage.ToShortString();
            }).FileAndForget("ClaudeForVS/Usage");
        }

        /// <summary>Attaches a code block to the next message the user sends.</summary>
        public void AttachContext(string context, string label)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _attached = context;
            _attachedLabel = label;
            _status.Text = $"Attached {label} – type your question.";
            _input.Focus();
        }

        private string _attached, _attachedLabel;
    }
}
