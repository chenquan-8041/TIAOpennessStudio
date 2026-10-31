using System.Xml.Linq;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.IO;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

internal class PLCInstruction
{
    public PLCInstruction(Match match)
    {
        InstructionCode = match.Groups["instruction"].Value;
        Arguments = match.Groups["args"].Value.Split(',');
        (sourceInts, destInts) = GetIndices(InstructionCode);
        Navigator = match.Groups["navigator"].Value;
        _match = match;
        ReachedFrom = new();
        _gates = new() { "XIC", "XIO", "NEQ", "EQU", "LES", "LIM", "GEQ", "GRT", "LEQ", "ONS", "AFI", };
        _bools = new() { "OTU", "OTE", "OTL" };
    }

    private List<string> _gates { get; }
    private List<string> _bools { get; }

    public bool IsGate { get => _gates.Contains(InstructionCode); }
    public bool IsBool { get => _gates.Contains(InstructionCode) || _bools.Contains(InstructionCode); }

    private Match _match { get; set; }

    public Match OriginalMatch { get => _match; }

    public string InstructionCode { get; }
    public string[] Arguments { get; }
    public string Navigator { get; }
    public List<PLCInstruction> ReachedFrom { get; set; }
    public bool IsUnreachable => ReachedFrom.Any() && ReachedFrom.All(p => p.InstructionCode == "AFI" || p.IsUnreachable);

    private List<int> sourceInts { get; }
    private List<int> destInts { get; }

    public List<string> Sources
    {
        get => sourceInts.SelectMany(i => RegexHelper.addressPattern.Matches(Arguments[i]), (i, m) => m.Value).ToList() ?? new();
    }

    public List<string> Destinations
    {
        get => destInts.SelectMany(i => RegexHelper.addressPattern.Matches(Arguments[i]), (i, m) => m.Value).ToList() ?? new();
    }

    public required string SubroutineName { get; set; }
    public required int RungNumber { get; set; }
    public required int GlobalRungIndex { get; set; }
    //public required int GlobalInstructionIndex{get;set;}

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.Append($"\nCommand: {this.InstructionCode}\n");
        // sb.Append($"Reached via: ");
        // sb.AppendJoin(", ", ReachedFrom);
        sb.Append("\nSources:\n");
        sb.AppendJoin(", ", this.Sources);
        sb.Append("\nDestinations:\n");
        sb.AppendJoin(", ", this.Destinations);
        sb.Append("\nReachable:\n");
        sb.Append(IsUnreachable);

        sb.Append($"{this.SubroutineName} - Rung: {this.RungNumber}\n");

        return sb.ToString();
    }

    private (List<int>, List<int>) GetIndices(string instruction)//, out List<int> sourceInd, out List<int> destInd) 
    {
        List<int> sourceInd = new();
        List<int> destInd = new();

        switch (instruction)
        {

            case "XIC":
            case "XIO":
            case "ONS":
                destInd.Add(0);//sourceInd.Add(0);
                break;

            case "FLL":
            case "MOV":
            case "COP":
                sourceInd.Add(0);
                destInd.Add(1);
                break;

            case "OTE":
            case "OTL":
            case "OTU":
            case "CTU":
            case "RES":
            case "RTO":
            case "TON":
            case "TOF":
                destInd.Add(0); //sourceInd.Add(0);
                break;

            case "MUL":
            case "ADD":
            case "DIV":
            case "SUB":
            case "MVM":
                sourceInd.AddRange(new int[2] { 0, 1 });
                destInd.Add(2);
                break;

            case "NEQ":
            case "EQU":
            case "LES":
            case "GRT":
            case "GEQ":
            case "LEQ":
                destInd.AddRange(new int[2] { 0, 1 });
                break;

            case "LIM":
                //sourceInd.AddRange(new int[3] { 0, 1, 2 });
                destInd.AddRange(new int[3] { 0, 1, 2 });
                break;

            case "BTD":
            case "SWPB":
                sourceInd.Add(0);
                destInd.Add(2);
                break;

            case "FFL":
            case "FFU":
                sourceInd.AddRange(new int[2] { 0, 2 });
                destInd.Add(1);
                break;

            case "GSV":
                destInd.Add(3);
                break;

            case "CPT":
                sourceInd.Add(1);
                destInd.Add(0);
                break;

            case "PID":
                sourceInd.AddRange(new int[3] { 0, 2, 3 });
                destInd.Add(1);
                break;

            default:
                break;
        }

        return (sourceInd, destInd);
    }
}

/// <summary>
/// navigator parse key;
/// 
/// [ - branch depth ++, horiz id ++, vert id=0 -> new array dimension
/// ] - branch depth --, vert id reverts to prior branch depth, 0 for 0
/// , - vert id++,
/// " " - horiz ++
/// ; end rung, reset vals
/// </summary>
/// 

internal class RegexHelper
{
    static public Regex instructionPattern = new(@"(?'prenav'(?<=CDATA\[)\[)*(?'instruction'[A-Z]{3,4})\((?'args'(([^()]*,?)*(?'open'\()*([^()]*,?)*)?((?'close-open'\))*(?>[^()]*,?)*)*(?(open)(?!)))\)(?'navigator'[\[\],;\s]*)");
    static public Regex branchPattern = new(@"(?>(?>(?'open'\[)(?=[A-Z]{3,4}\(.*\)|\[)(?>(?>[^\]\[]+)(?>\[(?>[\w\s.+])*\])*)*)+(?>(?'close-open'\])(?>(?>[^\]\[]+)(?>\[(?>[\w\s.+])*\])*)*)+)+");
    static public Regex addressPattern = new(@"(((?!\d+\.\d+)(\w+(\.))+\w+)|(\w+(\[[\w\s+]+\]|(\:\w)+)(\.\w+)*)|(?<=\(|,|^)(\d*[a-zA-Z_]+\d*)+(?=\)|,|$))(\[(\d+|((\w+\.)*\w*))\])?(\.\d+)?");
}

internal enum LogicRepresentation
{
    SignalPath,
    IODisplay,
    ProcessVariableDisplay
}

public delegate List<string> TreeExtract(TreeExtract del, string address);

internal class Program
{


    private static void Main(string[] args)
    {


        XElement plcCode = XElement.Load(Path.Combine(Directory.GetCurrentDirectory(), "Kddfauto.xml"));

        Console.WriteLine("Accessing Code complete!");

        var rungs = plcCode.Descendants("Programs").Descendants("Rung");

        var rungInstructionsGroups = rungs.AsParallel().AsOrdered()
                                    .Select((r, i) => new
                                    {
                                        i,
                                        routine = r.Ancestors("Routine").First().Attribute("Name").Value,
                                        ro_RungNumber = r.Attribute("Number").Value,
                                        value = r.Element("Text").Value
                                    })
                                    .SelectMany((s, i) => RegexHelper.instructionPattern.Matches(s.value),
                                                (s, m) => new PLCInstruction(m)
                                                {
                                                    SubroutineName = s.routine,
                                                    RungNumber = int.Parse(s.ro_RungNumber),
                                                    GlobalRungIndex = s.i,
                                                })
                                    .GroupBy(r => r.GlobalRungIndex);


        var rungInstructions = rungInstructionsGroups.Select(g => g.ToArray());

        var rungstrings = rungs.AsParallel().AsOrdered()
                                .Select((r, i) => new { i, value = r.Element("Text").ToString() })
                                .SelectMany((s) => RegexHelper.branchPattern.Matches(s.value),
                                            (s, m) => new { s.i, match = m }).ToArray();

        var plcInstructions = rungInstructionsGroups.SelectMany(g => g.ToList()).ToList();

        IDictionary<string, List<string>> connectionsDict = rungs.AsParallel()
                                                               .Select((r, i) => new { i, value = r.Element("Text").ToString() })
                                                               .SelectMany((s) => RegexHelper.addressPattern.Matches(s.value), (s, m) => m.Value)
                                                               .Distinct()
                                                               .ToDictionary(s => s, m => new List<string>());

        var nestedKeys = connectionsDict.Keys.Where(s => s.Contains('[') && RegexHelper.addressPattern.Match(s.Split('[')[1].Split(']')[0]).Success)
                                             .Select(k => new KeyValuePair<string, List<string>>(RegexHelper.addressPattern.Match(k.Split('[')[1]).Value, new List<string>() { k, }));

        //hooks nested addresses up to outer address
        foreach (KeyValuePair<string, List<string>> kvp in nestedKeys)
        {
            Console.WriteLine($"address - {kvp.Key}     extracted address - {kvp.Value[0]}");

            if (connectionsDict.ContainsKey(kvp.Key))
            {
                connectionsDict[kvp.Key].Add(kvp.Value[0]);
                continue;
            }

            connectionsDict.Add(kvp);
        }

        foreach (string key in connectionsDict.Keys)
        {
            if (key.Contains("].")) continue; // stops ].1 picking ].10
            var subAddressKeys = connectionsDict.Keys.Where(k => k != key && k.Contains(key));
            connectionsDict[key].AddRange(subAddressKeys);
        }

        bool succ;
        int logicChoice;

        do
        {
            Console.WriteLine("Choose a logic representation; \n0 for signal path\n1 for I/O display\n2 for process variable gates");
            succ = int.TryParse(Console.ReadLine(), out logicChoice);
        } while (!succ);


        foreach ((int i, PLCInstruction ins) gate in plcInstructions.Select((source, i) => (i, ins: source)).Where(p => p.ins.IsGate))
        {
            List<bool> inSubRung = new();
            List<PLCInstruction> gates = new() { gate.ins, };

            var startIndex = gate.i;
            bool startSkip = false;
            PLCInstruction target;

            ParseNavigator(gate.ins.Navigator, ref startSkip, ref inSubRung);

            if (startSkip) SkipUntil(plcInstructions, ']', ref startIndex);


            for (int index = startIndex + 1; index > 0; index++)
            {
                bool skip = false;
                target = plcInstructions[index];

                // switch(logicChoice){
                //     case (int)LogicRepresentation.SignalPath:
                //         target.ReachedFrom.AddRange(gates);
                //     break;
                //     case (int)LogicRepresentation.IODisplay:
                //     break;
                //     case (int)LogicRepresentation.ProcessVariableDisplay:
                //     break;
                // }


                target.ReachedFrom.AddRange(gates);

                // breaker
                if (!inSubRung.Any() && target.IsGate) break; //next gate start
                if (target.Navigator.Contains(';')) break; //rung end

                //skip decision
                ParseNavigator(target.Navigator, ref skip, ref inSubRung);

                //skippers
                if (skip) SkipUntil(plcInstructions, ']', ref index);

            }
        }

        foreach (PLCInstruction ins in plcInstructions)
        {

            if (ins.InstructionCode != "JSR") continue;

            var jsrGate = ins.ReachedFrom;

            var jsrTargets = plcInstructions.Where(p => p.SubroutineName == ins.Arguments[0] && !p.ReachedFrom.Any());

            foreach (PLCInstruction target in jsrTargets)
            {
                target.ReachedFrom = jsrGate;
            }
        }

        foreach (PLCInstruction ins in plcInstructions)
        {

            if (ins.InstructionCode == "AFI" || ins.InstructionCode == "JSR" || ins.InstructionCode == "RET") continue;


            switch (logicChoice)
            {
                case (int)LogicRepresentation.SignalPath:
                    foreach (string source in ins.ReachedFrom.SelectMany(s => s.Destinations))
                    {
                        if (string.IsNullOrWhiteSpace(source)) continue;
                        connectionsDict[source].AddRange(ins.Destinations);
                    }
                    break;
                case (int)LogicRepresentation.IODisplay when !ins.IsGate:
                    if (ins.IsUnreachable) continue;

                    List<PLCInstruction> allGates = new();
                    allGates.AddRange(ins.ReachedFrom);

                    var extensions = allGates.AsParallel().SelectMany(gate => gate.ReachedFrom).Distinct();

                    while (extensions.Any())
                    {
                        allGates.AddRange(extensions);
                        extensions = allGates.AsParallel().SelectMany(gate => gate.ReachedFrom).Distinct().Except(allGates.AsParallel());
                    }

                    foreach (string source in allGates.Distinct().Where(gate => gate.InstructionCode != "AFI").Select(gate => gate.Destinations[0]))
                    {
                        connectionsDict[source].Add(ins.Destinations[0]);
                    }


                    break;
                default:
                    break;

            }


            // foreach (string source in ins.ReachedFrom.SelectMany(s => s.Destinations))
            // {
            //     if (string.IsNullOrWhiteSpace(source)) continue;
            //     connectionsDict[source].AddRange(ins.Destinations);
            // }
        }

        plcInstructions.AsParallel().Where(ins => !ins.IsGate || !ins.Sources.All(string.IsNullOrWhiteSpace) || !ins.Destinations.All(string.IsNullOrWhiteSpace))
                                    .SelectMany(ins => ins.Sources, (ins, s) => new { ins, s })
                                    .ForAll(a => connectionsDict[a.s].AddRange(a.ins.Destinations));

        var unreachable = plcInstructions.Where(p => p.IsUnreachable).ToList();

        Console.WriteLine($"{unreachable.Count}/{plcInstructions.Count} unreachable instructions");

        Console.WriteLine("Enter y to skip unreachable addresses");
        bool afiSkip = Console.ReadLine() == "y";

        if (afiSkip)
        {
            connectionsDict.AsParallel()
                           .Select(kvp => kvp.Key)
                           .Where(key => plcInstructions.Where(p => p.Sources.Contains(key)
                                                                 || p.Destinations.Contains(key))
                                                        .All(p => unreachable.Contains(p)))
                           .ForAll(key => connectionsDict.Remove(key));
            connectionsDict.AsParallel()
                           .Select(kvp => kvp.Value)
                           .ForAll(L => L.RemoveAll(address => plcInstructions.Where(p => p.Sources.Contains(address) || p.Destinations.Contains(address)).All(p => unreachable.Contains(p))));
        }

        Console.WriteLine("Please enter a valid folder name, or blank to exit");

        string dirName = Console.ReadLine();
        if (string.IsNullOrEmpty(dirName)) return;

        DirectoryInfo dir = Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), dirName));

        List<string> selectedFiles;

        Console.WriteLine("type y for address tree");
        bool addressTree = Console.ReadLine() == "y";
        string address = "";
        if (addressTree)
        {

            while (true)
            {
                Console.WriteLine("Enter address to start from:");
                address = Console.ReadLine();
                if (connectionsDict.ContainsKey(address)) break;
                Console.WriteLine("address not found");
            }
            string treedir = "";

            while (true)
            {
                Console.WriteLine("Choose tree direction: f for forwards or r for reverse, or leave blank to exit");
                treedir = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(treedir)) return;
                if (treedir != "f" && treedir != "r") continue;
                break;
            }

            selectedFiles = new() { address, };

            switch (treedir)
            {
                case "f":

                    selectedFiles.AddRange(connectionsDict[address]);

                    var extensions = selectedFiles.AsParallel().SelectMany(s => connectionsDict[s]).Distinct().Except(selectedFiles.AsParallel());

                    while (extensions.Any())
                    {
                        selectedFiles.AddRange(extensions);
                        extensions = selectedFiles.AsParallel().SelectMany(s => connectionsDict[s]).Distinct().Except(selectedFiles.AsParallel());
                    }

                    break;

                case "r":

                    //connectionsDict[address] = new();

                    // var additions = connectionsDict.Keys.Where(k=>connectionsDict[k].Contains(address));

                    // foreach( string addition in additions){
                    //     connectionsDict[addition] = new(){address,};
                    // }

                    //var additions = selectedFiles.AsParallel().SelectMany(s=>connectionsDict.Where(kvp=>kvp.Value.Contains(s)),(s,kvp)=>kvp.Key).Distinct().Except(selectedFiles.AsParallel());

                    while (true)
                    {
                        var additions = selectedFiles.AsParallel().SelectMany(s => connectionsDict.Where(kvp => kvp.Value.Contains(s)), (s, kvp) => kvp.Key).Distinct().Except(selectedFiles.AsParallel());
                        if (!additions.Any()) break;
                        selectedFiles.AddRange(additions);
                    }

                    foreach (string a in selectedFiles)
                    {
                        connectionsDict[a] = connectionsDict[a].Intersect(selectedFiles).ToList();
                    }

                    break;

            }


            //Console.WriteLine(selectedFiles.Count());
        }

        else { selectedFiles = connectionsDict.Keys.ToList(); }


        foreach (KeyValuePair<string, List<string>> pair in connectionsDict.Where(p => selectedFiles.Contains(p.Key)))
        {
            StringBuilder sb = new();

            var relevantIns = plcInstructions.AsParallel().Where(p => p.Sources.Contains(pair.Key) || p.Destinations.Contains(pair.Key));

            if (addressTree && pair.Key == address) sb.AppendLine(@"#TreeStart");
            if (relevantIns.All(p => unreachable.Contains(p)))
            {
                if (afiSkip) continue;
                sb.AppendLine(@"#AFI");
            }
            if (relevantIns.All(p => p.IsBool)) sb.AppendLine(@"#gate");
            if (connectionsDict.Values.AsParallel().All(l => !l.Contains(pair.Key)))
            {
                if (relevantIns.All(ins => ins.IsGate)) sb.AppendLine(@"#boolInput");
                else sb.AppendLine(@"#input");
            }
            if (pair.Value.All(string.IsNullOrWhiteSpace) || !pair.Value.Any())
            {
                if (relevantIns.All(ins => ins.IsBool)) sb.AppendLine(@"#boolOutput");
                else sb.AppendLine(@"#output");
            }

            if (pair.Value.All(p => p.Contains(pair.Key))) sb.AppendLine(@"#parentNode");

            using StreamWriter outputfile = new(Path.Combine(dir.Name, pair.Key.Replace(':', ';').Replace('[', '(').Replace(']', ')') + ".md"), false);
            outputfile.WriteLine(sb.ToString());
            foreach (string connection in pair.Value.Distinct())
            {
                outputfile.WriteLine($"[[{connection.Replace(':', ';').Replace('[', '(').Replace(']', ')')}]]");
            }
        }



        // var outputFiles = selectedFiles.SelectMany(s=>connectionsDict[s]).Distinct().Except(connectionsDict.Keys.AsParallel().Where(k=>connectionsDict[k].Any()));

        // foreach(string file in outputFiles){
        //     using StreamWriter outputfile = new(Path.Combine(dir.Name, file.Replace(':', ';').Replace('[', '(').Replace(']', ')') + ".md"),false);
        //     outputfile.WriteLine(@"#output");
        // }

        static void ParseNavigator(string navigator, ref bool skip, ref List<bool> subrung)
        {
            foreach (char nav in navigator)
            {
                switch (nav)
                {
                    case ']':
                        if (subrung.Any()) subrung.Remove(true);
                        skip = false;
                        break;
                    case '[':
                        if (!skip) subrung.Add(true);
                        break;
                    case ',':
                        if (!subrung.Any()) skip = true;
                        break;
                    default:
                        break;
                }
            }
        }

        static void SkipUntil(List<PLCInstruction> instructions, char stop, ref int index)
        {
            PLCInstruction target;
            List<bool> subrung = new();

            bool subrungExit = false;

            do
            {
                subrungExit = false;
                index++;
                target = instructions[index];

                foreach (char nav in target.Navigator)
                {
                    switch (nav)
                    {
                        case '[':
                            subrung.Add(true);
                            break;
                        case ']':
                            subrungExit = !subrung.Remove(true);
                            break;
                        default:
                            break;
                    }
                }
            } while (!target.Navigator.Contains(stop) && subrungExit);
        }
    }
}