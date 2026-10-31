using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Services.Abstractions
{
	/// <summary>
	/// Model of a copied library element: creation date and content descriptions.
	/// </summary>
	public interface ILibraryCopyModel : ILibraryElement
	{
		DateTime CreationDate { get; set; }

		Dictionary<string, string> ContentDescriptions { get; set; }
	}
}