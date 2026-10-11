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

using System.Linq;
using System.Text.RegularExpressions;

using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: C# 11 file-local types. "file class T" in X.cs is emitted as the internal type
	/// "&lt;X&gt;F{checksum}__T"; this names it T again and adds the 'file' modifier.
	/// </summary>
	public sealed class RestoreFileLocalTypes : IAstTransform
	{
		static readonly Regex fileLocalName = new Regex(@"^<(?<file>[^>]+)>F[0-9A-F]+__(?<name>.+)$", RegexOptions.CultureInvariant);

		/// <summary>The source file (without extension) and the declared name of a file-local type's metadata name.</summary>
		public static bool IsFileLocalName(string metadataName, out string fileName, out string name)
		{
			var match = fileLocalName.Match(metadataName);
			fileName = match.Groups["file"].Value;
			name = match.Groups["name"].Value;
			return match.Success;
		}

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.FileLocalTypes)
				return;
			foreach (var type in rootNode.Descendants.OfType<TypeDeclaration>().ToList())
			{
				if (type.GetSymbol() is not ITypeDefinition { DeclaringTypeDefinition: null } definition
					|| !IsFileLocalName(definition.Name, out _, out var name))
					continue;
				type.Modifiers = (type.Modifiers & ~Modifiers.VisibilityMask) | Modifiers.File;
				// the declaration and every reference: all spelled with the metadata name
				foreach (var identifier in rootNode.Descendants.OfType<Identifier>().Where(i => i.Name == definition.Name).ToList())
					identifier.Name = name;
			}
			// references to a file-local type declared elsewhere (a single type shown in the editor): its declared name
			foreach (var identifier in rootNode.Descendants.OfType<Identifier>().ToList())
			{
				if (IsFileLocalName(identifier.Name, out _, out var declared))
					identifier.Name = declared;
			}
		}
	}
}
