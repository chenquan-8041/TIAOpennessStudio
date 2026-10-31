using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TIAOpennessStudio.Controls
{
    /// <summary>
    /// Read-only, selectable text control that renders a lightweight Markdown subset:
    /// headings (#..###), bullets ("-", "*", "•"), **bold**, `inline code` and
    /// [text](url) links (shown as their text only).
    /// Deliberately not a full Markdown engine — no tables, images or navigation.
    /// </summary>
    public class MarkdownRichText : RichTextBox
    {
        public static readonly DependencyProperty MarkdownProperty =
            DependencyProperty.Register(
                nameof(Markdown),
                typeof(string),
                typeof(MarkdownRichText),
                new PropertyMetadata(null, OnMarkdownChanged));

        private static readonly FontFamily CodeFont =
            new FontFamily("Cascadia Code, Consolas, Microsoft YaHei UI");

        // **bold** | `code` | [text](url). Non-greedy so adjacent spans do not merge.
        private static readonly Regex InlinePattern = new Regex(
            @"\*\*(?<bold>.+?)\*\*|`(?<code>[^`\r\n]+)`|\[(?<link>[^\]\r\n]+)\]\((?<url>[^)\r\n]+)\)",
            RegexOptions.Compiled);

        public MarkdownRichText()
        {
            IsReadOnly = true;
            IsTabStop = false;
            IsUndoEnabled = false;
            BorderThickness = new Thickness(0);
            Padding = new Thickness(0);
            Background = Brushes.Transparent;
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Document = CreateEmptyDocument();
            SetResourceReference(SelectionBrushProperty, "EditorSelectionBrush");
        }

        public string Markdown
        {
            get => (string)GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((MarkdownRichText)d).RenderMarkdown(e.NewValue as string);
        }

        private static FlowDocument CreateEmptyDocument()
        {
            return new FlowDocument
            {
                PagePadding = new Thickness(0)
            };
        }

        private void RenderMarkdown(string markdown)
        {
            var document = Document ?? CreateEmptyDocument();
            document.Blocks.Clear();

            // A FlowDocument built from a converter/controls sits outside the theme's
            // DynamicResource chain, so resolve the body color explicitly.
            document.SetResourceReference(TextElement.ForegroundProperty, "AgentTextPrimaryBrush");

            if (string.IsNullOrEmpty(markdown)) return;

            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var rawLine in lines)
            {
                var line = rawLine ?? string.Empty;

                int level = HeadingLevel(line, out var headingText);
                if (level > 0)
                {
                    var heading = CreateParagraph(FontWeights.SemiBold, FontSize + HeadingOffset(level));
                    AppendInlineRuns(heading, headingText);
                    document.Blocks.Add(heading);
                    continue;
                }

                if (TryStripBullet(line, out var bulletText))
                {
                    var item = CreateParagraph(FontWeights.Normal, 0);
                    var dot = new Run("•  ") { FontWeight = FontWeights.SemiBold };
                    dot.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                    item.Inlines.Add(dot);
                    AppendInlineRuns(item, bulletText);
                    document.Blocks.Add(item);
                    continue;
                }

                var paragraph = CreateParagraph(FontWeights.Normal, 0);
                AppendInlineRuns(paragraph, line);
                document.Blocks.Add(paragraph);
            }
        }

        private static Paragraph CreateParagraph(FontWeight weight, double fontSize)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                FontWeight = weight
            };
            if (fontSize > 0) paragraph.FontSize = fontSize;
            return paragraph;
        }

        private static double HeadingOffset(int level)
        {
            switch (level)
            {
                case 1: return 4;
                case 2: return 2;
                case 3: return 1;
                default: return 0;
            }
        }

        /// <summary>Returns 1..6 for an ATX heading line, 0 otherwise.</summary>
        private static int HeadingLevel(string line, out string text)
        {
            text = null;
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;

            int hashes = 0;
            while (i + hashes < line.Length && line[i + hashes] == '#') hashes++;
            if (hashes == 0 || hashes > 6) return 0;
            if (i + hashes >= line.Length || line[i + hashes] != ' ') return 0;

            text = line.Substring(i + hashes).Trim();
            return string.IsNullOrEmpty(text) ? 0 : hashes;
        }

        private static bool TryStripBullet(string line, out string text)
        {
            text = null;
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;

            if (i + 1 < line.Length && (line[i] == '-' || line[i] == '*' || line[i] == '•') && line[i + 1] == ' ')
            {
                text = line.Substring(i + 2).TrimEnd();
                return true;
            }
            return false;
        }

        private static void AppendInlineRuns(Paragraph paragraph, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            int last = 0;
            foreach (Match match in InlinePattern.Matches(text))
            {
                if (match.Index > last)
                {
                    paragraph.Inlines.Add(new Run(text.Substring(last, match.Index - last)));
                }

                if (match.Groups["bold"].Success)
                {
                    paragraph.Inlines.Add(new Bold(new Run(match.Groups["bold"].Value)));
                }
                else if (match.Groups["code"].Success)
                {
                    // Monospace only; size and weight are inherited from the document.
                    var run = new Run(match.Groups["code"].Value) { FontFamily = CodeFont };
                    run.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                    paragraph.Inlines.Add(run);
                }
                else
                {
                    // Links render as their display text; click-through is not supported.
                    paragraph.Inlines.Add(new Run(match.Groups["link"].Value));
                }

                last = match.Index + match.Length;
            }

            if (last < text.Length)
            {
                paragraph.Inlines.Add(new Run(text.Substring(last)));
            }
        }
    }
}
