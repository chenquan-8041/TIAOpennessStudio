using System;
using System.ComponentModel;

namespace TIAOpennessStudio.Services.Enums
{
	/// <summary>Kinds of items that can appear in a TIA global library.</summary>
	[TypeConverter(typeof(EnumToStringUsingDescription))]
	public enum LibraryItemType
	{
		[Description("None")]
		Unknown,
		[Description("Faceplate")]
		Faceplate,
		[Description("C Script")]
		CScript,
		[Description("VB Script")]
		VbScript,
		[Description("Screen")]
		Screen,
		[Description("HMI Style")]
		HmiStyle,
		[Description("HMI Style Sheet")]
		HmiStyleSheet,
		[Description("HMI UDT")]
		HmiUdt,
		[Description("Block")]
		Block,
		[Description("PLC UDT")]
		PlcUdt
	}
}