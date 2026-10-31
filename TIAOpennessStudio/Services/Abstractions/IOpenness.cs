using System;
using System.Collections.Generic;
using TIAOpennessStudio.Services.Implementations;

namespace TIAOpennessStudio.Services.Abstractions
{
	/// <summary>
	/// Low-level Openness facade for comparing library archives and reporting progress.
	/// </summary>
	public interface IOpenness
	{
		CompareResults Compare(string leftPath, string rightPath);

		void DetailCompare(string leftPath, string rightPath, IEnumerable<ILibraryTypeModel> leftModels, IEnumerable<ILibraryTypeModel> rightModels);

		event ProgressUpdate OnProgressUpdate;
	}
}