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
using System.Linq;
using System.Reflection.Metadata;

using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>NetSpy: how the original wrote an accessor (from the PDB).</summary>
	public enum AccessorStyle
	{
		/// <summary>"int P => x;" (the whole get-only property/indexer)</summary>
		PropertyExpressionBody,
		/// <summary>"get => x;"</summary>
		ExpressionBody,
		/// <summary>"get { return x; }"</summary>
		SingleLineBlock,
		/// <summary>"get" with its block on the next lines</summary>
		Block
	}

	public sealed class AccessorStyleAnnotation
	{
		public readonly AccessorStyle Style;

		AccessorStyleAnnotation(AccessorStyle style) => Style = style;

		public static readonly AccessorStyleAnnotation[] All = Enum.GetValues(typeof(AccessorStyle)).Cast<AccessorStyle>()
			.Select(s => new AccessorStyleAnnotation(s)).ToArray();
	}

	/// <summary>
	/// NetSpy: the first statement of an accessor starts at a column that only depends on how it was written.
	/// With S the column of statements in the file's method bodies (the accessor keyword is at S too):
	/// "get => x;" puts x at S + "get => ".Length, "get { return x; }" puts "return" at S + "get { ".Length,
	/// a multi-line block puts it at S + 4, and "int P => x;" puts x after the declaration ("int P => ").
	/// Runs before NormalizeBlockStatements, which would otherwise turn every simple getter into "int P => x;".
	/// </summary>
	public sealed class AnnotateAccessorStyles : IAstTransform
	{
		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.SourceLayoutFromPdb || context.DecompileRun.DebugInfoProvider is not { } provider)
				return;
			foreach (var accessor in rootNode.Descendants.OfType<Accessor>().ToList())
			{
				if (accessor.Body is not { } body || accessor.GetSymbol() is not IMethod method
					|| method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition
					|| accessor.Parent is not EntityDeclaration owner || owner.GetSymbol() is not IMember { DeclaringTypeDefinition: { } type })
					continue;
				DebugInfo.SequencePoint? first;
				try
				{
					first = provider.GetSequencePoints((MethodDefinitionHandle)method.MetadataToken)
						.Where(sp => !sp.IsHidden && !ApplySourceLayout.IsBrace(sp))
						.OrderBy(sp => sp.StartLine).ThenBy(sp => sp.StartColumn).FirstOrDefault();
				}
				catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
				{
					continue;
				}
				if (first is null || ApplySourceLayout.DocumentBlockColumn(context, provider, type, first.DocumentUrl) is not int column)
					continue;
				string keyword = accessor.Kind switch {
					AccessorKind.Getter => "get",
					AccessorKind.Setter => "set",
					AccessorKind.Init => "init",
					AccessorKind.Adder => "add",
					AccessorKind.Remover => "remove",
					_ => ""
				};
				if (keyword.Length == 0)
					continue;
				string prefix = ModifiersText(accessor.Modifiers) + keyword;
				AccessorStyle? style = null;
				// the declaration's own text is the strongest evidence: checked first
				if (accessor.Kind == AccessorKind.Getter && DeclarationText(owner) is { } declaration
					&& first.StartColumn == column - 4 + declaration.Length + " => ".Length)
					style = AccessorStyle.PropertyExpressionBody;
				else if (first.StartColumn == column + prefix.Length + " => ".Length)
					style = AccessorStyle.ExpressionBody;
				else if (first.StartColumn == column + prefix.Length + " { ".Length)
					style = AccessorStyle.SingleLineBlock;
				else if (first.StartColumn == column + 4)
					style = AccessorStyle.Block;
				if (style is { } value)
					body.AddAnnotation(AccessorStyleAnnotation.All[(int)value]);
			}
		}

		/// <summary>"public int Rows", "public int this[string cell]" (what comes before "=>").</summary>
		static string? DeclarationText(EntityDeclaration owner)
		{
			string modifiers = ModifiersText(owner.Modifiers);
			switch (owner)
			{
				case PropertyDeclaration property:
					return modifiers + property.ReturnType + " " + property.Name;
				case IndexerDeclaration indexer:
					return modifiers + indexer.ReturnType + " this[" + string.Join(", ", indexer.Parameters.Select(p => p.ToString())) + "]";
				default:
					return null;
			}
		}

		/// <summary>"private ", "public static " (written in the output's order), or "".</summary>
		static string ModifiersText(Modifiers modifiers)
		{
			return string.Concat(CSharpModifiers.AllModifiers.Where(m => (modifiers & m) == m).Select(m => CSharpModifiers.GetModifierName(m) + " "));
		}

		/// <summary>The style the PDB gave the accessor body, if any.</summary>
		public static AccessorStyle? StyleOf(BlockStatement? body) => body?.Annotation<AccessorStyleAnnotation>()?.Style;
	}
}
