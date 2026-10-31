using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace TIAOpennessStudio.Services
{
    public sealed class PlcKnowledgeCanonicalizer
    {
        public string CanonicalizeBlockName(string blockName)
        {
            return NormalizeWhitespace(blockName);
        }

        public string CanonicalizeSymbolPath(string symbol)
        {
            var text = NormalizeWhitespace(symbol)
                .Replace("\\", ".")
                .Replace("/", ".");

            while (text.Contains(".."))
            {
                text = text.Replace("..", ".");
            }

            text = text.Trim('.');
            return TiaXmlParseHelper.NormalizeSymbol(text);
        }

        public string BuildScopedPath(string ownerName, string scope, string symbolPath)
        {
            var owner = CanonicalizeBlockName(ownerName);
            var path = CanonicalizeSymbolPath(symbolPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                return owner;
            }

            if (!string.IsNullOrWhiteSpace(owner) &&
                path.StartsWith(owner + ".", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            var scopePrefix = NormalizeScope(scope);
            if (string.IsNullOrWhiteSpace(owner))
            {
                return string.IsNullOrWhiteSpace(scopePrefix) ? path : scopePrefix + "." + path;
            }

            if (string.IsNullOrWhiteSpace(scopePrefix))
            {
                return owner + "." + path;
            }

            return owner + "." + scopePrefix + "." + path;
        }

        public string NormalizeScope(string scope)
        {
            var value = NormalizeWhitespace(scope).ToLowerInvariant();
            switch (value)
            {
                case "input":
                case "var_input":
                case "in":
                    return "In";
                case "output":
                case "var_output":
                case "out":
                    return "Out";
                case "inout":
                case "in_out":
                case "var_in_out":
                    return "InOut";
                case "static":
                case "stat":
                case "var":
                case "var_stat":
                    return "Static";
                case "temp":
                case "var_temp":
                    return "Temp";
                case "constant":
                case "const":
                case "typedconstant":
                    return "Constant";
                case "globalvariable":
                    return "Global";
                case "localvariable":
                    return "Local";
                default:
                    return NormalizeWhitespace(scope);
            }
        }

        public bool LooksLikeSymbol(string value)
        {
            var text = CanonicalizeSymbolPath(value);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (text.StartsWith("%", StringComparison.Ordinal))
            {
                return true;
            }

            return text.Any(char.IsLetter) || text.Contains(".") || text.Contains("[");
        }

        public string NormalizeWhitespace(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return Regex.Replace(value.Trim(), @"\s+", " ");
        }
    }
}
