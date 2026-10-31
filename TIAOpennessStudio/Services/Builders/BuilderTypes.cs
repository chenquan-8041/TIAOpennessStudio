using System;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// PLC UDT member definition, describing a single field in a user-defined data type.
    /// </summary>
    public sealed class PlcUdtMemberDefinition
    {
        /// <summary>
        /// Creates a new UDT member definition.
        /// </summary>
        /// <param name="name">Member name.</param>
        /// <param name="datatype">Data type (e.g. Bool, Int, Real, etc.).</param>
        /// <param name="externalWritable">Whether external write is allowed; defaults to false.</param>
        /// <param name="commentZhCn">Chinese comment; defaults to empty.</param>
        public PlcUdtMemberDefinition(string name, string datatype, bool externalWritable = false, string commentZhCn = "")
        {
            Name = name;
            Datatype = datatype;
            ExternalWritable = externalWritable;
            CommentZhCn = commentZhCn;
        }

        /// <summary>Member name.</summary>
        public string Name { get; }

        /// <summary>Data type.</summary>
        public string Datatype { get; }

        /// <summary>Whether external write is allowed.</summary>
        public bool ExternalWritable { get; }

        /// <summary>Chinese comment.</summary>
        public string CommentZhCn { get; }
    }

    /// <summary>
    /// PLC tag definition, describing a variable tag in a PLC tag table.
    /// </summary>
    public sealed class PlcTagDefinition
    {
        /// <summary>
        /// Creates a new PLC tag definition.
        /// </summary>
        /// <param name="name">Tag name.</param>
        /// <param name="dataTypeName">Data type name.</param>
        /// <param name="logicalAddress">Logical address (e.g. %MW0, %DB1.DBW0, etc.).</param>
        public PlcTagDefinition(string name, string dataTypeName, string logicalAddress)
        {
            Name = name;
            DataTypeName = dataTypeName;
            LogicalAddress = logicalAddress;
        }

        /// <summary>Tag name.</summary>
        public string Name { get; }

        /// <summary>Data type name.</summary>
        public string DataTypeName { get; }

        /// <summary>Logical address.</summary>
        public string LogicalAddress { get; }
    }

    /// <summary>
    /// PLC global data-block member definition, describing a single variable in a DB.
    /// </summary>
    public sealed class PlcDbMemberDefinition
    {
        /// <summary>
        /// Creates a new DB member definition.
        /// </summary>
        /// <param name="name">Member name.</param>
        /// <param name="datatype">Data type.</param>
        /// <param name="externalWritable">Whether external write is allowed; null means unspecified.</param>
        /// <param name="commentZhCn">Chinese comment; defaults to empty.</param>
        /// <param name="startValue">Initial value; defaults to empty.</param>
        public PlcDbMemberDefinition(string name, string datatype, bool? externalWritable = null, string commentZhCn = "", string startValue = "")
        {
            Name = name;
            Datatype = datatype;
            ExternalWritable = externalWritable;
            CommentZhCn = commentZhCn;
            StartValue = startValue;
        }

        /// <summary>Member name.</summary>
        public string Name { get; }

        /// <summary>Data type.</summary>
        public string Datatype { get; }

        /// <summary>Whether external write is allowed.</summary>
        public bool? ExternalWritable { get; }

        /// <summary>Chinese comment.</summary>
        public string CommentZhCn { get; }

        /// <summary>Initial value.</summary>
        public string StartValue { get; }
    }

    /// <summary>
    /// PLC block interface member definition, describing a member of an FB/FC/OB
    /// Input/Output/InOut/Static/Temp section.
    /// </summary>
    public sealed class PlcBlockMemberDefinition
    {
        /// <summary>
        /// Creates a new block interface member definition.
        /// </summary>
        /// <param name="name">Member name.</param>
        /// <param name="datatype">Data type.</param>
        /// <param name="commentZhCn">Chinese comment; defaults to empty.</param>
        public PlcBlockMemberDefinition(string name, string datatype, string commentZhCn = "")
        {
            Name = name;
            Datatype = datatype;
            CommentZhCn = commentZhCn ?? "";
        }

        /// <summary>Member name.</summary>
        public string Name { get; }

        /// <summary>Data type.</summary>
        public string Datatype { get; }

        /// <summary>Chinese comment.</summary>
        public string CommentZhCn { get; }
    }

    /// <summary>
    /// FlgNet call parameter, representing a single argument of a CALL instruction in
    /// S7 GRAPH / F-LAD.
    /// </summary>
    public sealed class FlgNetCallParameter
    {
        /// <summary>
        /// Creates a new FlgNet call parameter.
        /// </summary>
        /// <param name="parameterName">Formal parameter name.</param>
        /// <param name="section">Parameter section (e.g. Input, Output, InOut).</param>
        /// <param name="dataType">Data type.</param>
        /// <param name="sourceKind">Data source kind.</param>
        /// <param name="constantValue">Constant value (only valid when sourceKind is LiteralConstant).</param>
        /// <param name="symbolPath">Symbol path (only valid when sourceKind is GlobalVariable).</param>
        public FlgNetCallParameter(string parameterName, string section, string dataType, FlgNetSourceKind sourceKind, string constantValue, string[] symbolPath)
        {
            ParameterName = parameterName;
            Section = section;
            DataType = dataType;
            SourceKind = sourceKind;
            ConstantValue = constantValue;
            SymbolPath = symbolPath;
        }

        /// <summary>Formal parameter name.</summary>
        public string ParameterName { get; }

        /// <summary>Parameter section.</summary>
        public string Section { get; }

        /// <summary>Data type.</summary>
        public string DataType { get; }

        /// <summary>Data source kind.</summary>
        public FlgNetSourceKind SourceKind { get; }

        /// <summary>Constant value.</summary>
        public string ConstantValue { get; }

        /// <summary>Symbol path.</summary>
        public string[] SymbolPath { get; }

        /// <summary>
        /// Access UID, auto-assigned by the FlgNet XML builder at build time (mutable).
        /// </summary>
        public int AccessUid { get; set; }

        /// <summary>
        /// Creates a global-variable call parameter.
        /// </summary>
        /// <param name="parameterName">Formal parameter name.</param>
        /// <param name="section">Parameter section.</param>
        /// <param name="dataType">Data type.</param>
        /// <param name="symbolPath">Symbol path.</param>
        /// <returns>The global-variable call parameter instance.</returns>
        public static FlgNetCallParameter Global(string parameterName, string section, string dataType, params string[] symbolPath)
        {
            return new FlgNetCallParameter(parameterName, section, dataType, FlgNetSourceKind.GlobalVariable, "", symbolPath);
        }

        /// <summary>
        /// Creates a constant call parameter.
        /// </summary>
        /// <param name="parameterName">Formal parameter name.</param>
        /// <param name="section">Parameter section.</param>
        /// <param name="dataType">Data type.</param>
        /// <param name="value">Constant value.</param>
        /// <returns>The constant call parameter instance.</returns>
        public static FlgNetCallParameter Constant(string parameterName, string section, string dataType, string value)
        {
            return new FlgNetCallParameter(parameterName, section, dataType, FlgNetSourceKind.LiteralConstant, value, Array.Empty<string>());
        }
    }

    /// <summary>
    /// FlgNet data source type enumeration.
    /// </summary>
    public enum FlgNetSourceKind
    {
        /// <summary>Global variable.</summary>
        GlobalVariable,

        /// <summary>Literal constant.</summary>
        LiteralConstant
    }
}
