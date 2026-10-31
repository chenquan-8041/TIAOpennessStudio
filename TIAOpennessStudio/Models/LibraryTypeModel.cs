using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TIAOpennessStudio.Services.Enums;
using TIAOpennessStudio.Services.Abstractions;
using Siemens.Engineering;
using Siemens.Engineering.Library.Types;

namespace TIAOpennessStudio.Models
{
	public class LibraryTypeModel : ILibraryTypeModel, ILibraryElement
	{
		public LibraryTypeModel(LibraryType type)
		{
			this.TypeObject = type;
			this.Name = type.Name;
			string name = type.GetType().Name;
			switch (name)
			{
			case "CodeBlockLibraryType":
				this.Type = LibraryItemType.Block;
				break;
			case "PlcTypeLibraryType":
				this.Type = LibraryItemType.PlcUdt;
				break;
			case "HmiUdtLibraryType":
				this.Type = LibraryItemType.HmiUdt;
				break;
			case "StyleLibraryType":
				this.Type = LibraryItemType.HmiStyle;
				break;
			case "StyleSheetLibraryType":
				this.Type = LibraryItemType.HmiStyleSheet;
				break;
			case "FaceplateLibraryType":
				this.Type = LibraryItemType.Faceplate;
				break;
			case "ScreenLibraryType":
				this.Type = LibraryItemType.Screen;
				break;
			case "CScriptLibraryType":
				this.Type = LibraryItemType.CScript;
				break;
			case "VBScriptLibraryType":
				this.Type = LibraryItemType.VbScript;
				break;
			default:
				this.Type = LibraryItemType.Unknown;
				break;
			}

			this.Guid = type.Guid;
			this.Comment = new Dictionary<string, string>();
			foreach (MultilingualTextItem multilingualTextItem in type.Comment.Items)
			{
				this.Comment.Add(multilingualTextItem.Language.Culture.Name, multilingualTextItem.Text);
			}
			this.Versions = new ObservableCollection<ILibraryVersionModel>();
			foreach (LibraryTypeVersion version in from o in type.Versions
			orderby o.VersionNumber
			select o)
			{
				this.Versions.Add(new LibraryVersionModel(this, version));
			}
			ILibraryVersionModel libraryVersionModel = this.Versions.LastOrDefault<ILibraryVersionModel>();
			this.LatestVersion = ((libraryVersionModel != null) ? libraryVersionModel.Version : null);
		}

		public string Name { get; set; }

		public string Author { get; set; }

		public LibraryItemType Type { get; set; }

		public Guid Guid { get; set; }

		public Dictionary<string, string> Comment { get; set; }

		public Version LatestVersion { get; set; }

		public bool OutDated { get; set; }

		public ObservableCollection<ILibraryVersionModel> Versions { get; set; }

		public string FolderPath { get; set; }

		private readonly LibraryType TypeObject;
	}
}