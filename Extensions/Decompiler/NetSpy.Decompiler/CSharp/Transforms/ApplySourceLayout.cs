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
using System.Linq;
using System.Reflection.Metadata;

using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.IL;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: uses the PDB's sequence points (source line/column of every statement) to lay the code out
	/// like the original: blank lines between statements, statements written on one line, and
	/// "=> expr" vs "{ ... }" bodies. Without a PDB nothing changes (the style heuristics apply).
	/// </summary>
	public sealed class ApplySourceLayout : IAstTransform
	{
		static readonly bool traceEnabled = System.Environment.GetEnvironmentVariable("NETSPY_DEBUG_LAYOUT") == "1";

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.SourceLayoutFromPdb || context.DecompileRun.DebugInfoProvider is not { } provider)
				return;
			var mainModule = context.TypeSystem.MainModule;
			var bodies = new List<(BlockStatement Body, List<DebugInfo.SequencePoint> SequencePoints)>();
			foreach (var entity in rootNode.DescendantsAndSelf.OfType<EntityDeclaration>())
			{
				var body = entity switch {
					MethodDeclaration m => m.Body,
					ConstructorDeclaration c => c.Body,
					DestructorDeclaration d => d.Body,
					OperatorDeclaration o => o.Body,
					Accessor a => a.Body,
					_ => null
				};
				if (body is null || entity.GetSymbol() is not IMethod declaredMethod)
					continue;
				// async methods and iterators: the statements (and their sequence points) are in the state machine's MoveNext
				var method = entity.Annotation<ILFunction>()?.MoveNextMethod ?? declaredMethod;
				if (method.ParentModule != mainModule || method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition)
					continue;
				var sequencePoints = provider.GetSequencePoints((MethodDefinitionHandle)method.MetadataToken).Where(sp => !sp.IsHidden).ToList();
				if (sequencePoints.Count == 0)
					continue;
				AnnotateStatements(body, sequencePoints);
				bodies.Add((body, sequencePoints));
			}
			DecideBodyStyles(bodies, context, provider);
		}

		static void AnnotateStatements(BlockStatement body, List<DebugInfo.SequencePoint> sequencePoints)
		{
			foreach (var block in body.DescendantsAndSelf.OfType<BlockStatement>())
			{
				if (IsInOtherFunction(block, body))
					continue;
				SourceLayoutAnnotation? previous = null;
				Statement? previousStatement = null;
				foreach (var statement in block.Statements)
				{
					var layout = GetLayout(statement, body, sequencePoints);
					if (traceEnabled)
						System.Console.Error.WriteLine($"[layout] {(layout is null ? "?" : $"{layout.StartLine}:{layout.StartColumn}-{layout.EndLine}")} {statement.ToString().Split('\n')[0].Trim()}");
					if (layout is not null)
					{
						statement.AddAnnotation(layout);
						if (previous is not null)
						{
							// lines between the two statements; a statement ending in a block also has its '}' line
							int between = layout.StartLine - previous.EndLine - 1;
							between -= TrailingBraces(previousStatement!);
							layout.BlankLineBefore = between > 0;
						}
					}
					previous = layout;
					previousStatement = statement;
				}
			}
		}

		/// <summary>The source lines of a statement: the sequence points of the IL it was decompiled from.</summary>
		static SourceLayoutAnnotation? GetLayout(Statement statement, BlockStatement body, List<DebugInfo.SequencePoint> sequencePoints)
		{
			var ranges = statement.DescendantsAndSelf
				.Where(n => !IsInOtherFunction(n, body))
				.SelectMany(n => n.Annotations.OfType<ILInstruction>())
				.SelectMany(i => i.ILRanges)
				.Where(r => !r.IsEmpty)
				.ToList();
			if (ranges.Count == 0)
				return null;
			// the IL of a sequence point is [Offset, EndOffset); some of its instructions (a ldloc inlined into its
			// user) carry no annotation, so any overlap with the statement's IL counts
			var points = sequencePoints.Where(sp => !IsBrace(sp) && ranges.Any(r => r.Start < sp.EndOffset && sp.Offset <= r.InclusiveEnd)).ToList();
			if (points.Count == 0)
				return null;
			var first = points.OrderBy(sp => sp.StartLine).ThenBy(sp => sp.StartColumn).First();
			var layout = new SourceLayoutAnnotation {
				StartLine = first.StartLine,
				StartColumn = first.StartColumn,
				EndLine = points.Max(sp => sp.EndLine),
				IsSingleLine = points.All(sp => sp.StartLine == sp.EndLine),
				IsMultiLine = points.Count == 1 && points[0].StartLine != points[0].EndLine,
				Document = first.DocumentUrl,
				Width = first.StartLine == first.EndLine ? first.EndColumn - first.StartColumn : -1
			};
			if (statement is SwitchStatement switchStatement)
				AdjustSwitchLines(switchStatement, layout, body, sequencePoints);
			if (statement is ForeachStatement foreachStatement)
			{
				// the iteration variable's own sequence point ("var x") ends right before the one of "in"
				var line = sequencePoints.Where(sp => sp.StartLine == first.StartLine && sp.EndLine == first.StartLine).ToList();
				var inKeyword = line.Where(sp => sp.EndColumn - sp.StartColumn == 2).ToList();
				var variable = line.Where(sp => inKeyword.Any(k => k.StartColumn == sp.EndColumn + 1)).ToList();
				if (variable.Count == 1)
					layout.ForeachVariableWidth = variable[0].EndColumn - variable[0].StartColumn;
				// the collection's own sequence point starts right after "in "
				if (inKeyword.Count == 1 && line.FirstOrDefault(sp => sp.StartColumn == inKeyword[0].EndColumn + 1) is { } collection)
					layout.ForeachExpressionWidth = collection.EndColumn - collection.StartColumn;
			}
			return layout;
		}

		/// <summary>
		/// NetSpy: an optimized build has no sequence point for "switch (x)" and none for the "break;" that ends the
		/// last section (it falls through to the code after the switch): the points only cover the sections' statements.
		/// With the usual layout ("switch (x)", "{", one line per case label, the statements) the header is
		/// <c>labels + 2</c> lines above the first statement, and the last "break;" one line below the last point.
		/// </summary>
		static void AdjustSwitchLines(SwitchStatement switchStatement, SourceLayoutAnnotation layout, BlockStatement body, List<DebugInfo.SequencePoint> sequencePoints)
		{
			var sections = switchStatement.SwitchSections
				.Select(s => (Section: s, First: s.Statements.FirstOrDefault() is { } f ? GetLayout(f, body, sequencePoints) : null))
				.ToList();
			// sections in braces ("case 1: { ... }") have other line counts
			if (sections.Count == 0 || sections.Any(s => s.First is null || s.Section.Statements.Any(st => st is BlockStatement)))
				return;
			var headerRanges = switchStatement.Expression.DescendantsAndSelf
				.SelectMany(n => n.Annotations.OfType<ILInstruction>())
				.SelectMany(i => i.ILRanges)
				.Where(r => !r.IsEmpty)
				.ToList();
			bool hasHeaderPoint = sequencePoints.Any(sp => !IsBrace(sp) && headerRanges.Any(r => r.Start < sp.EndOffset && sp.Offset <= r.InclusiveEnd));
			var firstSection = sections.OrderBy(s => s.First!.StartLine).First();
			if (!hasHeaderPoint && firstSection.First!.StartLine == layout.StartLine)
				layout.StartLine -= firstSection.Section.CaseLabels.Count + 2;
			var lastSection = sections.OrderBy(s => s.First!.StartLine).Last().Section;
			if (lastSection.Statements.LastOrDefault() is BreakStatement lastBreak && GetLayout(lastBreak, body, sequencePoints) is null)
				layout.EndLine++;
		}

		/// <summary>
		/// NetSpy: the source line a statement starts on (from its method's sequence points), for transforms that run
		/// before this one; -1 if unknown.
		/// </summary>
		internal static int StatementLine(Statement statement, TransformContext context)
		{
			if (!context.Settings.SourceLayoutFromPdb || context.DecompileRun.DebugInfoProvider is not { } provider)
				return -1;
			var entity = statement.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
			var body = statement.Ancestors.OfType<BlockStatement>().LastOrDefault();
			var method = entity?.Annotation<ILFunction>()?.MoveNextMethod ?? entity?.GetSymbol() as IMethod;
			if (body is null || method is null || method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition)
				return -1;
			try
			{
				var points = provider.GetSequencePoints((MethodDefinitionHandle)method.MetadataToken).Where(sp => !sp.IsHidden).ToList();
				return GetLayout(statement, body, points)?.StartLine ?? -1;
			}
			catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
			{
				return -1;
			}
		}

		/// <summary>NetSpy: the first source line of a lambda's own method; -1 if unknown.</summary>
		internal static int LambdaLine(AstNode lambda, TransformContext context)
		{
			if (context.DecompileRun.DebugInfoProvider is not { } provider || lambda.Annotation<ILFunction>()?.Method is not { } method
				|| method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition)
				return -1;
			try
			{
				var points = provider.GetSequencePoints((MethodDefinitionHandle)method.MetadataToken).Where(sp => !sp.IsHidden).ToList();
				return points.Count == 0 ? -1 : points.Min(sp => sp.StartLine);
			}
			catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
			{
				return -1;
			}
		}

		/// <summary>Nodes inside lambdas and local functions are other methods, with other sequence points.</summary>
		static bool IsInOtherFunction(AstNode node, AstNode body)
		{
			for (var n = node; n != null && n != body; n = n.Parent)
			{
				if (n is LambdaExpression or AnonymousMethodExpression or LocalFunctionDeclarationStatement)
					return true;
			}
			return false;
		}

		/// <summary>
		/// NetSpy: the "}" lines after a statement's last sequence point: one per block it ends with, nested ones included
		/// ("if (c) { foreach (...) { x; } }" ends with two).
		/// </summary>
		static int TrailingBraces(Statement statement)
		{
			if (!EndsWithBlock(statement))
				return 0;
			// the block the statement ends with, and its last statement
			Statement? last = statement switch {
				IfElseStatement ifElse => ifElse.FalseStatement ?? ifElse.TrueStatement,
				ForStatement f => f.EmbeddedStatement,
				ForeachStatement f => f.EmbeddedStatement,
				WhileStatement w => w.EmbeddedStatement,
				UsingStatement u => u.EmbeddedStatement,
				LockStatement l => l.EmbeddedStatement,
				FixedStatement f => f.EmbeddedStatement,
				CheckedStatement c => c.Body,
				UncheckedStatement u => u.Body,
				UnsafeStatement u => u.Body,
				BlockStatement b => b,
				_ => null
			};
			if (last is IfElseStatement elseIf)
				return TrailingBraces(elseIf);
			if (last is BlockStatement block && block.Statements.LastOrDefault() is { } inner && inner is not (TryCatchStatement or SwitchStatement))
				return 1 + TrailingBraces(inner);
			return 1;
		}

		static bool EndsWithBlock(Statement statement)
		{
			return statement switch {
				IfElseStatement ifElse => ifElse.FalseStatement is { } f ? f is BlockStatement or IfElseStatement : ifElse.TrueStatement is BlockStatement,
				ForStatement or ForeachStatement or WhileStatement or UsingStatement or LockStatement
					or FixedStatement or CheckedStatement or UncheckedStatement or UnsafeStatement or SwitchStatement
					or TryCatchStatement or BlockStatement => true,
				_ => false
			};
		}

		/// <summary>
		/// A single-statement body was "=> expr" if the statement starts to the right of where the statements
		/// of block bodies start (it follows the "=>" on the declaration line).
		/// </summary>
		static void DecideBodyStyles(List<(BlockStatement Body, List<DebugInfo.SequencePoint> SequencePoints)> bodies,
			TransformContext context, DebugInfo.IDebugInfoProvider provider)
		{
			foreach (var group in bodies.GroupBy(b => b.Body.GetParent<TypeDeclaration>()))
			{
				// the column of statements in bodies that certainly are blocks (several statements)
				// (from the PDB, not from the decompiled statements: "=> a && b" may be decompiled as an if)
				var blockColumns = group.Where(b => b.Body.Parent is not ConstructorDeclaration)
					.Select(b => BlockBodyColumn(b.SequencePoints))
					.OfType<int>()
					.ToList();
				int? blockColumn = Mode(blockColumns);
				foreach (var (body, sequencePoints) in group)
				{
					if (body.Statements.Count != 1 || body.Statements.Single().Annotation<SourceLayoutAnnotation>() is not { } layout)
						continue;
					// no block body in this type: use the other types of the same source file
					blockColumn ??= group.Key?.GetSymbol() is ITypeDefinition type ? DocumentBlockColumn(context, provider, type, layout.Document) : null;
					bool? expressionBody = null;
					// a constructor's base call is at the declaration: "C(int x) : base(x) => ..." / "C(int x) => ..." is on its line
					// (the last point before the body: field initializers come earlier)
					var declaration = sequencePoints.LastOrDefault(sp => sp.StartLine < layout.StartLine || sp.StartLine == layout.StartLine && sp.StartColumn < layout.StartColumn);
					if (body.Parent is ConstructorDeclaration && declaration is not null)
					{
						expressionBody = declaration.StartLine == layout.StartLine;
					}
					else
					{
						// "=> expr" is one expression: its sequence point contains all the others (switch arms, lambdas...);
						// separate statements ("switch (x) { case 1: return a; ... }") have disjoint ones
						var first = sequencePoints.OrderBy(sp => sp.StartLine).ThenBy(sp => sp.StartColumn).First();
						bool containsAll = sequencePoints.All(sp => (sp.StartLine, sp.StartColumn).CompareTo((first.StartLine, first.StartColumn)) >= 0
							&& (sp.EndLine, sp.EndColumn).CompareTo((first.EndLine, first.EndColumn)) <= 0);
						if (!containsAll)
							expressionBody = false;
						else if (blockColumn is { } column)
							expressionBody = first.StartColumn > column;
					}
					if (traceEnabled)
						System.Console.Error.WriteLine($"[body] {body.Parent?.GetSymbol()} col={layout.StartColumn} blockColumn={blockColumn} => {expressionBody}");
					if (expressionBody is { } value)
						body.AddAnnotation(value ? BodyStyleAnnotation.ExpressionBody : BodyStyleAnnotation.Block);
				}
			}
		}

		/// <summary>
		/// The column of the statements of a block body ("}" of a void method excluded); null for a single point or
		/// an expression body ("=> x switch { ... }": one point containing all the others).
		/// </summary>
		static int? BlockBodyColumn(List<DebugInfo.SequencePoint> sequencePoints)
		{
			var points = sequencePoints.Where(sp => !IsBrace(sp)).ToList();
			if (points.Count < 2)
				return null;
			var first = points.OrderBy(sp => sp.StartLine).ThenBy(sp => sp.StartColumn).First();
			if (points.All(sp => (sp.StartLine, sp.StartColumn).CompareTo((first.StartLine, first.StartColumn)) >= 0
				&& (sp.EndLine, sp.EndColumn).CompareTo((first.EndLine, first.EndColumn)) <= 0))
				return null;
			return points.Min(sp => sp.StartColumn);
		}

		static int? Mode(List<int> values) => values.Count == 0 ? null : values.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;

		static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<(string, int), int?>> documentBlockColumns = new();

		/// <summary>
		/// Where block-body statements start in <paramref name="document"/> for types nested as deep as
		/// <paramref name="type"/>: the most common first column of methods with several statements.
		/// </summary>
		internal static int? DocumentBlockColumn(TransformContext context, DebugInfo.IDebugInfoProvider provider, ITypeDefinition type, string document)
		{
			var module = context.TypeSystem.MainModule;
			int depth = Depth(type);
			var cache = documentBlockColumns.GetOrCreateValue(module.MetadataFile);
			lock (cache)
			{
				if (cache.TryGetValue((document, depth), out var cached))
					return cached;
			}
			var columns = new List<int>();
			foreach (var other in module.TypeDefinitions)
			{
				if (Depth(other) != depth || other.Name.Contains('<'))
					continue;
				foreach (var method in other.Methods)
				{
					if (method.IsConstructor || method.Name.Contains('<') || method.MetadataToken.IsNil)
						continue;
					var points = provider.GetSequencePoints((MethodDefinitionHandle)method.MetadataToken).Where(sp => !sp.IsHidden).ToList();
					if (points.All(sp => sp.DocumentUrl == document) && BlockBodyColumn(points) is { } column)
						columns.Add(column);
				}
			}
			var result = Mode(columns);
			lock (cache)
				cache[(document, depth)] = result;
			return result;
		}

		/// <summary>A sequence point of a brace ("}" of a void method, "{" in debug builds).</summary>
		internal static bool IsBrace(DebugInfo.SequencePoint point) => point.StartLine == point.EndLine && point.EndColumn - point.StartColumn == 1;

		static int Depth(ITypeDefinition type)
		{
			int depth = 0;
			for (var t = type.DeclaringTypeDefinition; t != null; t = t.DeclaringTypeDefinition)
				depth++;
			return depth;
		}
	}

	/// <summary>NetSpy: where a statement was in the original source (from the PDB).</summary>
	public sealed class SourceLayoutAnnotation
	{
		public int StartLine;
		public int StartColumn;
		public int EndLine;
		public string Document = "";
		/// <summary>The original had an empty line before this statement.</summary>
		public bool BlankLineBefore;
		/// <summary>Every source statement merged into this one was written on one line.</summary>
		public bool IsSingleLine;
		/// <summary>The original was one statement written on several lines.</summary>
		public bool IsMultiLine;
		/// <summary>NetSpy: the source width of the statement's own (first) sequence point if it is on one line, else -1.</summary>
		public int Width = -1;
		/// <summary>NetSpy: for a foreach, the width of the iteration variable's sequence point ("var x"), else -1.</summary>
		public int ForeachVariableWidth = -1;
		/// <summary>NetSpy: for a foreach, the width of the collection expression's sequence point, else -1.</summary>
		public int ForeachExpressionWidth = -1;
	}

	/// <summary>NetSpy: how the original wrote a member's body ("=> expr" or a block), from the PDB.</summary>
	public sealed class BodyStyleAnnotation
	{
		public static readonly BodyStyleAnnotation ExpressionBody = new BodyStyleAnnotation(true);
		public static readonly BodyStyleAnnotation Block = new BodyStyleAnnotation(false);

		public readonly bool IsExpressionBody;

		BodyStyleAnnotation(bool isExpressionBody) => IsExpressionBody = isExpressionBody;
	}
}
