using System;

namespace TIAOpennessStudio.Services.Enums
{
	/// <summary>Outcome of an online/offline comparison entry.</summary>
	public enum CompareResult
	{
		None,
		Equal,
		Left,
		Right,
		Diff
	}
}