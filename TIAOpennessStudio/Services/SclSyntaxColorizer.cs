using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace TIAOpennessStudio.Services
{
    public class SclSyntaxColorizer : DocumentColorizingTransformer
    {
        // Resolve from Application resources on each use so colors follow theme switches.
        static Brush ResolveBrush(string key, Color fallback)
        {
            return (Application.Current?.TryFindResource(key) as Brush)
                ?? new SolidColorBrush(fallback);
        }

        Brush KeywordBrush  => ResolveBrush("EditorKeywordBrush",  Color.FromRgb(86, 156, 214));
        Brush TypeBrush     => ResolveBrush("EditorTypeBrush",     Color.FromRgb(78, 201, 176));
        Brush NumberBrush   => ResolveBrush("EditorNumberBrush",   Color.FromRgb(181, 206, 168));
        Brush StringBrush   => ResolveBrush("EditorStringBrush",   Color.FromRgb(206, 145, 120));
        Brush CommentBrush  => ResolveBrush("EditorCommentBrush",  Color.FromRgb(106, 153, 85));
        Brush VariableBrush => ResolveBrush("EditorVariableBrush", Color.FromRgb(156, 220, 254));

        bool inBlockComment;
        bool inVarBlock;

        static readonly Regex WordRegex = new Regex(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled);
        static readonly Regex NumberRegex = new Regex(@"\b\d+(\.\d+)?\b", RegexOptions.Compiled);
        static readonly Regex StringRegex = new Regex(@"""([^""]|"""")*""", RegexOptions.Compiled);
        static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FUNCTION","FUNCTION_BLOCK","END_FUNCTION","END_FUNCTION_BLOCK","RETURN",
            "VAR","VAR_INPUT","VAR_OUTPUT","VAR_IN_OUT","VAR_TEMP","END_VAR",
            "IF","THEN","ELSE","ELSIF","END_IF",
            "WHILE","DO","END_WHILE","FOR","TO","DOWNTO","END_FOR",
            "CASE","OF","END_CASE",
            "STRUCT","END_STRUCT","TYPE","END_TYPE",
            "REGION","END_REGION"
        };
        static readonly HashSet<string> Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BOOL","BYTE","WORD","DWORD","LWORD","CHAR","STRING",
            "INT","DINT","LINT","SINT","USINT","UINT","UDINT","ULINT",
            "REAL","LREAL",
            "TIME","DATE","DT","TOD"
        };

        protected override void ColorizeLine(DocumentLine line)
        {
            var text = CurrentContext.Document.GetText(line);

            if (!inBlockComment)
            {
                int idx = text.IndexOf("(*", StringComparison.Ordinal);
                int end = text.IndexOf("*)", StringComparison.Ordinal);
                if (idx >= 0 && (end < 0 || end < idx))
                {
                    inBlockComment = true;
                    ChangeLinePart(line.Offset + idx, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(CommentBrush));
                    return;
                }
            }
            else
            {
                int end = text.IndexOf("*)", StringComparison.Ordinal);
                if (end >= 0)
                {
                    ChangeLinePart(line.Offset, line.Offset + end + 2, e => e.TextRunProperties.SetForegroundBrush(CommentBrush));
                    inBlockComment = false;
                }
                else
                {
                    ChangeLinePart(line.Offset, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(CommentBrush));
                    return;
                }
            }

            int slComment = text.IndexOf("//", StringComparison.Ordinal);
            if (slComment >= 0)
            {
                ChangeLinePart(line.Offset + slComment, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(CommentBrush));
                text = text.Substring(0, slComment);
            }

            if (Regex.IsMatch(text, @"\bVAR(_INPUT|_OUTPUT|_IN_OUT|_TEMP)?\b", RegexOptions.IgnoreCase))
            {
                inVarBlock = true;
            }
            if (Regex.IsMatch(text, @"\bEND_VAR\b", RegexOptions.IgnoreCase))
            {
                inVarBlock = false;
            }

            foreach (Match m in StringRegex.Matches(text))
            {
                ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, e => e.TextRunProperties.SetForegroundBrush(StringBrush));
            }

            foreach (Match m in NumberRegex.Matches(text))
            {
                ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, e => e.TextRunProperties.SetForegroundBrush(NumberBrush));
            }

            foreach (Match m in WordRegex.Matches(text))
            {
                var token = m.Value;
                var start = line.Offset + m.Index;
                var end = start + m.Length;

                if (Keywords.Contains(token))
                {
                    ChangeLinePart(start, end, e => e.TextRunProperties.SetForegroundBrush(KeywordBrush));
                    continue;
                }

                if (Types.Contains(token))
                {
                    ChangeLinePart(start, end, e => e.TextRunProperties.SetForegroundBrush(TypeBrush));
                    continue;
                }

                if (inVarBlock)
                {
                    int colon = text.IndexOf(':');
                    if (colon > 0)
                    {
                        var decl = text.Substring(0, colon);
                        if (Regex.IsMatch(decl, @"\b" + Regex.Escape(token) + @"\b"))
                        {
                            ChangeLinePart(start, end, e => e.TextRunProperties.SetForegroundBrush(VariableBrush));
                        }
                    }
                }
            }
        }
    }
}
