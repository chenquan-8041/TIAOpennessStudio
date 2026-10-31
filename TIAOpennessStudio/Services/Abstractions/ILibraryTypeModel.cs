using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using TIAOpennessStudio.Services.Enums;

namespace TIAOpennessStudio.Services.Abstractions
{
	/// <summary>
	/// Model of a library type: metadata, version collection, and staleness state.
	/// </summary>
	public interface ILibraryTypeModel : ILibraryElement
	{
		Dictionary<string, string> Comment { get; set; }

		Guid Guid { get; set; }

		Version LatestVersion { get; set; }

		LibraryItemType Type { get; set; }

		bool OutDated { get; set; }

		ObservableCollection<ILibraryVersionModel> Versions { get; set; }
	}
}