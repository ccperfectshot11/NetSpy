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
using System.Linq;

using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: members implemented by a source generator are shown as the partial declaration the user
	/// wrote, eg. <c>[GeneratedRegex("...")] private static partial Regex PhoneRegex();</c>.
	/// The generated body (and generator-only attributes) are removed and the declaring types become partial,
	/// so an exported project builds (the generator runs again) and looks like the original source.
	/// </summary>
	public class RestoreSourceGeneratorPartials : IAstTransform
	{
		sealed class GeneratorInfo
		{
			public readonly string TriggerAttribute;
			public readonly string[] AttributesToRemove;
			public readonly bool RemoveUnsafe;

			public GeneratorInfo(string triggerAttribute, bool removeUnsafe, params string[] attributesToRemove)
			{
				TriggerAttribute = triggerAttribute;
				RemoveUnsafe = removeUnsafe;
				AttributesToRemove = attributesToRemove;
			}
		}

		const string GeneratedCodeAttribute = "System.CodeDom.Compiler.GeneratedCodeAttribute";

		/// <summary>
		/// Generator name (first argument of [GeneratedCode]) => info
		/// </summary>
		static readonly Dictionary<string, GeneratorInfo> generators = new Dictionary<string, GeneratorInfo> {
			["System.Text.RegularExpressions.Generator"] = new GeneratorInfo("System.Text.RegularExpressions.GeneratedRegexAttribute", false),
			["Microsoft.Interop.LibraryImportGenerator"] = new GeneratorInfo("System.Runtime.InteropServices.LibraryImportAttribute", true,
				"System.Runtime.CompilerServices.SkipLocalsInitAttribute"),
			["Microsoft.Extensions.Logging.Generators"] = new GeneratorInfo("Microsoft.Extensions.Logging.LoggerMessageAttribute", false),
		};

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.SourceGeneratorPartialMembers)
				return;
			foreach (var entity in rootNode.Descendants.OfType<EntityDeclaration>().ToArray())
			{
				if (entity is not (MethodDeclaration or PropertyDeclaration))
					continue;
				if (entity.GetSymbol() is not IMember member || !TryGetGenerator(member, out var info))
					continue;

				switch (entity)
				{
					case MethodDeclaration method:
						method.Body = null;
						break;
					case PropertyDeclaration property:
						property.ExpressionBody = null;
						if (property.Getter != null)
							property.Getter.Body = null;
						if (property.Setter != null)
							property.Setter.Body = null;
						break;
				}
				entity.Modifiers |= Modifiers.Partial;
				if (info.RemoveUnsafe)
					entity.Modifiers &= ~Modifiers.Unsafe;
				RemoveAttributes(entity, GeneratedCodeAttribute);
				foreach (var attr in info.AttributesToRemove)
					RemoveAttributes(entity, attr);

				// A partial member needs partial declaring types
				for (var parent = entity.Parent; parent != null; parent = parent.Parent)
				{
					if (parent is TypeDeclaration typeDecl)
						typeDecl.Modifiers |= Modifiers.Partial;
				}
			}
		}

		static bool TryGetGenerator(IMember member, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GeneratorInfo? info)
		{
			info = null;
			var attributes = member.GetAttributes().ToArray();
			foreach (var attr in attributes)
			{
				if (attr.AttributeType.FullName != GeneratedCodeAttribute || attr.FixedArguments.Length == 0)
					continue;
				if (attr.FixedArguments[0].Value is not string generatorName || !generators.TryGetValue(generatorName, out var gen))
					continue;
				if (attributes.Any(a => a.AttributeType.FullName == gen.TriggerAttribute))
				{
					info = gen;
					return true;
				}
			}
			return false;
		}

		static void RemoveAttributes(EntityDeclaration entity, string attributeFullName)
		{
			foreach (var section in entity.Attributes.ToArray())
			{
				foreach (var attr in section.Attributes.ToArray())
				{
					if (attr.Type.GetSymbol() is ITypeDefinition td && td.FullName == attributeFullName)
						attr.Remove();
				}
				if (section.Attributes.Count == 0)
					section.Remove();
			}
		}
	}
}
