using System;
using System.Collections.Generic;
using TIAOpennessStudio.Services.Enums;

namespace TIAOpennessStudio.Services.Abstractions
{
	/// <summary>
	/// Model of a versioned library element: metadata, state, and dependency links.
	/// </summary>
	public interface ILibraryVersionModel
	{
		string Author { get; set; }

		Dictionary<string, string> Comment { get; set; }

		Guid Guid { get; set; }

		DateTime LastModified { get; set; }

		ILibraryTypeModel Parent { get; set; }

		LibraryVersionState State { get; set; }

		Version Version { get; set; }

		IList<ILibraryVersionModel> Dependencies { get; set; }

		IList<ILibraryVersionModel> Dependents { get; set; }
	}
}