using System;
using System.Collections.Generic;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
	/// <summary>Holds the matched copy/type pairs produced by a library comparison.</summary>
	public class CompareResults
	{
		public IList<Tuple<ILibraryCopyModel, ILibraryCopyModel>> CopyPairs;

		public IList<Tuple<ILibraryTypeModel, ILibraryTypeModel>> TypePairs;
	}
}