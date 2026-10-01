using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeForVS.Services;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.UI
{
    /// <summary>Chat UI hosted in the Claude tool window. Each conversation maps to one resumable CLI session.</summary>
    public partial class ChatControl : UserControl
    {
        private static readonly (string Label, string Prompt, bool WithFile)[] SuggestionList =
        {
            ("Explain this solution's structure", "Give me a short tour of this solution: the main projects, what each one does, and where the entry points are.", false),
            ("Summarize the open file", "Summarize what this file does and point out anything unusual.", true),
            ("Find bugs in the open file", "Review this file for bugs, edge cases and risky patterns. List the most important issues first, with line references.", true),
            ("Where are the tests?", "Where are the tests in this solution, and how are they organized?", false),
        };

        private readonly DispatcherTimer _renderTimer;
        private string _sessionId;
        private CancellationTokenSource _cts;
        private AssistantMessageView _current;
        private readonly StringBuilder _currentText = new StringBuilder();
        private bool _renderPending;
        private string _attached, _attachedLabel;

        public ChatControl()
        {
            InitializeComponent();

            foreach (var s in SuggestionList)
            {
                var chip = new Button { Style = (Style)FindResource("Claude.ChipButton"), Content = s.Label };
                var suggestion = s;
                chip.Click += (_, __) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (suggestion.WithFile) IncludeFileToggle.IsChecked = true;
                    Send(suggestion.Prompt, suggestion.Label);
                };
                Suggestions.Children.Add(chip);
            }

            // Re-render streamed Markdown at most ~8x per second.
            _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            _renderTimer.Tick += (s, e) => FlushRender();

            // Nested text boxes swallow the mouse wheel; scroll the conversation instead.
            Messages.PreviewMouseWheel += (s, e) =>
            {
                Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta / 2.5);
                e.Handled = true;
            };

            Loaded += (s, e) =>
            {
                ClaudeCli.UsageUpdated -= OnUsageUpdated;
                ClaudeCli.UsageUpdated += OnUsageUpdated;
                if (ClaudeCli.LastUsage != null) ShowUsage(ClaudeCli.LastUsage);
                UpdateSubtitle();
            };
            Unloaded += (s, e) => ClaudeCli.UsageUpdated -= OnUsageUpdated;
        }

        public bool IsBusy => _cts != null;

        /// <summary>Sends a prompt. <paramref name="display"/> is what the bubble shows (the prompt may carry a large code block).</summary>
        public void Send(string prompt, string display = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (IsBusy) { StatusText.Text = "Claude is still answering – stop it first."; return; }
            ThreadHelper.JoinableTaskFactory.RunAsync(() => SendAsync(prompt, display ?? prompt)).FileAndForget("ClaudeForVS/Chat");
        }

        /// <summary>Attaches a code block to the next message the user sends.</summary>
        public void AttachContext(string context, string label)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _attached = context;
            _attachedLabel = label;
            AttachmentText.Text = label;
            AttachmentChip.Visibility = Visibility.Visible;
            FocusInput();
        }

        public void FocusInput()
        {
            Input.Focus();
            Keyboard.Focus(Input);
        }

        private async System.Threading.Tasks.Task SendAsync(string prompt, string display)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var options = ClaudePackage.Options;

            var fullPrompt = new StringBuilder(prompt);
            string attachment = null;
            if (_attached != null)
            {
                fullPrompt.Append("\n\n").Append(_attached);
                attachment = _attachedLabel;
                ClearAttachment();
            }
            else if (IncludeFileToggle.IsChecked == true)
            {
                var sel = VsContext.GetSelection();
                if (sel != null)
                {
                    fullPrompt.Insert(0, $"Currently open file: {sel.FilePath} (lines {sel.StartLine}-{sel.EndLine})\n```\n{sel.Text}\n```\n\n");
                    attachment = $"{Path.GetFileName(sel.FilePath)}:{sel.StartLine}-{sel.EndLine}";
                }
            }

            EmptyState.Visibility = Visibility.Collapsed;
            Messages.Children.Add(new UserMessageView(this, display.Trim(), attachment));
            _current = new AssistantMessageView(this, CreateCodeBlock);
            Messages.Children.Add(_current);
            _currentText.Clear();
            ScrollToEnd(force: true);

            var cwd = VsContext.GetWorkingDirectory();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetBusy(true);
            var view = _current;

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
                },
                delta => OnUi(() =>
                {
                    if (view != _current) return;
                    _currentText.Append(delta);
                    _renderPending = true;
                    if (!_renderTimer.IsEnabled) _renderTimer.Start();
                }),
                token,
                activity => OnUi(() =>
                {
                    view.AddActivity(activity);
                    StatusText.Text = activity + "…";
                    ScrollToEnd();
                }));

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _sessionId = result.SessionId ?? _sessionId;
                _renderTimer.Stop();
                _renderPending = false;

                if (result.IsError) view.ShowNotice(result.Text, isError: true);
                else
                {
                    // Prefer the streamed text: the final result holds only the last turn after tool use.
                    view.SetMarkdown(_currentText.Length > 0 ? _currentText.ToString() : result.Text);
                    view.Complete(result, CopyText);
                }
            }
            catch (OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                FlushRender();
                view.ShowNotice("Stopped.", isError: false);
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                FlushRender();
                view.ShowNotice(ex.Message, isError: true);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _renderTimer.Stop();
                _cts?.Dispose();
                _cts = null;
                SetBusy(false);
                ScrollToEnd();
            }
        }

        private void FlushRender()
        {
            if (!_renderPending || _current == null) { _renderTimer.Stop(); return; }
            _renderPending = false;
            var follow = IsNearBottom();
            _current.SetMarkdown(_currentText.ToString());
            if (follow) ScrollToEnd(force: true);
        }

        private UIElement CreateCodeBlock(string language, string code) =>
            CodeBlockView.Create(this, language, code, CopyText, InsertCode);

        private void CopyText(string text)
        {
            try { Clipboard.SetText(text ?? ""); StatusText.Text = "Copied to clipboard."; }
            catch (Exception ex) { StatusText.Text = "Could not copy: " + ex.Message; }
        }

        private void InsertCode(string code)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StatusText.Text = VsContext.InsertIntoActiveDocument(code)
                ? "Inserted into the active editor."
                : "Open a code editor first to insert code.";
        }

        private void SetBusy(bool busy)
        {
            SendButton.Content = busy ? "" : ""; // stop / send
            SendButton.ToolTip = busy ? "Stop" : "Send (Enter)";
            SendButton.IsEnabled = busy || Input.Text.Trim().Length > 0;
            StatusText.Text = busy ? "Claude is thinking…" : "";
            NewChatButton.IsEnabled = !busy;
        }

        private void OnSendClicked(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (IsBusy) { _cts.Cancel(); return; }
            var text = Input.Text.Trim();
            if (text.Length == 0) return;
            Input.Clear();
            Send(text);
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                if (!IsBusy) OnSendClicked(sender, e);
            }
            else if (e.Key == Key.Escape && IsBusy)
            {
                e.Handled = true;
                _cts.Cancel();
            }
        }

        private void OnInputChanged(object sender, TextChangedEventArgs e)
        {
            Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!IsBusy) SendButton.IsEnabled = Input.Text.Trim().Length > 0;
        }

        private void OnNewChat(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _cts?.Cancel();
            _sessionId = null;
            _current = null;
            Messages.Children.Clear();
            EmptyState.Visibility = Visibility.Visible;
            ClearAttachment();
            FocusInput();
        }

        private void OnRemoveAttachment(object sender, RoutedEventArgs e) => ClearAttachment();

        private void ClearAttachment()
        {
            _attached = _attachedLabel = null;
            AttachmentChip.Visibility = Visibility.Collapsed;
        }

        private void UpdateSubtitle()
        {
            var model = ClaudePackage.Options.ChatModel;
            SubtitleText.Text = string.IsNullOrWhiteSpace(model) ? "Using your Claude subscription" : $"{model.Trim()} · your Claude subscription";
        }

        private void OnUsageUpdated(UsageSnapshot usage) => OnUi(() => ShowUsage(usage));

        private void ShowUsage(UsageSnapshot u)
        {
            UsagePanel.Visibility = Visibility.Visible;
            SetMeter(FiveHourBar, FiveHourLabel, "5h", u.FiveHourUtilization);
            SetMeter(SevenDayBar, SevenDayLabel, "7d", u.SevenDayUtilization);
            UsagePanel.ToolTip = u.ToShortString().Replace("Claude usage: ", "Subscription usage\n").Replace(" · ", "\n");
        }

        private void SetMeter(Border bar, TextBlock label, string name, double? value)
        {
            var v = Math.Max(0, Math.Min(1, value ?? 0));
            bar.Width = 54 * v;
            bar.Background = (Brush)FindResource(v >= 0.85 ? "Claude.Error" : "Claude.Accent");
            label.Text = value.HasValue ? $"{name}  {v:P0}" : $"{name}  –";
        }

        private bool IsNearBottom() => Scroller.ScrollableHeight - Scroller.VerticalOffset < 60;

        private void ScrollToEnd(bool force = false)
        {
            if (!force && !IsNearBottom()) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Scroller.ScrollToEnd()));
        }

        private static void OnUi(Action action)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                action();
            }).FileAndForget("ClaudeForVS/ChatUi");
        }
    }
}
