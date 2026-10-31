using System;

namespace TIAOpennessStudio.Services.Abstractions
{
	/// <summary>
	/// Common identity of a library element: name, author, and folder path.
	/// </summary>
	public interface ILibraryElement
	{
		string Name { get; set; }

		string Author { get; set; }

		string FolderPath { get; set; }
	}
}