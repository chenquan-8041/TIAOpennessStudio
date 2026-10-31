using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;

namespace TIAOpennessStudio.Services
{
    public class BraceFoldingStrategy
    {
        static readonly Regex SclStartPattern = new Regex(@"\b(FUNCTION_BLOCK|FUNCTION|STRUCT|TYPE|VAR_INPUT|VAR_OUTPUT|VAR_IN_OUT|VAR_TEMP|VAR|IF|WHILE|FOR|CASE|REGION)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SclEndPattern = new Regex(@"\b(END_FUNCTION_BLOCK|END_FUNCTION|END_STRUCT|END_TYPE|END_VAR|END_IF|END_WHILE|END_FOR|END_CASE|END_REGION)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly bool _foldSquareBrackets;

        /// <param name="foldSquareBrackets">
        /// Enables folding on "[" / "]" as well as "{" / "}". Intended for JSON,
        /// where arrays should be foldable like objects. Must stay off for SCL,
        /// where "[]" is an array index expression rather than a block.
        /// </param>
        public BraceFoldingStrategy(bool foldSquareBrackets = false)
        {
            _foldSquareBrackets = foldSquareBrackets;
        }

        public void UpdateFoldings(FoldingManager manager, TextDocument document)
        {
            int firstErrorOffset;
            IEnumerable<NewFolding> newFoldings = CreateNewFoldings(document, out firstErrorOffset);
            manager.UpdateFoldings(newFoldings, firstErrorOffset);
        }

        public IEnumerable<NewFolding> CreateNewFoldings(TextDocument document, out int firstErrorOffset)
        {
            firstErrorOffset = -1;
            List<NewFolding> newFoldings = new List<NewFolding>();

            Stack<int> startOffsets = new Stack<int>();
            Stack<int> sclStartOffsets = new Stack<int>();

            for (int i = 0; i < document.TextLength; i++)
            {
                char c = document.GetCharAt(i);
                if (c == '{' || (_foldSquareBrackets && c == '['))
                {
                    startOffsets.Push(i);
                }
                else if ((c == '}' || (_foldSquareBrackets && c == ']')) && startOffsets.Count > 0)
                {
                    int start = startOffsets.Pop();
                    if (i > start + 1) // Don't fold empty or single-char blocks
                    {
                        newFoldings.Add(new NewFolding(start, i + 1));
                    }
                }
            }

            foreach (var line in document.Lines)
            {
                var text = document.GetText(line);
                var startMatch = SclStartPattern.Match(text);
                var endMatch = SclEndPattern.Match(text);

                if (startMatch.Success)
                {
                    sclStartOffsets.Push(line.Offset);
                }

                if (endMatch.Success && sclStartOffsets.Count > 0)
                {
                    int start = sclStartOffsets.Pop();
                    int end = line.EndOffset;
                    if (end > start + 1)
                    {
                        newFoldings.Add(new NewFolding(start, end));
                    }
                }
            }

            return newFoldings.OrderBy(f => f.StartOffset);
        }
    }
}
