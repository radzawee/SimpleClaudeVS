using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClaudeForVS.Services;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.UI
{
    /// <summary>Right-aligned bubble with the user's message.</summary>
    internal sealed class UserMessageView : Border
    {
        public UserMessageView(FrameworkElement owner, string text, string attachment)
        {
            HorizontalAlignment = HorizontalAlignment.Right;
            Margin = new Thickness(48, 6, 0, 10);
            Padding = new Thickness(12, 8, 12, 9);
            CornerRadius = new CornerRadius(12, 12, 3, 12);
            Background = (Brush)owner.FindResource("Claude.AccentSoft");

            var stack = new StackPanel();
            if (!string.IsNullOrEmpty(attachment))
            {
                var chip = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 4), Opacity = 0.8 };
                chip.Inlines.Add(new Run("  ") { FontFamily = (FontFamily)owner.FindResource("Claude.IconFont"), FontSize = 10 });
                chip.Inlines.Add(new Run(attachment));
                stack.Children.Add(chip);
            }

            var body = new TextBox { Text = text, IsReadOnly = true, Style = (Style)owner.FindResource("Claude.BareTextBox") };
            stack.Children.Add(body);
            Child = stack;
        }
    }

    /// <summary>Claude's answer: avatar header, tool-activity chips, rendered Markdown, and a footer with actions.</summary>
    internal sealed class AssistantMessageView : StackPanel
    {
        private readonly FrameworkElement _owner;
        private readonly WrapPanel _activity;
        private readonly FrameworkElement _typing;
        private readonly RichTextBox _body;
        private readonly FlowDocument _document;
        private readonly StackPanel _footer;
        private readonly MarkdownRenderer _renderer;
        private string _markdown = "";

        public AssistantMessageView(FrameworkElement owner, Func<string, string, UIElement> codeBlockFactory)
        {
            _owner = owner;
            Margin = new Thickness(0, 6, 0, 14);

            // Header: avatar + name
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var avatar = new Grid { Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0) };
            avatar.Children.Add(new System.Windows.Shapes.Ellipse { Fill = Brush("Claude.Accent") });
            avatar.Children.Add(new System.Windows.Shapes.Path
            {
                Data = (Geometry)owner.FindResource("Claude.Burst"),
                Stroke = Brushes.White,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 10,
                Height = 10,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            header.Children.Add(avatar);
            header.Children.Add(new TextBlock { Text = "Claude", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            Children.Add(header);

            var content = new StackPanel { Margin = new Thickness(28, 0, 0, 0) };

            _activity = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
            content.Children.Add(_activity);

            _typing = CreateTypingIndicator();
            content.Children.Add(_typing);

            _document = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left, LineHeight = double.NaN };
            _document.SetResourceReference(FlowDocument.FontFamilyProperty, VsFonts.EnvironmentFontFamilyKey);
            _document.SetResourceReference(FlowDocument.FontSizeProperty, VsFonts.EnvironmentFontSizeKey);
            _document.SetResourceReference(FlowDocument.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            _body = new RichTextBox
            {
                Document = _document,
                IsReadOnly = true,
                IsDocumentEnabled = true, // lets the Copy / Insert buttons inside code blocks receive clicks
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(-5, 0, 0, 0), // RichTextBox has a built-in 5px page inset
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                SelectionBrush = Brush("Claude.Accent"),
                Visibility = Visibility.Collapsed,
            };
            _body.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            content.Children.Add(_body);

            _footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-7, 2, 0, 0), Visibility = Visibility.Collapsed };
            content.Children.Add(_footer);

            Children.Add(content);
            _renderer = new MarkdownRenderer(owner, codeBlockFactory);
        }

        public string Markdown => _markdown;

        public void AddActivity(string text)
        {
            _activity.Visibility = Visibility.Visible;
            var chip = new Border
            {
                Background = Brush("Claude.Overlay"),
                BorderBrush = Brush("Claude.Hairline"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 8, 3),
                Margin = new Thickness(0, 0, 6, 4),
            };
            var t = new TextBlock { FontSize = 11 };
            t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            t.Inlines.Add(new Run(IconFor(text) + "  ") { FontFamily = (FontFamily)_owner.FindResource("Claude.IconFont"), FontSize = 10 });
            t.Inlines.Add(new Run(text));
            chip.Child = t;
            _activity.Children.Add(chip);
        }

        public void SetMarkdown(string markdown)
        {
            _markdown = markdown ?? "";
            if (_markdown.Length == 0) return;
            _typing.Visibility = Visibility.Collapsed;
            _body.Visibility = Visibility.Visible;
            _renderer.RenderInto(_document, _markdown);
        }

        public void Complete(ClaudeResult result, Action<string> copy)
        {
            HideTyping();
            if (_markdown.Length == 0) SetMarkdown("_(no text response)_");

            _footer.Children.Add(FooterButton("", "Copy", () => copy(_markdown)));
            var meta = new System.Collections.Generic.List<string>();
            if (result?.DurationMs != null) meta.Add($"{result.DurationMs.Value / 1000.0:0.0}s");
            if (meta.Count > 0)
            {
                var info = new TextBlock { Text = string.Join(" · ", meta), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                info.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
                _footer.Children.Add(info);
            }
            _footer.Visibility = Visibility.Visible;
        }

        public void ShowNotice(string text, bool isError)
        {
            HideTyping();
            var box = new Border
            {
                Background = Brush(isError ? "Claude.ErrorSoft" : "Claude.Overlay"),
                BorderBrush = Brush(isError ? "Claude.Error" : "Claude.Hairline"),
                BorderThickness = new Thickness(1, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 4, 0, 0),
            };
            var tb = new TextBox { Text = text, IsReadOnly = true, Style = (Style)_owner.FindResource("Claude.BareTextBox") };
            box.Child = tb;
            ((Panel)Children[1]).Children.Add(box);
        }

        private void HideTyping() => _typing.Visibility = Visibility.Collapsed;

        private Button FooterButton(string icon, string label, Action onClick)
        {
            var tb = new TextBlock();
            tb.Inlines.Add(new Run(icon + "  ") { FontFamily = (FontFamily)_owner.FindResource("Claude.IconFont"), FontSize = 10 });
            tb.Inlines.Add(new Run(label));
            var b = new Button { Content = tb, Style = (Style)_owner.FindResource("Claude.GhostButton") };
            b.Click += (s, e) => onClick();
            return b;
        }

        private FrameworkElement CreateTypingIndicator()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4), Height = 12 };
            for (var i = 0; i < 3; i++)
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 5, 0), Fill = Brush("Claude.Accent"), Opacity = 0.25 };
                var anim = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(450))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 150),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                dot.BeginAnimation(OpacityProperty, anim);
                panel.Children.Add(dot);
            }
            return panel;
        }

        private static string IconFor(string activity)
        {
            if (activity.StartsWith("Read")) return "";      // document
            if (activity.StartsWith("Grep") || activity.StartsWith("Glob")) return ""; // search
            if (activity.StartsWith("Edit") || activity.StartsWith("Write")) return ""; // edit
            if (activity.StartsWith("Bash") || activity.StartsWith("PowerShell")) return ""; // terminal
            if (activity.StartsWith("Web")) return ""; // globe
            return ""; // wrench
        }

        private Brush Brush(string key) => (Brush)_owner.FindResource(key);
    }

    /// <summary>Code block card with a language label and Copy / Insert actions.</summary>
    internal static class CodeBlockView
    {
        public static UIElement Create(FrameworkElement owner, string language, string code, Action<string> copy, Action<string> insert)
        {
            Brush B(string key) => (Brush)owner.FindResource(key);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderBrush = B("Claude.Hairline"),
                BorderThickness = new Thickness(1),
                Background = B("Claude.OverlayStrong"),
                ClipToBounds = true,
            };

            var dock = new DockPanel();

            var header = new Border { Background = B("Claude.Overlay"), BorderBrush = B("Claude.Hairline"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 2, 3, 2) };
            var headerDock = new DockPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Action(owner, "", "Copy", () => copy(code)));
            actions.Children.Add(Action(owner, "", "Insert", () => insert(code)));
            DockPanel.SetDock(actions, Dock.Right);
            headerDock.Children.Add(actions);
            var lang = new TextBlock { Text = string.IsNullOrEmpty(language) ? "code" : language, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            lang.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            headerDock.Children.Add(lang);
            header.Child = headerDock;
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            var body = new TextBox
            {
                Text = code,
                IsReadOnly = true,
                Style = (Style)owner.FindResource("Claude.BareTextBox"),
                FontFamily = (FontFamily)owner.FindResource("Claude.MonoFont"),
                FontSize = Math.Max(10, TextElement.GetFontSize(owner)),
                Margin = new Thickness(12, 9, 12, 10),
                TextWrapping = TextWrapping.Wrap,
            };
            dock.Children.Add(body);

            card.Child = dock;
            return card;
        }

        private static Button Action(FrameworkElement owner, string icon, string label, Action onClick)
        {
            var tb = new TextBlock();
            tb.Inlines.Add(new Run(icon + "  ") { FontFamily = (FontFamily)owner.FindResource("Claude.IconFont"), FontSize = 10 });
            var text = new Run(label);
            tb.Inlines.Add(text);
            var b = new Button { Content = tb, Style = (Style)owner.FindResource("Claude.GhostButton") };
            b.Click += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                onClick();
                // Brief confirmation.
                var original = text.Text;
                text.Text = original == "Copy" ? "Copied" : "Inserted";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
                timer.Tick += (_, __) => { timer.Stop(); text.Text = original; };
                timer.Start();
            };
            return b;
        }
    }
}
