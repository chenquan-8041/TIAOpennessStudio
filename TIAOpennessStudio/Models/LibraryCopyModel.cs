using System;
using System.Collections.Generic;
using TIAOpennessStudio.Services.Abstractions;
using Siemens.Engineering.Library.MasterCopies;

namespace TIAOpennessStudio.Models
{
	public class LibraryCopyModel : ILibraryCopyModel, ILibraryElement
	{
		public LibraryCopyModel(MasterCopy copy)
		{
			this.Name = copy.Name;
			this.Author = copy.Author;
			this.CreationDate = copy.CreationDate;
			this.ContentDescriptions = new Dictionary<string, string>();
			foreach (MasterCopyContentDescription masterCopyContentDescription in copy.ContentDescriptions)
			{
				this.ContentDescriptions.Add(masterCopyContentDescription.ContentName, masterCopyContentDescription.ContentType.ToString());
			}
		}

		public string Name { get; set; }

		public string Author { get; set; }

		public string FolderPath { get; set; }

		public DateTime CreationDate { get; set; }

		public Dictionary<string, string> ContentDescriptions { get; set; }
	}
}