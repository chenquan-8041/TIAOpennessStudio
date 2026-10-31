using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Models;
using Siemens.Engineering;
using Siemens.Engineering.Compare;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;

namespace TIAOpennessStudio.Services.Implementations
{
	/// <summary>Compares two TIA library artifacts (copies and types) via the Openness Compare API.</summary>
	public class LibraryCompareEngine : IOpenness
	{
		public event ProgressUpdate OnProgressUpdate
		{
			add
			{
				this._onProgressUpdate = (ProgressUpdate)Delegate.Combine(this._onProgressUpdate, value);
			}
			remove
			{
				this._onProgressUpdate = (ProgressUpdate)Delegate.Remove(this._onProgressUpdate, value);
			}
		}

		public CompareResults Compare(string leftPath, string rightPath)
		{
			if (string.IsNullOrEmpty(leftPath))
			{
				throw new ArgumentException("Parameter is null or empty", "leftPath");
			}
			if (string.IsNullOrEmpty(rightPath))
			{
				throw new ArgumentException("Parameter is null or empty", "rightPath");
			}
			FileInfo fileInfo = new FileInfo(leftPath);
			if (!fileInfo.Exists)
			{
				throw new FileNotFoundException("File does not exist", fileInfo.FullName);
			}
			FileInfo fileInfo2 = new FileInfo(rightPath);
			if (!fileInfo2.Exists)
			{
				throw new FileNotFoundException("File does not exist", fileInfo2.FullName);
			}
			this.UpdateProgress(0, "Start Openness");
			TiaPortal tiaPortal = null;
			CompareResults result;
			try
			{
				this.UpdateProgress(0, "Openning Project/Library 1: " + fileInfo.FullName);
				ILibrary library = LibraryCompareEngine.OpenFile(ref tiaPortal, fileInfo);
				this.UpdateProgress(0, "Browse all Types Library 1");
				List<LibraryTypeModel> list = LibraryCompareEngine.GetAllTypes(library.TypeFolder, "").ToList<LibraryTypeModel>();
				this.UpdateProgress(0, "Found " + list.Count + " Types");
				this.UpdateProgress(0, "Browse all MasterCopies Library 1");
				List<LibraryCopyModel> list2 = LibraryCompareEngine.GetAllMasterCopies(library.MasterCopyFolder, "").ToList<LibraryCopyModel>();
				this.UpdateProgress(0, "Found " + list2.Count + " MasterCopies");
				this.UpdateProgress(0, "Close Library 1");
				LibraryCompareEngine.CloseLibrary(library);
				this.UpdateProgress(0, "Openning Project/Library 2: " + fileInfo2.FullName);
				ILibrary library2 = LibraryCompareEngine.OpenFile(ref tiaPortal, fileInfo2);
				this.UpdateProgress(0, "Browse all Types Library 2");
				List<LibraryTypeModel> list3 = LibraryCompareEngine.GetAllTypes(library2.TypeFolder, "").ToList<LibraryTypeModel>();
				this.UpdateProgress(0, "Found " + list3.Count + " Types");
				this.UpdateProgress(0, "Browse all MasterCopies Library 2");
				List<LibraryCopyModel> list4 = LibraryCompareEngine.GetAllMasterCopies(library2.MasterCopyFolder, "").ToList<LibraryCopyModel>();
				this.UpdateProgress(0, "Found " + list4.Count + " MasterCopies");
				this.UpdateProgress(0, "Close Library 2");
				LibraryCompareEngine.CloseLibrary(library2);
				this.UpdateProgress(0, "Create Type Relations");
				IList<Tuple<ILibraryTypeModel, ILibraryTypeModel>> list5 = LibraryCompareEngine.CreateTypeRelations(list, list3);
				this.UpdateProgress(0, "Created " + list5.Count + " Type Relations");
				this.UpdateProgress(0, "Read Dependency structure");
				LibraryCompareEngine.AnalyzeTypes(list5);
				this.UpdateProgress(0, "Dependency structure complete");
				this.UpdateProgress(0, "Create MasterCopy Relations");
				IList<Tuple<ILibraryCopyModel, ILibraryCopyModel>> list6 = LibraryCompareEngine.CreateMasterCopyRelations(list2, list4);
				this.UpdateProgress(0, "Created " + list6.Count + " MasterCopy Relations");
				this.UpdateProgress(0, "Done");
				result = new CompareResults
				{
					TypePairs = list5,
					CopyPairs = list6
				};
			}
			finally
			{
				if (tiaPortal != null)
				{
					tiaPortal.Dispose();
				}
			}
			return result;
		}

		public void DetailCompare(string leftPath, string rightPath, IEnumerable<ILibraryTypeModel> leftModels, IEnumerable<ILibraryTypeModel> rightModels)
		{
			if (string.IsNullOrEmpty(leftPath))
			{
				throw new ArgumentException("Parameter is null or empty", "leftPath");
			}
			if (string.IsNullOrEmpty(rightPath))
			{
				throw new ArgumentException("Parameter is null or empty", "rightPath");
			}
			FileInfo fileInfo = new FileInfo(leftPath);
			if (!fileInfo.Exists)
			{
				throw new FileNotFoundException("File does not exist", fileInfo.FullName);
			}
			FileInfo fileInfo2 = new FileInfo(rightPath);
			if (!fileInfo2.Exists)
			{
				throw new FileNotFoundException("File does not exist", fileInfo2.FullName);
			}
			if (fileInfo.Extension == ".ap14" && fileInfo2.Extension == ".ap14")
			{
				throw new ArgumentException("Comparing 2 projects is not possible");
			}
			this.UpdateProgress(0, "Start Openness");
			using (TiaPortal tiaPortal = new TiaPortal(TiaPortalMode.WithUserInterface))
			{
				this.UpdateProgress(0, "Prepare Library 1: " + fileInfo.FullName);
				UserGlobalLibrary userGlobalLibrary = LibraryCompareEngine.PrepareLibrary(tiaPortal, fileInfo);
				this.UpdateProgress(0, "Prepare Library 2: " + fileInfo2.FullName);
				UserGlobalLibrary userGlobalLibrary2 = LibraryCompareEngine.PrepareLibrary(tiaPortal, fileInfo2);
				this.UpdateProgress(0, "Create Compare project");
				Project project = tiaPortal.Projects.Create(new DirectoryInfo(Path.GetTempPath()), string.Concat(new string[]
				{
					userGlobalLibrary.Name,
					"-",
					userGlobalLibrary2.Name,
					"_",
					DateTime.Now.ToString("yyyyMMdd-hhmmss")
				}));
				this.UpdateProgress(0, "Prepare project for Library 1");
				PlcSoftware plcSoftware = LibraryCompareEngine.PrepareProject(project, userGlobalLibrary, leftModels);
				this.UpdateProgress(0, "Prepare project for Library 2");
				PlcSoftware plcSoftware2 = LibraryCompareEngine.PrepareProject(project, userGlobalLibrary2, rightModels);
				this.UpdateProgress(0, "Start Compare");
				CompareResultElement rootElement = plcSoftware.CompareTo(plcSoftware2).RootElement;
				this.UpdateProgress(0, string.Concat(new object[]
				{
					rootElement.ComparisonResult,
					" - ",
					rootElement.LeftName,
					"-",
					rootElement.RightName,
					" -- ",
					rootElement.DetailedInformation
				}));
			}
			this.UpdateProgress(0, "Finished");
		}

		private void UpdateProgress(int percent, string state)
		{
			Trace.TraceInformation(DateTime.Now + " - " + state);
			ProgressUpdate onProgressUpdate = this._onProgressUpdate;
			if (onProgressUpdate == null)
			{
				return;
			}
			onProgressUpdate(percent, state);
		}

		private static ILibrary OpenFile(ref TiaPortal portal, FileInfo fileInfo)
		{
			ILibrary result;
			try
			{
				string extension = fileInfo.Extension.ToLowerInvariant();
				if (extension.Contains(".al1"))
				{
					if (portal == null)
					{
						TiaPortalProcess tiaPortalProcess = TiaPortal.GetProcesses().FirstOrDefault<TiaPortalProcess>();
						if (tiaPortalProcess != null)
						{
							portal = tiaPortalProcess.Attach();
						}
						else
						{
							portal = new TiaPortal(TiaPortalMode.WithoutUserInterface);
						}
					}
					GlobalLibrary globalLibrary = portal.GlobalLibraries.FirstOrDefault((GlobalLibrary x) => x.Path.FullName == fileInfo.FullName);
					if (globalLibrary != null)
					{
						result = globalLibrary;
					}
					else
					{
						result = portal.GlobalLibraries.Open(fileInfo, OpenMode.ReadOnly);
					}
				}
				else
				{
					if (!extension.Contains(".ap1"))
					{
						throw new ArgumentException("Selected File is of the wrong type");
					}
					TiaPortalProcess tiaPortalProcess2 = TiaPortal.GetProcesses().FirstOrDefault(delegate(TiaPortalProcess x)
					{
						FileInfo projectPath = x.ProjectPath;
						return ((projectPath != null) ? projectPath.FullName : null) == fileInfo.FullName;
					});
					TiaPortalProcess tiaPortalProcess3 = TiaPortal.GetProcesses().FirstOrDefault((TiaPortalProcess x) => x.ProjectPath == null);
					if (tiaPortalProcess2 != null)
					{
						TiaPortal tiaPortal = portal;
						if (tiaPortal != null)
						{
							tiaPortal.Dispose();
						}
						portal = tiaPortalProcess2.Attach();
					}
					else if (portal == null)
					{
						if (tiaPortalProcess3 != null)
						{
							portal = tiaPortalProcess3.Attach();
						}
						else
						{
							portal = new TiaPortal(TiaPortalMode.WithoutUserInterface);
						}
					}
					Project project = portal.Projects.FirstOrDefault<Project>();
					if (project != null && project.Path.FullName == fileInfo.FullName)
					{
						result = project.ProjectLibrary;
					}
					else
					{
						project = portal.Projects.Open(fileInfo);
						result = project.ProjectLibrary;
					}
				}
			}
			catch (EngineeringTargetInvocationException innerException)
			{
				throw new ArgumentException("The File cannot be opened", fileInfo.FullName, innerException);
			}
			return result;
		}

		private static UserGlobalLibrary CreateNewLibrary(TiaPortal portal, string libraryName)
		{
			DirectoryInfo directoryInfo = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "LibraryCompare"));
			if (directoryInfo.Exists)
			{
				directoryInfo.Delete(true);
			}
			return portal.GlobalLibraries.Create<UserGlobalLibrary>(directoryInfo, libraryName);
		}

		private static void CloseLibrary(ILibrary library)
		{
			UserGlobalLibrary userGlobalLibrary = library as UserGlobalLibrary;
			if (userGlobalLibrary != null)
			{
				userGlobalLibrary.Close();
			}
			ProjectLibrary projectLibrary = library as ProjectLibrary;
			Project project = ((projectLibrary != null) ? projectLibrary.Parent : null) as Project;
			TiaPortal tiaPortal = ((project != null) ? project.Parent : null) as TiaPortal;
			if (tiaPortal != null && project != null && !project.IsModified)
			{
				project.Close();
			}
		}

		private static UserGlobalLibrary PrepareLibrary(TiaPortal portal, FileInfo fileInfo)
		{
			UserGlobalLibrary userGlobalLibrary;
			if (fileInfo.Extension == ".ap14")
			{
				ILibrary library = LibraryCompareEngine.OpenFile(ref portal, fileInfo);
				userGlobalLibrary = LibraryCompareEngine.CreateNewLibrary(portal, "New" + Path.GetFileNameWithoutExtension(fileInfo.FullName));
				library.UpdateLibrary(new LibraryTypeSystemFolder[]
				{
					library.TypeFolder
				}, userGlobalLibrary);
				LibraryCompareEngine.CloseLibrary(library);
			}
			else
			{
				userGlobalLibrary = (LibraryCompareEngine.OpenFile(ref portal, fileInfo) as UserGlobalLibrary);
			}
			return userGlobalLibrary;
		}

		private static PlcSoftware PrepareProject(Project project, UserGlobalLibrary library, IEnumerable<ILibraryTypeModel> models)
		{
			PlcSoftware plcSoftware = project.Devices.CreateWithItem("OrderNumber:6ES7 518-4AP00-0AB0/V2.1", library.Name, library.Name + "Station").DeviceItems[1].GetService<SoftwareContainer>().Software as PlcSoftware;
			foreach (LibraryType type in LibraryCompareEngine.GetSelectedTypes(library, models))
			{
				LibraryCompareEngine.InstantiateType(plcSoftware, type);
			}
			return plcSoftware;
		}

		private static void InstantiateType(PlcSoftware target, LibraryType type)
		{
			Stack<string> stack = new Stack<string>();
			IEngineeringObject engineeringObject = type;
			try
			{
				while (!(engineeringObject is LibraryTypeSystemFolder))
				{
					engineeringObject = engineeringObject.Parent;
					stack.Push(((LibraryTypeFolder)engineeringObject).Name);
				}
				if (type.GetType().Name == "CodeBlockLibraryType")
				{
					PlcBlockGroup plcBlockGroup = target.BlockGroup;
					while (stack.Any<string>())
					{
						string text = stack.Pop();
						plcBlockGroup = (plcBlockGroup.Groups.Find(text) ?? plcBlockGroup.Groups.Create(text));
					}
					plcBlockGroup.Blocks.CreateFrom((CodeBlockLibraryTypeVersion)type.Versions.LastOrDefault<LibraryTypeVersion>());
				}
				else if (type.GetType().Name == "PlcTypeLibraryType")
				{
					PlcTypeGroup plcTypeGroup = target.TypeGroup;
					while (stack.Any<string>())
					{
						string text2 = stack.Pop();
						plcTypeGroup = (plcTypeGroup.Groups.Find(text2) ?? plcTypeGroup.Groups.Create(text2));
					}
					plcTypeGroup.Types.CreateFrom((PlcTypeLibraryTypeVersion)type.Versions.LastOrDefault<LibraryTypeVersion>());
				}
			}
			catch (Exception arg)
			{
				Trace.TraceError(DateTime.Now + " - " + arg);
			}
		}

		private static IEnumerable<LibraryType> GetSelectedTypes(ILibrary library, IEnumerable<ILibraryTypeModel> models)
		{
			Dictionary<Guid, ILibraryTypeModel> dict = models.ToDictionary((ILibraryTypeModel model) => model.Guid);
			return from item in LibraryCompareEngine.GetAllTypes(library.TypeFolder)
			where dict.ContainsKey(item.Guid)
			select item;
		}

		private static IEnumerable<LibraryTypeModel> GetAllTypes(LibraryTypeFolder folder, string path)
		{
			path = path + "\\" + folder.Name;
			List<LibraryTypeModel> list = (from o in folder.Types
			select new LibraryTypeModel(o)
			{
				FolderPath = path
			}).ToList<LibraryTypeModel>();
			foreach (LibraryTypeUserFolder folder2 in folder.Folders)
			{
				list.AddRange(LibraryCompareEngine.GetAllTypes(folder2, path));
			}
			return list;
		}

		private static IEnumerable<LibraryType> GetAllTypes(LibraryTypeFolder folder)
		{
			List<LibraryType> list = folder.Types.ToList<LibraryType>();
			foreach (LibraryTypeUserFolder folder2 in folder.Folders)
			{
				list.AddRange(LibraryCompareEngine.GetAllTypes(folder2));
			}
			return list;
		}

		private static IList<Tuple<ILibraryTypeModel, ILibraryTypeModel>> CreateTypeRelations(IReadOnlyList<LibraryTypeModel> leftTypes, IReadOnlyList<LibraryTypeModel> rightTypes)
		{
			List<Tuple<ILibraryTypeModel, ILibraryTypeModel>> list = new List<Tuple<ILibraryTypeModel, ILibraryTypeModel>>();
			leftTypes = (from o in leftTypes
			orderby o.Guid
			select o).ToList<LibraryTypeModel>();
			rightTypes = (from o in rightTypes
			orderby o.Guid
			select o).ToList<LibraryTypeModel>();
			int num = 0;
			int i;
			for (i = 0; i < leftTypes.Count; i++)
			{
				ILibraryTypeModel item = null;
				LibraryTypeModel item2 = leftTypes[i];
				if (i + num < rightTypes.Count)
				{
					item = rightTypes[i + num];
					int num2 = leftTypes[i].Guid.CompareTo(rightTypes[i + num].Guid);
					if (num2 < 0)
					{
						item = null;
						num--;
					}
					else if (num2 > 0)
					{
						item2 = null;
						num++;
						i--;
					}
				}
				list.Add(new Tuple<ILibraryTypeModel, ILibraryTypeModel>(item2, item));
			}
			for (int j = i + num; j < rightTypes.Count; j++)
			{
				list.Add(new Tuple<ILibraryTypeModel, ILibraryTypeModel>(null, rightTypes[j]));
			}
			return list;
		}

		private static void AnalyzeTypes(IEnumerable<Tuple<ILibraryTypeModel, ILibraryTypeModel>> typePairs)
		{
			List<LibraryTypeModel> list = new List<LibraryTypeModel>();
			List<LibraryTypeModel> list2 = new List<LibraryTypeModel>();
			foreach (Tuple<ILibraryTypeModel, ILibraryTypeModel> tuple in typePairs)
			{
				if (tuple.Item1 != null)
				{
					list.Add((LibraryTypeModel)tuple.Item1);
				}
				if (tuple.Item2 != null)
				{
					list2.Add((LibraryTypeModel)tuple.Item2);
				}
			}
			LibraryCompareEngine.BrowseDependencies(list);
			LibraryCompareEngine.BrowseDependencies(list2);
			Trace.TraceInformation(DateTime.Now + " - Check for Updates");
			LibraryCompareEngine.CheckForUpdates(list, list2.ToDictionary((LibraryTypeModel o) => o.Guid));
			LibraryCompareEngine.CheckForUpdates(list2, list.ToDictionary((LibraryTypeModel o) => o.Guid));
		}

		private static void BrowseDependencies(IReadOnlyList<LibraryTypeModel> typeModels)
		{
			Dictionary<Guid, LibraryVersionModel> dictionary = (from typeModel in typeModels
			from versionModel in typeModel.Versions
			select versionModel as LibraryVersionModel).ToDictionary((LibraryVersionModel version) => version.Guid);
			foreach (LibraryTypeModel libraryTypeModel in typeModels)
			{
				foreach (ILibraryVersionModel libraryVersionModel in libraryTypeModel.Versions)
				{
					LibraryVersionModel libraryVersionModel2 = (LibraryVersionModel)libraryVersionModel;
					foreach (Guid key in libraryVersionModel2.DependencyGuids)
					{
						LibraryVersionModel item;
						if (dictionary.TryGetValue(key, out item))
						{
							libraryVersionModel.Dependencies.Add(item);
						}
					}
					foreach (Guid key2 in libraryVersionModel2.DependentGuids)
					{
						LibraryVersionModel item2;
						if (dictionary.TryGetValue(key2, out item2))
						{
							libraryVersionModel.Dependents.Add(item2);
						}
					}
				}
			}
		}

		private static void CheckForUpdates(IEnumerable<LibraryTypeModel> toCheck, IReadOnlyDictionary<Guid, LibraryTypeModel> referenzes)
		{
			foreach (LibraryTypeModel libraryTypeModel in toCheck)
			{
				foreach (ILibraryVersionModel libraryVersionModel in libraryTypeModel.Versions.Last<ILibraryVersionModel>().Dependencies)
				{
					if (libraryTypeModel.OutDated)
					{
						break;
					}
					LibraryTypeModel libraryTypeModel2;
					if (libraryVersionModel.Parent.LatestVersion > libraryVersionModel.Version || (referenzes.TryGetValue(libraryVersionModel.Parent.Guid, out libraryTypeModel2) && libraryTypeModel2.LatestVersion > libraryVersionModel.Version))
					{
						libraryTypeModel.OutDated = true;
					}
				}
			}
		}

		private static IEnumerable<LibraryCopyModel> GetAllMasterCopies(MasterCopyFolder folder, string path)
		{
			path = path + "\\" + folder.Name;
			List<LibraryCopyModel> list = (from o in folder.MasterCopies
			select new LibraryCopyModel(o)
			{
				FolderPath = path
			}).ToList<LibraryCopyModel>();
			foreach (MasterCopyUserFolder folder2 in folder.Folders)
			{
				list.AddRange(LibraryCompareEngine.GetAllMasterCopies(folder2, path));
			}
			return list;
		}

		private static IList<Tuple<ILibraryCopyModel, ILibraryCopyModel>> CreateMasterCopyRelations(IReadOnlyList<LibraryCopyModel> leftCopies, IReadOnlyList<LibraryCopyModel> rightCopies)
		{
			List<Tuple<ILibraryCopyModel, ILibraryCopyModel>> list = new List<Tuple<ILibraryCopyModel, ILibraryCopyModel>>();
			leftCopies = (from o in leftCopies
			orderby o.FolderPath, o.Name
			select o).ToList<LibraryCopyModel>();
			rightCopies = (from o in rightCopies
			orderby o.FolderPath, o.Name
			select o).ToList<LibraryCopyModel>();
			int num = 0;
			int i;
			for (i = 0; i < leftCopies.Count; i++)
			{
				ILibraryCopyModel libraryCopyModel = null;
				LibraryCopyModel libraryCopyModel2 = leftCopies[i];
				if (i + num < rightCopies.Count)
				{
					libraryCopyModel = rightCopies[i + num];
					int num2 = string.Compare(libraryCopyModel2.FolderPath, libraryCopyModel.FolderPath, StringComparison.OrdinalIgnoreCase);
					if (num2 < 0)
					{
						libraryCopyModel = null;
						num--;
					}
					else if (num2 > 0)
					{
						libraryCopyModel2 = null;
						num++;
						i--;
					}
					else
					{
						num2 = string.Compare(libraryCopyModel2.Name, libraryCopyModel.Name, StringComparison.OrdinalIgnoreCase);
						if (num2 < 0)
						{
							libraryCopyModel = null;
							num--;
						}
						else if (num2 > 0)
						{
							libraryCopyModel2 = null;
							num++;
							i--;
						}
					}
				}
				list.Add(new Tuple<ILibraryCopyModel, ILibraryCopyModel>(libraryCopyModel2, libraryCopyModel));
			}
			for (int j = i + num; j < rightCopies.Count; j++)
			{
				list.Add(new Tuple<ILibraryCopyModel, ILibraryCopyModel>(null, rightCopies[j]));
			}
			return list;
		}

		private ProgressUpdate _onProgressUpdate;
	}
}