using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// Composes a valid organization block (OB) XML.
    ///
    /// An OB is the one block type an SCL external source cannot create correctly: TIA rejects it with
    /// "The block has an invalid number" and "The interface of the standard OB is smaller than the
    /// minimum value of 20 bytes", because an OB needs a legal number, a SecondaryType and the
    /// system start-information interface (exactly 20 bytes for the classic family). Those come from
    /// real V15.1 exports (OB1 program cycle / OB32 cyclic interrupt), so the composition mirrors them.
    ///
    /// The body is transplanted as an already-tokenized NetworkSource: the SCL XSD only accepts
    /// structured Access/Token/Comment children, never plain text, so the caller tokenizes the body
    /// by round-tripping it through a temporary FC and passing the exported element here.
    /// </summary>
    public static class PlcObXmlBuilder
    {
        private static XNamespace InterfaceNs => PlcBlockXmlHelpers.InterfaceNs;

        /// <summary>OB number → SecondaryType, or null when the number is not a supported OB.</summary>
        public static string SecondaryTypeFor(int number)
        {
            if (number == 1) return "ProgramCycle";
            if (number >= 30 && number <= 38) return "CyclicInterrupt";
            if (number >= 100 && number <= 102) return "Startup";
            return null;
        }

        /// <summary>
        /// Start-information member suffixes per OB type, verified against real V15.1 exports:
        /// 6 x Byte + 3 x Int + 1 x Date_And_Time = 20 bytes.
        /// </summary>
        public static (string Suffix, string Datatype)[] StartInfoMembers(string secondaryType)
        {
            switch (secondaryType)
            {
                case "ProgramCycle":
                    return new[]
                    {
                        ("EV_CLASS", "Byte"), ("SCAN_1", "Byte"), ("PRIORITY", "Byte"),
                        ("OB_NUMBR", "Byte"), ("RESERVED_1", "Byte"), ("RESERVED_2", "Byte"),
                        ("PREV_CYCLE", "Int"), ("MIN_CYCLE", "Int"), ("MAX_CYCLE", "Int"),
                        ("DATE_TIME", "Date_And_Time"),
                    };
                case "CyclicInterrupt":
                    return new[]
                    {
                        ("EV_CLASS", "Byte"), ("STRT_INF", "Byte"), ("PRIORITY", "Byte"),
                        ("OB_NUMBR", "Byte"), ("RESERVED_1", "Byte"), ("RESERVED_2", "Byte"),
                        ("PHS_OFFSET", "Int"), ("RESERVED_3", "Int"), ("EXC_FREQ", "Int"),
                        ("DATE_TIME", "Date_And_Time"),
                    };
                default:
                    return Array.Empty<(string, string)>();
            }
        }

        private static readonly Dictionary<string, int> DatatypeBytes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bool"] = 1, ["Byte"] = 1, ["SInt"] = 1, ["USInt"] = 1, ["Char"] = 1,
            ["Word"] = 2, ["Int"] = 2, ["UInt"] = 2,
            ["DWord"] = 4, ["DInt"] = 4, ["UDInt"] = 4, ["Real"] = 4, ["Time"] = 4,
            ["LReal"] = 8, ["LInt"] = 8, ["LWord"] = 8, ["Date_And_Time"] = 8,
        };

        public static int InterfaceBytes(string secondaryType, IEnumerable<(string Name, string Datatype)> userTemps)
        {
            int total = StartInfoMembers(secondaryType).Sum(m => DatatypeBytes.TryGetValue(m.Datatype, out var b) ? b : 0);
            foreach (var t in userTemps ?? Enumerable.Empty<(string, string)>())
                total += DatatypeBytes.TryGetValue(t.Datatype ?? "", out var b) ? b : 0;
            return total;
        }

        /// <summary>
        /// Build the OB XML. <paramref name="networkSourceInner"/> must be one complete element
        /// (e.g. &lt;StructuredText xmlns="..."&gt;…&lt;/StructuredText&gt;) taken from a real export.
        /// </summary>
        public static string BuildXml(
            string obName,
            int number,
            string language,
            IEnumerable<(string Name, string Datatype)> userTemps,
            string networkSourceInner,
            string culture = "zh-CN",
            string titleZhCn = "",
            string commentZhCn = "")
        {
            if (string.IsNullOrWhiteSpace(obName))
                throw new ArgumentException("OB name is required.");
            if (number <= 0)
                throw new ArgumentException("OB number must be greater than 0.");
            if (string.IsNullOrWhiteSpace(networkSourceInner))
                throw new ArgumentException("OB body is empty (no tokenized network source).");

            string secondary = SecondaryTypeFor(number);
            if (secondary == null)
                throw new ArgumentException(
                    $"OB number {number} is not supported. Use 1 (program cycle), 30-38 (cyclic interrupt) or 100-102 (startup).");
            if (secondary == "Startup")
                throw new ArgumentException(
                    "Startup OBs (100-102) are not supported yet: no verified start-information template.");

            if (InterfaceBytes(secondary, userTemps) < 20)
                throw new ArgumentException(
                    "The composed OB interface is below the 20-byte minimum required for a standard OB.");

            var tempSection = new XElement(InterfaceNs + "Section", new XAttribute("Name", "Temp"));
            foreach (var m in StartInfoMembers(secondary))
            {
                tempSection.Add(new XElement(InterfaceNs + "Member",
                    new XAttribute("Name", $"OB{number}_{m.Suffix}"),
                    new XAttribute("Datatype", m.Datatype)));
            }
            foreach (var t in userTemps ?? Enumerable.Empty<(string, string)>())
            {
                if (string.IsNullOrWhiteSpace(t.Name)) continue;
                tempSection.Add(new XElement(InterfaceNs + "Member",
                    new XAttribute("Name", t.Name),
                    new XAttribute("Datatype", t.Datatype ?? "Bool")));
            }

            var networkSource = XElement.Parse("<NetworkSource>" + networkSourceInner + "</NetworkSource>");
            var compileUnit = new XElement("SW.Blocks.CompileUnit",
                new XAttribute("ID", "5"),
                new XAttribute("CompositionName", "CompileUnits"),
                new XElement("AttributeList",
                    networkSource,
                    new XElement("ProgrammingLanguage", language)),
                new XElement("ObjectList",
                    PlcBlockXmlHelpers.BuildMultilingualText("6", "7", "Comment", "", culture)));

            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Document",
                    new XElement("Engineering", new XAttribute("version", TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentEngineeringVersion)),
                    new XElement("DocumentInfo",
                        new XElement("Created", "2000-01-01T00:00:00.0000000Z"),
                        new XElement("ExportSetting", "None"),
                        new XElement("InstalledProducts")),
                    new XElement("SW.Blocks.OB",
                        new XAttribute("ID", "0"),
                        new XElement("AttributeList",
                            new XElement("Interface",
                                new XElement(InterfaceNs + "Sections",
                                    tempSection,
                                    new XElement(InterfaceNs + "Section", new XAttribute("Name", "Constant")))),
                            new XElement("Name", obName),
                            new XElement("Number", number),
                            new XElement("ProgrammingLanguage", language),
                            new XElement("SecondaryType", secondary)),
                        new XElement("ObjectList",
                            PlcBlockXmlHelpers.BuildMultilingualText("1", "2", "Comment", commentZhCn, culture),
                            PlcBlockXmlHelpers.BuildMultilingualText("3", "4", "Title", titleZhCn, culture),
                            compileUnit))));

            using var writer = new Utf8StringWriter();
            doc.Save(writer, SaveOptions.None);
            return writer.ToString();
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}
