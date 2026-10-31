using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Theme-aware JSON colorizer. AvalonEdit ships no JSON highlighting
    /// definition, and its C# definition paints nearly the whole document one
    /// hardcoded color, so JSON is colored here instead.
    /// </summary>
    public class JsonSyntaxColorizer : DocumentColorizingTransformer
    {
        // Resolve from Application resources on each use so colors follow theme switches.
        static Brush ResolveBrush(string key, Color fallback)
        {
            return (Application.Current?.TryFindResource(key) as Brush)
                ?? new SolidColorBrush(fallback);
        }

        Brush KeyBrush     => ResolveBrush("EditorVariableBrush", Color.FromRgb(156, 220, 254));
        Brush StringBrush  => ResolveBrush("EditorStringBrush",   Color.FromRgb(206, 145, 120));
        Brush NumberBrush  => ResolveBrush("EditorNumberBrush",   Color.FromRgb(181, 206, 168));
        Brush LiteralBrush => ResolveBrush("EditorKeywordBrush",  Color.FromRgb(86, 156, 214));

        // Strings with escape support, e.g. "a\"b".
        static readonly Regex StringRegex = new Regex(@"""(?:\\.|[^""\\])*""", RegexOptions.Compiled);
        // JSON numbers: optional sign, integer/fraction, optional exponent.
        static readonly Regex NumberRegex = new Regex(@"-?\b\d+(\.\d+)?([eE][+-]?\d+)?", RegexOptions.Compiled);
        // true / false / null as standalone tokens.
        static readonly Regex LiteralRegex = new Regex(@"\b(true|false|null)\b", RegexOptions.Compiled);

        protected override void ColorizeLine(DocumentLine line)
        {
            var text = CurrentContext.Document.GetText(line);

            // Resolve brushes once per line instead of once per token: a minified
            // JSON line can hold thousands of tokens, and every lookup walks the
            // application resource dictionaries.
            var literalBrush = LiteralBrush;
            var numberBrush = NumberBrush;
            var keyBrush = KeyBrush;
            var stringBrush = StringBrush;

            // Literals and numbers first, then strings last so anything inside a
            // quoted string is overpainted with the string color.
            foreach (Match m in LiteralRegex.Matches(text))
            {
                ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, e => e.TextRunProperties.SetForegroundBrush(literalBrush));
            }

            foreach (Match m in NumberRegex.Matches(text))
            {
                ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, e => e.TextRunProperties.SetForegroundBrush(numberBrush));
            }

            foreach (Match m in StringRegex.Matches(text))
            {
                int end = m.Index + m.Length;

                // A string followed (ignoring inline whitespace) by ':' is a key.
                int probe = end;
                while (probe < text.Length && (text[probe] == ' ' || text[probe] == '\t')) probe++;
                var brush = probe < text.Length && text[probe] == ':' ? keyBrush : stringBrush;

                ChangeLinePart(line.Offset + m.Index, line.Offset + end, e => e.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }
}