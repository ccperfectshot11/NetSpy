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

using System.Collections.Generic;
using System.Reflection.Metadata;

namespace ICSharpCode.Decompiler.DebugInfo
{
	public enum SourceImportKind
	{
		/// <summary>using N;</summary>
		Namespace,
		/// <summary>using static T;</summary>
		Type,
		/// <summary>using A = N;</summary>
		NamespaceAlias,
		/// <summary>using A = T;</summary>
		TypeAlias,
	}

	/// <summary>NetSpy: a using directive recorded in the PDB.</summary>
	public sealed class SourceImport
	{
		public SourceImportKind Kind { get; set; }
		public string? Alias { get; set; }
		public string? Namespace { get; set; }
		/// <summary>TypeDef, TypeRef or TypeSpec of <see cref="SourceImportKind.Type"/> / <see cref="SourceImportKind.TypeAlias"/>.</summary>
		public EntityHandle Type { get; set; }

		/// <summary>Identifies the directive (for comparing the files' lists).</summary>
		public string Key => Kind + " " + Alias + " " + Namespace + " " + (Type.IsNil ? "" : System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(Type).ToString("X8"));
	}

	/// <summary>
	/// NetSpy: the source files and using directives of the original project, from the PDB
	/// (implemented next to <see cref="IDebugInfoProvider"/> by providers that can read them).
	/// </summary>
	public interface ISourceImportProvider
	{
		/// <summary>All source documents of the compilation (also those without code, like GlobalUsings.cs).</summary>
		IReadOnlyList<string> SourceDocuments { get; }

		/// <summary>
		/// The using directives at the top of the file of <paramref name="method"/> (the import scope right
		/// below the compilation's root scope), with an id of that scope; null if the PDB doesn't say.
		/// </summary>
		(int ScopeId, IReadOnlyList<SourceImport> Imports)? GetFileImports(MethodDefinitionHandle method);
	}
}
