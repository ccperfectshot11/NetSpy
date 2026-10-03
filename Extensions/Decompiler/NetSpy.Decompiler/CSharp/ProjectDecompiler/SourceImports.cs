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

using NetSpy.Decompiler.CSharp.OutputVisitor;
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

		/// <summary>Project file name â†’ its own using directives.</summary>
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
		public void Apply(SyntaxTree syntaxTree, string fileName, MetadataModule module, CSharpFormattingOptions formattingOptions)
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
			UseTypeAliases(syntaxTree, Global.Concat(own).Where(i => i.Kind == SourceImportKind.TypeAlias && i.Alias != null), module,
				directives.OfType<UsingAliasDeclaration>().ToList(), formattingOptions);
		}

		/// <summary>
		/// NetSpy: writes "using" type aliases back where the source used them. The PDB keeps no trace of a type's
		/// spelling, except through widths: a statement's sequence point is exactly as wide as its source text, so
		/// "IntList x = ...;" and "List&lt;int&gt; x = ...;" can be told apart. In signatures (no sequence points) only a
		/// tuple alias is used, when the tuple has the alias's element names (which the directive gets too: the
		/// PDB's alias target is the plain ValueTuple).
		/// </summary>
		static void UseTypeAliases(SyntaxTree syntaxTree, IEnumerable<SourceImport> imports, MetadataModule module, List<UsingAliasDeclaration> ownDirectives,
			CSharpFormattingOptions formattingOptions)
		{
			var aliases = new List<(string Name, IType Type)>();
			foreach (var import in imports)
			{
				try
				{
					var type = module.ResolveType(import.Type, default);
					if (type.Kind != TypeKind.Unknown)
						aliases.Add((import.Alias!, type));
				}
				catch (BadImageFormatException)
				{
				}
			}
			if (aliases.Count == 0)
				return;
			var candidates = syntaxTree.Descendants.OfType<AstType>()
				.Where(t => !t.Ancestors.Any(a => a is UsingAliasDeclaration or UsingDeclaration) && t.Parent is not AstType && t.Parent is not TupleTypeElement)
				.Select(t => (Node: t, Type: t.GetResolveResult()?.Type))
				.Where(c => c.Type is { Kind: not TypeKind.Unknown })
				.ToList();
			foreach (var (aliasName, aliasType) in aliases)
			{
				var matches = candidates.Where(c => NormalizeTypeVisitor.TypeErasure.EquivalentTypes(c.Type!, aliasType)).Select(c => c.Node).ToList();
				// a tuple alias: the element names the signatures use
				string?[]? names = null;
				if (aliasType.Kind == TypeKind.Tuple)
				{
					var nameSets = matches.OfType<TupleAstType>().Select(t => t.Elements.Select(e => e.Name).ToArray())
						.Where(n => n.All(x => x != null)).Select(n => string.Join(",", n)).Distinct().ToList();
					if (nameSets.Count == 1)
					{
						names = nameSets[0].Split(',');
						if (ownDirectives.FirstOrDefault(d => d.Alias == aliasName)?.Import is TupleAstType directiveTuple
							&& directiveTuple.Elements.Count == names.Length)
						{
							int i = 0;
							foreach (var element in directiveTuple.Elements)
								element.Name = names[i++];
						}
					}
				}
				foreach (var node in matches)
				{
					if (node.Parent is null)
						continue;
					var statement = node.Ancestors.OfType<Statement>().FirstOrDefault(s => s.Annotation<Transforms.SourceLayoutAnnotation>() is not null);
					if (statement?.Annotation<Transforms.SourceLayoutAnnotation>() is { Width: > 0 } layout
						&& !node.Ancestors.Any(a => a is LambdaExpression or AnonymousMethodExpression))
					{
						// replace, and keep it only if the statement now has the source's width (and didn't before)
						int before = Width(statement, formattingOptions);
						// equal widths ("(int, int) p" / "Coordinate p"): only a named tuple alias is preferred
						if (before == layout.Width && !(names is not null && node is TupleAstType unnamed && unnamed.Elements.All(e => e.Name is null)))
							continue;
						var alias = new SimpleType(aliasName);
						alias.CopyAnnotationsFrom(node);
						node.ReplaceWith(alias);
						if (Width(statement, formattingOptions) != layout.Width)
							alias.ReplaceWith(node);
					}
					else if (names is not null && statement is null && node is TupleAstType tuple
						&& tuple.Elements.Select(e => e.Name).SequenceEqual(names))
					{
						var alias = new SimpleType(aliasName);
						alias.CopyAnnotationsFrom(node);
						node.ReplaceWith(alias);
					}
				}
			}
		}

		/// <summary>The length of a statement written on one line with the output's formatting (-1 if it takes several).</summary>
		static int Width(Statement statement, CSharpFormattingOptions formattingOptions)
		{
			var text = statement.ToString(formattingOptions).Trim();
			return text.IndexOf('\n') >= 0 ? -1 : text.Length;
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
