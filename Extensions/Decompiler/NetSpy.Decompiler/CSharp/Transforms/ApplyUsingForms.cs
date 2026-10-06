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

using NetSpy.Decompiler.CSharp.Syntax;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: "using (var r = ...) { }" and "using var r = ...;" compile alike (when the using ends the block).
	/// The resource's sequence point tells them apart: in the statement form it starts inside the parentheses
	/// ("var r = ..."), in the declaration form at the statement itself ("using var r = ...;").
	/// </summary>
	public sealed class ApplyUsingForms : IAstTransform
	{
		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.SourceLayoutFromPdb || context.DecompileRun.DebugInfoProvider is null)
				return;
			foreach (var usingStatement in rootNode.Descendants.OfType<UsingStatement>().ToList())
			{
				if (usingStatement.Annotation<SourceLayoutAnnotation>() is not { } layout || StatementColumn(usingStatement) is not int column)
					continue;
				string keyword = usingStatement.IsAsync ? "await using" : "using";
				if (layout.StartColumn == column + (keyword + " (").Length)
				{
					if (usingStatement.IsEnhanced)
					{
						context.Step("Using statement (PDB)", usingStatement);
						usingStatement.IsEnhanced = false;
					}
				}
				else if (layout.StartColumn == column && !usingStatement.IsEnhanced && context.Settings.UseEnhancedUsing
					&& usingStatement.ResourceAcquisition is VariableDeclarationStatement
					&& usingStatement.Parent is BlockStatement && usingStatement.GetNextNonEmptyStatement() is null)
				{
					context.Step("Using declaration (PDB)", usingStatement);
					usingStatement.IsEnhanced = true;
				}
			}
			foreach (var usingStatement in rootNode.Descendants.OfType<UsingStatement>().ToList())
				StackUsings(usingStatement, context);
		}

		/// <summary>
		/// "using (a) { using (b) { ... } }" whose inner header starts at the outer one's column was written stacked:
		/// "using (a)\nusing (b)\n{ ... }".
		/// </summary>
		static void StackUsings(UsingStatement outer, TransformContext context)
		{
			if (outer.IsEnhanced || outer.EmbeddedStatement is not BlockStatement { Statements.Count: 1 } block
				|| block.Statements.Single() is not UsingStatement { IsEnhanced: false } inner
				|| outer.Annotation<SourceLayoutAnnotation>() is not { } outerLayout || inner.Annotation<SourceLayoutAnnotation>() is not { } innerLayout
				|| innerLayout.StartColumn != outerLayout.StartColumn || innerLayout.StartLine <= outerLayout.StartLine)
				return;
			context.Step("Stack usings", outer);
			outer.EmbeddedStatement = inner.Detach();
		}

		/// <summary>The column the statements of the using's block start at (from its siblings, or from its own body).</summary>
		static int? StatementColumn(UsingStatement usingStatement)
		{
			if (usingStatement.Parent is BlockStatement block)
			{
				var sibling = block.Statements.Where(s => s != usingStatement && s is not UsingStatement)
					.Select(s => s.Annotation<SourceLayoutAnnotation>()).FirstOrDefault(l => l is not null);
				if (sibling is not null)
					return sibling.StartColumn;
			}
			if (usingStatement.EmbeddedStatement is BlockStatement body
				&& body.Statements.Select(s => s.Annotation<SourceLayoutAnnotation>()).FirstOrDefault(l => l is not null) is { } inner)
				return inner.StartColumn - 4;
			return null;
		}
	}
}
