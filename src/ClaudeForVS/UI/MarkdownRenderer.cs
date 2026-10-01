using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace ClaudeForVS.UI
{
    /// <summary>
    /// Renders the Markdown subset Claude uses (headings, paragraphs, lists, quotes, tables, rules,
    /// fenced code, inline code, bold, italic, links) into a WPF FlowDocument. Tolerates an unterminated
    /// code fence so a response can be re-rendered while it is still streaming.
    /// </summary>
    internal sealed class MarkdownRenderer
    {
        private static readonly Regex Heading = new Regex(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
        private static readonly Regex Rule = new Regex(@"^\s*([-*_])(\s*\1){2,}\s*$");
        private static readonly Regex ListItem = new Regex(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$");
        private static readonly Regex TableSeparator = new Regex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");
        private static readonly Regex InlineToken = new Regex(
            @"(?<code>`+)(?<codeText>.+?)\k<code>" +
            @"|\*\*(?<bold>.+?)\*\*|__(?<bold2>.+?)__" +
            @"|(?<![\w*])\*(?<italic>[^*\s](?:[^*]*[^*\s])?)\*(?!\w)" +
            @"|(?<![\w_])_(?<italic2>[^_\s](?:[^_]*[^_\s])?)_(?!\w)" +
            @"|\[(?<linkText>[^\]]+)\]\((?<linkUrl>[^)\s]+)\)",
            RegexOptions.Compiled);

        private readonly FrameworkElement _owner;
        private readonly Func<string, string, UIElement> _codeBlockFactory;

        /// <param name="owner">Element whose resources (ChatStyles.xaml) supply brushes.</param>
        /// <param name="codeBlockFactory">Builds the UI for a fenced code block (language, code).</param>
        public MarkdownRenderer(FrameworkElement owner, Func<string, string, UIElement> codeBlockFactory)
        {
            _owner = owner;
            _codeBlockFactory = codeBlockFactory;
        }

        public void RenderInto(FlowDocument document, string markdown)
        {
            document.Blocks.Clear();
            foreach (var block in ParseBlocks((markdown ?? "").Replace("\r\n", "\n").Split('\n')))
                document.Blocks.Add(block);
            if (document.Blocks.FirstBlock != null) document.Blocks.FirstBlock.Margin = new Thickness(0, 0, 0, document.Blocks.FirstBlock.Margin.Bottom);
            if (document.Blocks.LastBlock != null) document.Blocks.LastBlock.Margin = new Thickness(0, document.Blocks.LastBlock.Margin.Top, 0, 0);
        }

        private IEnumerable<Block> ParseBlocks(IList<string> lines)
        {
            var i = 0;
            while (i < lines.Count)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                if (trimmed.Length == 0) { i++; continue; }

                // Fenced code
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    var fence = trimmed.Substring(0, 3);
                    var lang = trimmed.Substring(3).Trim();
                    var code = new List<string>();
                    i++;
                    while (i < lines.Count && !lines[i].TrimStart().StartsWith(fence)) code.Add(lines[i++]);
                    i++;
                    yield return new BlockUIContainer(_codeBlockFactory(lang, string.Join("\n", code))) { Margin = new Thickness(0, 4, 0, 10) };
                    continue;
                }

                var heading = Heading.Match(trimmed);
                if (heading.Success)
                {
                    var level = heading.Groups[1].Length;
                    var p = new Paragraph { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, level <= 2 ? 12 : 8, 0, 4) };
                    p.FontSize = level == 1 ? 18 : level == 2 ? 15.5 : 13.5;
                    AddInlines(p.Inlines, heading.Groups[2].Value);
                    yield return p;
                    i++;
                    continue;
                }

                if (Rule.IsMatch(line))
                {
                    yield return new Paragraph { Margin = new Thickness(0, 6, 0, 10), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Brush("Claude.Hairline"), FontSize = 2 };
                    i++;
                    continue;
                }

                // Table: header row followed by a |---|---| separator
                if (line.Contains("|") && i + 1 < lines.Count && TableSeparator.IsMatch(lines[i + 1]))
                {
                    var rows = new List<string> { line };
                    i += 2;
                    while (i < lines.Count && lines[i].Contains("|") && lines[i].Trim().Length > 0) rows.Add(lines[i++]);
                    yield return BuildTable(rows);
                    continue;
                }

                if (trimmed.StartsWith(">"))
                {
                    var quoted = new List<string>();
                    while (i < lines.Count && lines[i].TrimStart().StartsWith(">"))
                    {
                        var q = lines[i++].TrimStart().Substring(1);
                        quoted.Add(q.StartsWith(" ") ? q.Substring(1) : q);
                    }
                    var section = new Section
                    {
                        BorderBrush = Brush("Claude.Accent"),
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(10, 2, 0, 2),
                        Margin = new Thickness(0, 2, 0, 10),
                    };
                    section.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
                    foreach (var b in ParseBlocks(quoted)) section.Blocks.Add(b);
                    yield return section;
                    continue;
                }

                if (ListItem.IsMatch(line))
                {
                    while (i < lines.Count)
                    {
                        var m = ListItem.Match(lines[i]);
                        if (!m.Success)
                        {
                            // Lazy continuation of the previous item's text.
                            if (lines[i].Trim().Length > 0 && lines[i].StartsWith("  ") && !lines[i].TrimStart().StartsWith("```")) { i++; continue; }
                            break;
                        }
                        var depth = m.Groups[1].Value.Replace("\t", "    ").Length / 2;
                        var marker = m.Groups[2].Value;
                        var bullet = char.IsDigit(marker[0]) ? marker.TrimEnd(')', '.') + "." : depth == 0 ? "•" : "◦";

                        var text = m.Groups[3].Value;
                        i++;
                        while (i < lines.Count && lines[i].Trim().Length > 0 && !ListItem.IsMatch(lines[i]) && lines[i].StartsWith("  ") && !lines[i].TrimStart().StartsWith("```"))
                            text += " " + lines[i++].Trim();

                        var left = 18 + depth * 18;
                        var p = new Paragraph { Margin = new Thickness(left, 1, 0, 3), TextIndent = -14 };
                        var bulletRun = new Run(bullet + " ");
                        if (bullet.Length > 1) { p.TextIndent = -18; }
                        bulletRun.Foreground = Brush("Claude.Accent");
                        p.Inlines.Add(bulletRun);
                        AddInlines(p.Inlines, text);
                        yield return p;
                    }
                    // Keep space after the list.
                    yield return new Paragraph { Margin = new Thickness(0, 0, 0, 4), FontSize = 1 };
                    continue;
                }

                // Paragraph: consecutive plain lines.
                var para = new List<string>();
                while (i < lines.Count)
                {
                    var l = lines[i];
                    var t = l.TrimStart();
                    if (t.Length == 0 || t.StartsWith("```") || t.StartsWith("~~~") || t.StartsWith(">") || Heading.IsMatch(t) || ListItem.IsMatch(l) || Rule.IsMatch(l)) break;
                    if (l.Contains("|") && i + 1 < lines.Count && TableSeparator.IsMatch(lines[i + 1])) break;
                    para.Add(l.Trim());
                    i++;
                }
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                for (var k = 0; k < para.Count; k++)
                {
                    if (k > 0) paragraph.Inlines.Add(new LineBreak());
                    AddInlines(paragraph.Inlines, para[k]);
                }
                yield return paragraph;
            }
        }

        private Table BuildTable(List<string> rows)
        {
            List<string> Cells(string row)
            {
                var r = row.Trim();
                if (r.StartsWith("|")) r = r.Substring(1);
                if (r.EndsWith("|")) r = r.Substring(0, r.Length - 1);
                return r.Split('|').Select(c => c.Trim()).ToList();
            }

            var parsed = rows.Select(Cells).ToList();
            var columns = parsed.Max(r => r.Count);
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 10), BorderBrush = Brush("Claude.Hairline"), BorderThickness = new Thickness(1, 1, 0, 0) };
            for (var c = 0; c < columns; c++) table.Columns.Add(new TableColumn());

            var group = new TableRowGroup();
            for (var r = 0; r < parsed.Count; r++)
            {
                var row = new TableRow();
                if (r == 0) { row.FontWeight = FontWeights.SemiBold; row.Background = Brush("Claude.Overlay"); }
                for (var c = 0; c < columns; c++)
                {
                    var p = new Paragraph { Margin = new Thickness(0) };
                    AddInlines(p.Inlines, c < parsed[r].Count ? parsed[r][c] : "");
                    row.Cells.Add(new TableCell(p) { Padding = new Thickness(8, 4, 8, 4), BorderBrush = Brush("Claude.Hairline"), BorderThickness = new Thickness(0, 0, 1, 1) });
                }
                group.Rows.Add(row);
            }
            table.RowGroups.Add(group);
            return table;
        }

        private void AddInlines(InlineCollection target, string text)
        {
            var pos = 0;
            foreach (Match m in InlineToken.Matches(text))
            {
                if (m.Index > pos) target.Add(new Run(text.Substring(pos, m.Index - pos)));
                pos = m.Index + m.Length;

                if (m.Groups["codeText"].Success)
                {
                    var run = new Run(m.Groups["codeText"].Value.Trim()) { FontFamily = (FontFamily)_owner.FindResource("Claude.MonoFont"), Background = Brush("Claude.OverlayHover") };
                    run.FontSize = Math.Max(10, TextElement.GetFontSize(_owner) - 0.5);
                    target.Add(run);
                }
                else if (m.Groups["bold"].Success || m.Groups["bold2"].Success)
                {
                    var bold = new Bold();
                    AddInlines(bold.Inlines, m.Groups["bold"].Success ? m.Groups["bold"].Value : m.Groups["bold2"].Value);
                    target.Add(bold);
                }
                else if (m.Groups["italic"].Success || m.Groups["italic2"].Success)
                {
                    var italic = new Italic();
                    AddInlines(italic.Inlines, m.Groups["italic"].Success ? m.Groups["italic"].Value : m.Groups["italic2"].Value);
                    target.Add(italic);
                }
                else if (m.Groups["linkText"].Success)
                {
                    var url = m.Groups["linkUrl"].Value;
                    var link = new Hyperlink(new Run(m.Groups["linkText"].Value)) { Foreground = Brush("Claude.Accent"), ToolTip = url, Cursor = System.Windows.Input.Cursors.Hand };
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        link.Click += (s, e) => { try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { } };
                    target.Add(link);
                }
            }
            if (pos < text.Length) target.Add(new Run(text.Substring(pos)));
        }

        private Brush Brush(string key) => (Brush)_owner.FindResource(key);
    }
}
