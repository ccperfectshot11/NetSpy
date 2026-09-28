/*
    Copyright (C) 2026 NetSpy contributors

    This file is part of NetSpy (a dnSpy fork)

    NetSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    NetSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with NetSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.MSBuild;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ILSpySettings = ICSharpCode.Decompiler.DecompilerSettings;

namespace dnSpy.Decompiler.ILSpy.Core.CSharp {
	/// <summary>
	/// "Export to Project" for C#: ILSpy's <see cref="WholeProjectDecompiler"/> (the same code ILSpy and
	/// ilspycmd use, so the project builds), connected to dnlib (assembly resolution, PDB names) and to
	/// NetSpy's BAML decompiler (WPF resources become .xaml pages with partial code-behind classes).
	/// </summary>
	sealed class ProjectExporter : WholeProjectDecompiler {
		readonly DecompileWholeProject data;

		ProjectExporter(ILSpySettings settings, DecompileWholeProject data)
			: base(settings, data.ProjectGuid, new DnlibAssemblyResolver(data.Module), IProjectFileWriter.FromSettings(settings),
				  CreateClassifier(data.Module), new DnlibDebugInfoProvider(data.Module)) {
			this.data = data;
		}

		/// <summary>
		/// ILSpy's resolver knows which references are part of the framework (GAC / shared framework),
		/// so they're not written as file references (HintPath) to the project file
		/// </summary>
		static IAssemblyReferenceClassifier CreateClassifier(ModuleDef module) {
			var file = DnlibMetadata.GetMetadataFile(module);
			return new UniversalAssemblyResolver(module.Location, false, file.DetectTargetFrameworkId());
		}

		public static void Export(ILSpySettings settings, DecompileWholeProject data) {
			settings = settings.Clone();
			settings.UseSdkStyleProjectFormat = data.SdkStyleProject;
			var exporter = new ProjectExporter(settings, data);
			var file = DnlibMetadata.GetMetadataFile(data.Module);
			var directory = Path.GetDirectoryName(data.ProjectFilename)!;
			Directory.CreateDirectory(directory);
			using (var writer = new StreamWriter(data.ProjectFilename))
				exporter.DecompileProject(file, directory, writer, data.CancellationToken);
			foreach (var error in exporter.Errors)
				data.Error?.Invoke(error.Message);
		}

		/// <summary>
		/// Generators whose types are created again when the exported project is built (the C# code of the
		/// user's partial members is restored by the engine's RestoreSourceGeneratorPartials transform)
		/// </summary>
		static readonly HashSet<string> regeneratedByBuild = new HashSet<string>(StringComparer.Ordinal) {
			"System.Text.RegularExpressions.Generator",
			"Microsoft.Interop.LibraryImportGenerator",
			"Microsoft.Extensions.Logging.Generators",
		};

		protected override bool IncludeTypeWhenDecompilingProject(MetadataFile module, TypeDefinitionHandle type) {
			if (!base.IncludeTypeWhenDecompilingProject(module, type))
				return false;
			if (Settings.SourceGeneratorPartialMembers && IsTypeCreatedBySourceGenerator(type))
				return false;
			return true;
		}

		bool IsTypeCreatedBySourceGenerator(TypeDefinitionHandle handle) {
			if (DnlibMetadata.ResolveToken(data.Module, handle) is not TypeDef td)
				return false;
			var ca = td.CustomAttributes.Find("System.CodeDom.Compiler.GeneratedCodeAttribute");
			if (ca is null || ca.ConstructorArguments.Count == 0)
				return false;
			var generator = ca.ConstructorArguments[0].Value as UTF8String;
			return generator is not null && regeneratedByBuild.Contains(generator.String);
		}

		protected override IEnumerable<ProjectItemInfo> WriteResourceFilesInProject(MetadataFile module) {
			if (!data.UnpackResources)
				return Array.Empty<ProjectItemInfo>();
			return base.WriteResourceFilesInProject(module);
		}

		protected override IEnumerable<ProjectItemInfo> WriteResourceToFile(string fileName, string resourceName, Stream entryStream) {
			if (data.DecompileBaml is not null && fileName.EndsWith(".baml", StringComparison.OrdinalIgnoreCase)) {
				var items = TryWriteXaml(fileName, entryStream);
				if (items is not null)
					return items;
				entryStream.Position = 0;
			}
			if (!data.CreateResX && fileName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) {
				using (var fs = new FileStream(Path.Combine(TargetDirectory, fileName), FileMode.Create, FileAccess.Write))
					entryStream.CopyTo(fs);
				return new[] { new ProjectItemInfo("EmbeddedResource", fileName).With("LogicalName", resourceName) };
			}
			return base.WriteResourceToFile(fileName, resourceName, entryStream);
		}

		/// <summary>
		/// BAML => XAML page. The x:Class type is written as a partial class without the members that
		/// the XAML compiler generates again (InitializeComponent, Connect, the named fields...)
		/// </summary>
		IEnumerable<ProjectItemInfo>? TryWriteXaml(string fileName, Stream entryStream) {
			byte[] bytes;
			using (var ms = new MemoryStream()) {
				entryStream.CopyTo(ms);
				bytes = ms.ToArray();
			}
			var module = data.Module;
			string xamlFileName = Path.ChangeExtension(fileName, ".xaml");
			try {
				using (var fs = new FileStream(Path.Combine(TargetDirectory, xamlFileName), FileMode.Create, FileAccess.Write))
					data.DecompileBaml!(module, bytes, data.CancellationToken, fs);
			}
			catch (Exception ex) when (ex is not OperationCanceledException) {
				data.Error?.Invoke($"Could not decompile BAML '{fileName}': {ex.Message}");
				return null;
			}

			var item = new ProjectItemInfo("Page", xamlFileName).With("Generator", "MSBuild:Compile").With("SubType", "Designer");
			var typeName = data.DecompileBamlTypeName?.Invoke(module, bytes, data.CancellationToken);
			var type = typeName is null ? null : module.Find(typeName, true);
			if (type is not null) {
				var partialType = new PartialTypeInfo(MetadataTokens.TypeDefinitionHandle((int)type.Rid)) {
					CompanionFileName = xamlFileName,
				};
				foreach (var member in XamlCodeBehind.GetGeneratedMembers(type))
					partialType.AddDeclaredMember(MetadataTokens.EntityHandle((int)member.MDToken.Raw));
				item.PartialTypes = new List<PartialTypeInfo> { partialType };
			}
			return new[] { item };
		}
	}
}
