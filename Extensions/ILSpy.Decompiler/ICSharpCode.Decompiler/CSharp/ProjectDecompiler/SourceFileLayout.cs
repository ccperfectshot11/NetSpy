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
using System.Reflection.Metadata.Ecma335;

using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Metadata;

using SequencePoint = ICSharpCode.Decompiler.DebugInfo.SequencePoint;

namespace ICSharpCode.Decompiler.CSharp.ProjectDecompiler
{
	/// <summary>
	/// NetSpy: puts the types of an exported project back into the source files they came from
	/// (several types per file, in their original order), using the PDB's document of each method.
	/// </summary>
	static class SourceFileLayout
	{
		/// <summary>
		/// Top-level type → (file name relative to the project, first line in that file).
		/// Types whose file can't be determined are missing (they get the usual one-file-per-type name).
		/// </summary>
		public static Dictionary<TypeDefinitionHandle, (string FileName, int Line)> Compute(MetadataReader metadata, IDebugInfoProvider provider, out string? root)
		{
			var documents = new Dictionary<TypeDefinitionHandle, (string Document, int Line)>();
			var types = metadata.GetTopLevelTypeDefinitions().ToList();
			foreach (var type in types)
			{
				var points = new List<SequencePoint>();
				CollectSequencePoints(metadata, type, provider, points);
				// the file named after the type (Form1.cs, not Form1.Designer.cs), else the one with the most code
				string typeName = metadata.GetString(metadata.GetTypeDefinition(type).Name).Split('`')[0];
				var byDocument = points.Where(p => !IsGenerated(p.DocumentUrl))
					.GroupBy(p => p.DocumentUrl, StringComparer.OrdinalIgnoreCase)
					.OrderByDescending(g => string.Equals(Path.GetFileName(g.Key), typeName + ".cs", StringComparison.OrdinalIgnoreCase))
					.ThenByDescending(g => g.Count())
					.FirstOrDefault();
				if (byDocument != null)
					documents[type] = (byDocument.Key, byDocument.Min(p => p.StartLine));
			}
			// Types without code (enums, interfaces, delegates...) have no sequence points. Compilers emit the
			// types of a file together and in order: a type between two types of the same file is in that file.
			var ordered = types.OrderBy(t => MetadataTokens.GetRowNumber(t)).ToList();
			for (int i = 0; i < ordered.Count; i++)
			{
				if (documents.ContainsKey(ordered[i]))
					continue;
				var previous = ordered.Take(i).LastOrDefault(documents.ContainsKey);
				var next = ordered.Skip(i + 1).FirstOrDefault(documents.ContainsKey);
				if (!previous.IsNil && !next.IsNil && string.Equals(documents[previous].Document, documents[next].Document, StringComparison.OrdinalIgnoreCase))
					documents[ordered[i]] = (documents[previous].Document, documents[previous].Line + 1);
				// at the border of two files: the file of the neighbor that derives from it ("interface I; class C : I")
				else if (!next.IsNil && ordered.Skip(i + 1).TakeWhile(t => !documents.ContainsKey(t) || t == next).Any(t => Inherits(metadata, t, ordered[i])))
					documents[ordered[i]] = (documents[next].Document, documents[next].Line - 1);
				else if (!previous.IsNil && ordered.Take(i).Reverse().TakeWhile(t => !documents.ContainsKey(t) || t == previous).Any(t => Inherits(metadata, t, ordered[i])))
					documents[ordered[i]] = (documents[previous].Document, documents[previous].Line + 1);
			}
			root = CommonDirectory(documents.Values.Select(d => d.Document).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
			var result = new Dictionary<TypeDefinitionHandle, (string, int)>();
			if (root == null)
				return result;
			foreach (var pair in documents)
			{
				var relative = pair.Value.Document.Substring(root.Length).TrimStart('\\', '/');
				if (relative.Length > 0 && relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
					result[pair.Key] = (relative.Replace('/', Path.DirectorySeparatorChar), pair.Value.Line);
			}
			return result;
		}

		/// <summary>
		/// The indentation and namespace style of each source file. Statements of the methods of top-level types
		/// start at column 1 + (indentation × levels): 2 levels with "namespace X;", 3 with "namespace X { }".
		/// </summary>
		public static Dictionary<string, (string Indentation, bool FileScopedNamespace)> ComputeStyles(MetadataReader metadata,
			IDebugInfoProvider provider, Dictionary<TypeDefinitionHandle, (string FileName, int Line)> types, string? root)
		{
			var columns = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
			foreach (var pair in types)
			{
				foreach (var method in metadata.GetTypeDefinition(pair.Key).GetMethods())
				{
					bool isConstructor = metadata.GetString(metadata.GetMethodDefinition(method).Name) is ".ctor" or ".cctor";
					List<SequencePoint> points;
					try
					{
						points = provider.GetSequencePoints(method).Where(p => !p.IsHidden).ToList();
					}
					catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
					{
						continue;
					}
					// constructors start with the field initializers, whose points begin at the member's column in structs
					if (isConstructor)
						continue;
					if (points.Count < 2)
						continue; // one point may be an expression body ("=> x")
					// per source file: a partial type's methods can be in several (Form1.Designer.cs)
					var document = points.GroupBy(p => p.DocumentUrl, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First().Key;
					var file = root != null && document.StartsWith(root, StringComparison.OrdinalIgnoreCase)
						? document.Substring(root.Length).TrimStart('\\', '/').Replace('/', Path.DirectorySeparatorChar)
						: pair.Value.FileName;
					if (!columns.TryGetValue(file, out var list))
						columns[file] = list = new List<int>();
					// (a one-character point is a brace: "}" of a void method is at the member's column)
					if (points.Any(p => !IsBrace(p)))
						list.Add(points.Where(p => !IsBrace(p)).Min(p => p.StartColumn));
				}
			}
			var result = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
			foreach (var pair in columns)
			{
				int indentation = pair.Value.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key - 1;
				(string, bool)? style = indentation switch {
					8 => ("    ", true),
					12 => ("    ", false),
					2 => ("\t", true),
					3 => ("\t", false),
					4 => ("  ", true),
					6 => ("  ", false),
					_ => null
				};
				if (style is { } s)
					result[pair.Key] = s;
			}
			// files without enough code to tell (key ""): the project's most common style
			if (result.Count > 0)
				result[""] = result.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
			return result;
		}

		/// <summary>A sequence point of a brace ("}" of a void method, "{" in debug builds).</summary>
		internal static bool IsBrace(DebugInfo.SequencePoint point) => point.StartLine == point.EndLine && point.EndColumn - point.StartColumn == 1;

		/// <summary>Does <paramref name="type"/> have <paramref name="baseType"/> as base type or interface (also generic: I&lt;T&gt;)?</summary>
		static bool Inherits(MetadataReader metadata, TypeDefinitionHandle type, TypeDefinitionHandle baseType)
		{
			var definition = metadata.GetTypeDefinition(type);
			var bases = definition.GetInterfaceImplementations().Select(i => metadata.GetInterfaceImplementation(i).Interface).ToList();
			if (!definition.BaseType.IsNil)
				bases.Add(definition.BaseType);
			foreach (var handle in bases)
			{
				var definitionHandle = handle;
				if (handle.Kind == HandleKind.TypeSpecification)
				{
					// GENERICINST (CLASS | VALUETYPE) TypeDefOrRef ...
					var reader = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
					if (reader.ReadByte() != 0x15)
						continue;
					reader.ReadByte();
					definitionHandle = reader.ReadTypeHandle();
				}
				if (definitionHandle.Kind == HandleKind.TypeDefinition && (TypeDefinitionHandle)definitionHandle == baseType)
					return true;
			}
			return false;
		}

		static void CollectSequencePoints(MetadataReader metadata, TypeDefinitionHandle handle, IDebugInfoProvider provider, List<SequencePoint> points)
		{
			var type = metadata.GetTypeDefinition(handle);
			foreach (var method in type.GetMethods())
			{
				try
				{
					points.AddRange(provider.GetSequencePoints(method).Where(p => !p.IsHidden && !string.IsNullOrEmpty(p.DocumentUrl)));
				}
				catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
				{
					// a broken body or PDB entry only costs this method's position
				}
			}
			foreach (var nested in type.GetNestedTypes())
				CollectSequencePoints(metadata, nested, provider, points);
		}

		/// <summary>Source generator output and other files under obj\ aren't part of the project's sources.</summary>
		internal static bool IsGenerated(string document)
		{
			var parts = document.Split('\\', '/');
			return parts.Any(p => p.Equals("obj", StringComparison.OrdinalIgnoreCase))
				|| document.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
				|| !Path.IsPathRooted(document);
		}

		/// <summary>The deepest directory containing all the documents (with a trailing separator), or null.</summary>
		internal static string? CommonDirectory(List<string> documents)
		{
			if (documents.Count == 0)
				return null;
			var common = Path.GetDirectoryName(documents[0]);
			foreach (var document in documents.Skip(1))
			{
				while (common != null && !document.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
					&& !document.StartsWith(common + "/", StringComparison.Ordinal))
				{
					common = Path.GetDirectoryName(common);
				}
			}
			return string.IsNullOrEmpty(common) ? null : common;
		}
	}
}
