using System;
using System.Collections.Generic;
using System.Linq;
using TIAOpennessStudio.Services.Enums;
using TIAOpennessStudio.Services.Abstractions;
using Siemens.Engineering;
using Siemens.Engineering.Library.Types;

namespace TIAOpennessStudio.Models
{
	public class LibraryVersionModel : ILibraryVersionModel
	{
		public LibraryVersionModel(ILibraryTypeModel parent, LibraryTypeVersion version)
		{
			this.VersionObject = version;
			this.Parent = parent;
			this.Author = version.Author;
			this.Guid = version.Guid;
			this.Comment = new Dictionary<string, string>();
			foreach (MultilingualTextItem multilingualTextItem in version.Comment.Items)
			{
				this.Comment.Add(multilingualTextItem.Language.Culture.Name, multilingualTextItem.Text);
			}
			this.Version = version.VersionNumber;
			this.LastModified = version.ModifiedDate;
			LibraryVersionState state;
			if (!Enum.TryParse(version.State.ToString(), true, out state))
			{
				state = LibraryVersionState.InWork;
			}
			this.State = state;
			this.Dependencies = new List<ILibraryVersionModel>();
			this.Dependents = new List<ILibraryVersionModel>();
			this.DependencyGuids = (from x in version.Dependencies
			select x.Guid).ToList<Guid>();
			this.DependentGuids = (from x in version.Dependents
			select x.Guid).ToList<Guid>();
		}

		public ILibraryTypeModel Parent { get; set; }

		public string Author { get; set; }

		public Guid Guid { get; set; }

		public Dictionary<string, string> Comment { get; set; }

		public Version Version { get; set; }

		public DateTime LastModified { get; set; }

		public LibraryVersionState State { get; set; }

		public IList<ILibraryVersionModel> Dependencies { get; set; }

		public IList<ILibraryVersionModel> Dependents { get; set; }

		public IList<Guid> DependentGuids { get; set; }

		public IList<Guid> DependencyGuids { get; set; }

		private readonly LibraryTypeVersion VersionObject;
	}
}