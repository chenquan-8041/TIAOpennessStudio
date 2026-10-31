using System;

namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// Type conversion rule for type-safe conversion in WriteNodeAsync operations.
    /// </summary>
    public class TypeConversionRule
    {
        /// <summary>
        /// Source data type (e.g. "string", "int", "bool").
        /// </summary>
        public string SourceType { get; set; }

        /// <summary>
        /// Target OPC UA data type (e.g. "Boolean", "Int32", "Double", "String").
        /// </summary>
        public string TargetOpcType { get; set; }

        /// <summary>
        /// Converter that maps a source-typed object to the target OPC UA type.
        /// </summary>
        public Func<object, object> Converter { get; set; }
    }
}
