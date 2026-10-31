using System.Collections.Generic;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// S7-GRAPH block definition: block header + one step sequence.
    /// Mirrors the exported GRAPH FB skeleton (SW.Blocks.FB + ProgrammingLanguage=GRAPH).
    /// </summary>
    public sealed class GraphBlockDefinition
    {
        public string BlockName { get; set; } = "";
        public int BlockNumber { get; set; }
        public string TitleZhCn { get; set; } = "";
        public string CommentZhCn { get; set; } = "";
        public List<PlcBlockMemberDefinition> Inputs { get; set; } = new List<PlcBlockMemberDefinition>();
        public List<PlcBlockMemberDefinition> Outputs { get; set; } = new List<PlcBlockMemberDefinition>();
        public List<PlcBlockMemberDefinition> InOuts { get; set; } = new List<PlcBlockMemberDefinition>();
        public List<PlcBlockMemberDefinition> Statics { get; set; } = new List<PlcBlockMemberDefinition>();
        public List<PlcBlockMemberDefinition> Temps { get; set; } = new List<PlcBlockMemberDefinition>();
        public GraphSequenceDefinition Sequence { get; set; } = new GraphSequenceDefinition();
    }

    /// <summary>One GRAPH sequence: steps, transitions, branches and the connections between them.</summary>
    public sealed class GraphSequenceDefinition
    {
        public string TitleZhCn { get; set; } = "";
        public string CommentZhCn { get; set; } = "";
        public List<GraphStepDefinition> Steps { get; set; } = new List<GraphStepDefinition>();
        public List<GraphTransitionDefinition> Transitions { get; set; } = new List<GraphTransitionDefinition>();
        public List<GraphBranchDefinition> Branches { get; set; } = new List<GraphBranchDefinition>();
        public List<GraphConnectionDefinition> Connections { get; set; } = new List<GraphConnectionDefinition>();
    }

    /// <summary>A GRAPH step with its action rows and optional supervision/interlock logic.</summary>
    public sealed class GraphStepDefinition
    {
        public int Number { get; set; }
        public string Name { get; set; } = "";
        public bool Init { get; set; }
        public string MaximumStepTime { get; set; } = "";
        public string WarningTime { get; set; } = "";
        public string CommentZhCn { get; set; } = "";
        public string ActionsTitleZhCn { get; set; } = "";
        public List<GraphActionDefinition> Actions { get; set; } = new List<GraphActionDefinition>();
        public GraphFlgNetDefinition Supervision { get; set; }
        public GraphFlgNetDefinition Interlock { get; set; }
    }

    /// <summary>One action row: qualifier + operand text (+ optional timer/parameter value).</summary>
    public sealed class GraphActionDefinition
    {
        public string Qualifier { get; set; } = "N";
        public string Event { get; set; } = "";
        public bool Interlock { get; set; }
        public string Operand { get; set; } = "";
        public string Parameter { get; set; } = "";
    }

    /// <summary>A transition: number/name plus the FlgNet condition that enables it.</summary>
    public sealed class GraphTransitionDefinition
    {
        public int Number { get; set; }
        public string Name { get; set; } = "";
        public bool IsMissing { get; set; }
        public string ProgrammingLanguage { get; set; } = "LAD";
        public string CommentZhCn { get; set; } = "";
        public GraphFlgNetDefinition Condition { get; set; } = new GraphFlgNetDefinition();
    }

    /// <summary>A branch node. Type uses the GRAPH schema enum: AltBegin/AltEnd/SimBegin/SimEnd.</summary>
    public sealed class GraphBranchDefinition
    {
        public int Number { get; set; }
        public string Type { get; set; } = "AltBegin";
        public int Cardinality { get; set; } = 2;
    }

    /// <summary>A directed connection between two sequence nodes (step / transition / branch / end).</summary>
    public sealed class GraphConnectionDefinition
    {
        public GraphNodeReference From { get; set; } = new GraphNodeReference();
        public GraphNodeReference To { get; set; } = new GraphNodeReference();
        public string LinkType { get; set; } = "Direct";
    }

    /// <summary>Connection endpoint: Kind = Step | Transition | Branch | End; In/Out only for Branch.</summary>
    public sealed class GraphNodeReference
    {
        public string Kind { get; set; } = "";
        public int Number { get; set; }
        public int? In { get; set; }
        public int? Out { get; set; }
    }

    /// <summary>
    /// A FlgNet condition graph (transition / supervision / interlock): explicit Parts + Accesses + Wires.
    /// The same contract works for LAD networks, but GRAPH nests it under the Graph namespace.
    /// </summary>
    public sealed class GraphFlgNetDefinition
    {
        public List<GraphFlgNetPart> Parts { get; set; } = new List<GraphFlgNetPart>();
        public List<GraphFlgNetAccess> Accesses { get; set; } = new List<GraphFlgNetAccess>();
        public List<GraphFlgNetWire> Wires { get; set; } = new List<GraphFlgNetWire>();
    }

    public sealed class GraphFlgNetPart
    {
        public string Name { get; set; } = "";
        public int UId { get; set; }
    }

    public sealed class GraphFlgNetAccess
    {
        public int UId { get; set; }
        public string Scope { get; set; } = "GlobalVariable";
        public List<string> SymbolPath { get; set; } = new List<string>();
        public string ConstantType { get; set; } = "Bool";
        public string ConstantValue { get; set; } = "";
    }

    public sealed class GraphFlgNetWire
    {
        public int UId { get; set; }
        public GraphFlgNetNode From { get; set; } = new GraphFlgNetNode();
        public GraphFlgNetNode To { get; set; } = new GraphFlgNetNode();
    }

    /// <summary>Wire endpoint: Kind = Powerrail | NameCon | IdentCon | OpenCon.</summary>
    public sealed class GraphFlgNetNode
    {
        public string Kind { get; set; } = "";
        public int UId { get; set; }
        public string Pin { get; set; } = "";
    }
}
