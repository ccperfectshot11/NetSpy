// Copyright (c) 2026 NetSpy contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;

using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.DebugInfo;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.ProjectDecompiler
{
	/// <summary>
	/// NetSpy: the using directives of the original files, from the PDB's import scopes:
	/// global usings (in every file's scope) go to their own file again (GlobalUsings.cs),
	/// every file gets its own directives in their original order.
	/// </summary>
	sealed class SourceImports
	{
		/// <summary>The global usings (empty if there were none, or if it can't be told).</summary>
		public List<SourceImport> Global { get; } = new List<SourceImport>();

		/// <summary>Project file name → its own using directives.</summary>
		public Dictionary<string, List<SourceImport>> Files { get; } = new Dictionary<string, List<SourceImport>>(StringComparer.OrdinalIgnoreCase);

		/// <summary>The file the global usings are written to.</summary>
		public string GlobalFileName { get; private set; } = "GlobalUsings.cs";

		public static SourceImports? Compute(MetadataReader metadata, ISourceImportProvider provider,
			Dictionary<TypeDefinitionHandle, (string FileName, int Line)> sourceFiles)
		{
			var result = new SourceImports();
			// every file-level scope of the compilation (the generated files' scopes are just the global usings)
			var scopes = new Dictionary<int, IReadOnlyList<SourceImport>>();
			foreach (var method in metadata.MethodDefinitions)
			{
				if (Get(provider, method) is { } scope)
					scopes[scope.ScopeId] = scope.Imports;
			}
			if (scopes.Count == 0)
				return null;
			if (scopes.Count > 1)
			{
				var common = new HashSet<string>(scopes.Values.First().Select(i => i.Key));
				foreach (var imports in scopes.Values.Skip(1))
					common.IntersectWith(imports.Select(i => i.Key));
				result.Global.AddRange(scopes.Values.First().Where(i => common.Contains(i.Key)));
			}
			var globalKeys = new HashSet<string>(result.Global.Select(i => i.Key));
			// the scope of each project file: that of the methods of its types
			foreach (var group in sourceFiles.GroupBy(t => t.Value.FileName, StringComparer.OrdinalIgnoreCase))
			{
				var imports = group.SelectMany(t => AllMethods(metadata, t.Key)).Select(m => Get(provider, m)).FirstOrDefault(s => s != null);
				if (imports is { } found)
					result.Files[group.Key] = found.Imports.Where(i => !globalKeys.Contains(i.Key)).ToList();
			}
			// the global usings' own file: a source document without types ("GlobalUsings.cs")
			var documents = provider.SourceDocuments.Where(d => !SourceFileLayout.IsGenerated(d) && d.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
			var root = SourceFileLayout.CommonDirectory(documents);
			if (root != null)
			{
				var withoutTypes = documents.Select(d => d.Substring(root.Length).TrimStart('\\', '/').Replace('/', Path.DirectorySeparatorChar))
					.Where(name => !result.Files.ContainsKey(name) && !sourceFiles.Values.Any(v => string.Equals(v.FileName, name, StringComparison.OrdinalIgnoreCase)))
					.ToList();
				result.GlobalFileName = withoutTypes.FirstOrDefault(n => n.IndexOf("using", StringComparison.OrdinalIgnoreCase) >= 0)
					?? (withoutTypes.Count == 1 ? withoutTypes[0] : result.GlobalFileName);
			}
			return result;
		}

		static (int ScopeId, IReadOnlyList<SourceImport> Imports)? Get(ISourceImportProvider provider, MethodDefinitionHandle method)
		{
			try
			{
				return provider.GetFileImports(method);
			}
			catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
			{
				return null;
			}
		}

		static IEnumerable<MethodDefinitionHandle> AllMethods(MetadataReader metadata, TypeDefinitionHandle type)
		{
			var definition = metadata.GetTypeDefinition(type);
			foreach (var method in definition.GetMethods())
				yield return method;
			foreach (var nested in definition.GetNestedTypes())
			{
				foreach (var method in AllMethods(metadata, nested))
					yield return method;
			}
		}

		/// <summary>
		/// Replaces the usings the decompiler computed with the file's original ones; the namespaces the decompiled
		/// code needs and that neither those nor the global usings import are kept (so it always compiles).
		/// </summary>
		public void Apply(SyntaxTree syntaxTree, string fileName, MetadataModule module)
		{
			if (!Files.TryGetValue(fileName, out var own))
				return;
			var existing = syntaxTree.Children.Where(n => n is UsingDeclaration or UsingAliasDeclaration).ToList();
			var imported = new HashSet<string>(Global.Concat(own).Where(i => i.Kind == SourceImportKind.Namespace).Select(i => i.Namespace!));
			var aliases = new HashSet<string>(Global.Concat(own).Where(i => i.Alias != null).Select(i => i.Alias!));
			var directives = own.Select(i => CreateDirective(i, module)).OfType<AstNode>().ToList();
			foreach (var node in existing)
			{
				node.Remove();
				if (node is UsingDeclaration { IsStatic: false } u && !imported.Contains(u.Namespace))
					directives.Add(node);
				else if (node is UsingAliasDeclaration a && !aliases.Contains(a.Alias))
					directives.Add(node);
			}
			AstNode? insertionPoint = syntaxTree.Children.FirstOrDefault(n => n is ExternAliasDeclaration);
			foreach (var directive in directives)
			{
				if (insertionPoint is null)
					syntaxTree.InsertChildBefore(syntaxTree.FirstChild, directive, Slots.Member);
				else
					syntaxTree.InsertChildAfter(insertionPoint, directive, Slots.Member);
				insertionPoint = directive;
			}
		}

		/// <summary>The text of the global usings file.</summary>
		public string GetGlobalUsingsText(MetadataModule module)
		{
			var text = new StringBuilder();
			foreach (var import in Global)
			{
				var directive = CreateDirective(import, module);
				if (directive != null)
					text.Append("global ").Append(directive.ToString().Trim()).AppendLine();
			}
			return text.ToString();
		}

		static AstNode? CreateDirective(SourceImport import, MetadataModule module)
		{
			switch (import.Kind)
			{
				case SourceImportKind.Namespace:
					return import.Namespace is null ? null : new UsingDeclaration(import.Namespace);
				case SourceImportKind.NamespaceAlias:
					return import.Namespace is null || import.Alias is null ? null
						: new UsingAliasDeclaration { Alias = import.Alias, Import = AstType.Create(import.Namespace) };
				case SourceImportKind.Type:
				case SourceImportKind.TypeAlias:
					if (import.Type.IsNil)
						return null;
					IType type;
					try
					{
						type = module.ResolveType(import.Type, default);
					}
					catch (BadImageFormatException)
					{
						return null;
					}
					// "using static string;" is invalid: "using static System.String;"
					var syntax = new TypeSystemAstBuilder { UseKeywordsForBuiltinTypes = import.Kind != SourceImportKind.Type }.ConvertType(type);
					if (import.Kind == SourceImportKind.Type)
						return new UsingDeclaration { Import = syntax, IsStatic = true };
					return import.Alias is null ? null : new UsingAliasDeclaration { Alias = import.Alias, Import = syntax };
				default:
					return null;
			}
		}
	}
}
