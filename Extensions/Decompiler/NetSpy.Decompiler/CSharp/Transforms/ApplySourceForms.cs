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

using NetSpy.Decompiler.CSharp.OutputVisitor;
using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.CSharp.Syntax.PatternMatching;
using NetSpy.Decompiler.Semantics;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: source forms the IL keeps no trace of, but that are certain (or proven by the PDB):
	/// <list type="bullet">
	/// <item>"x ?? (x = y)" is "x ??= y" (x a local, parameter or field);</item>
	/// <item>"if (x == null) { x = y; }" is "x ??= y;" when one sequence point covers both (two statements have two);</item>
	/// <item>an argument for a [CallerArgumentExpression] parameter that is exactly the text of the other argument
	/// is the one the compiler filled in;</item>
	/// <item>local functions are declared in their source order (their own sequence points' lines).</item>
	/// </list>
	/// </summary>
	public sealed class ApplySourceForms : IAstTransform
	{
		TransformContext context = null!;

		public void Run(AstNode rootNode, TransformContext context)
		{
			this.context = context;
			if (context.Settings.GetMinimumRequiredVersion() >= LanguageVersion.CSharp8_0)
			{
				foreach (var coalescing in rootNode.Descendants.OfType<BinaryOperatorExpression>().Where(b => b.Operator == BinaryOperatorType.NullCoalescing).ToList())
					IntroduceNullCoalescingAssignment(coalescing);
				if (context.Settings.SourceLayoutFromPdb)
				{
					foreach (var ifElse in rootNode.Descendants.OfType<IfElseStatement>().ToList())
						IntroduceNullCoalescingAssignmentStatement(ifElse);
				}
			}
			foreach (var invocation in rootNode.Descendants.OfType<InvocationExpression>().ToList())
				RemoveCallerArgumentExpression(invocation);
			if (context.Settings.SourceLayoutFromPdb && context.DecompileRun.DebugInfoProvider is { } provider)
			{
				foreach (var block in rootNode.Descendants.OfType<BlockStatement>().ToList())
					SortLocalFunctions(block, provider);
			}
		}

		/// <summary>"x ?? (x = y)" → "x ??= y"</summary>
		void IntroduceNullCoalescingAssignment(BinaryOperatorExpression coalescing)
		{
			if (coalescing.Right is not AssignmentExpression { Operator: AssignmentOperatorType.Assign } assignment
				|| !IsSimpleTarget(coalescing.Left!) || !coalescing.Left!.IsMatch(assignment.Left))
				return;
			context.Step("Introduce ??=", coalescing);
			var replacement = new AssignmentExpression(coalescing.Left!.Detach(), assignment.Right.Detach()) { Operator = AssignmentOperatorType.NullCoalescing };
			replacement.CopyAnnotationsFrom(coalescing);
			coalescing.ReplaceWith(replacement);
		}

		/// <summary>"if (x == null) { x = y; }" → "x ??= y;" (the PDB has one sequence point for both).</summary>
		void IntroduceNullCoalescingAssignmentStatement(IfElseStatement ifElse)
		{
			if (ifElse.FalseStatement is not null
				|| ifElse.Condition is not BinaryOperatorExpression { Operator: BinaryOperatorType.Equality, Right: NullReferenceExpression, Left: { } target }
				|| !IsSimpleTarget(target))
				return;
			var inner = ifElse.TrueStatement is BlockStatement { Statements.Count: 1 } block ? block.Statements.Single() : ifElse.TrueStatement;
			if (inner is not ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } assignment }
				|| !target.IsMatch(assignment.Left))
				return;
			if (ifElse.Annotation<SourceLayoutAnnotation>() is not { } outer || inner.Annotation<SourceLayoutAnnotation>() is not { } own
				|| outer.StartLine != own.StartLine || outer.StartColumn != own.StartColumn)
				return;
			context.Step("Introduce ??= statement", ifElse);
			var statement = new ExpressionStatement(new AssignmentExpression(target.Detach(), assignment.Right.Detach()) { Operator = AssignmentOperatorType.NullCoalescing });
			statement.CopyAnnotationsFrom(ifElse);
			ifElse.ReplaceWith(statement);
		}

		/// <summary>A local, a parameter or a field of this/a local/a type: reading it twice is the same as once.</summary>
		static bool IsSimpleTarget(Expression e) => e switch {
			IdentifierExpression id => id.GetILVariable() is not null || id.GetSymbol() is IField,
			MemberReferenceExpression { Target: ThisReferenceExpression or TypeReferenceExpression } member => member.GetSymbol() is IField,
			MemberReferenceExpression { Target: IdentifierExpression target } member => member.GetSymbol() is IField && target.GetILVariable() is not null,
			_ => false
		};

		/// <summary>"Check(a > 0, "a > 0")" → "Check(a > 0)" for "[CallerArgumentExpression("condition")] string expression = """.</summary>
		void RemoveCallerArgumentExpression(InvocationExpression invocation)
		{
			if (invocation.GetResolveResult() is not CSharpInvocationResolveResult { IsExpandedForm: false } resolved
				|| invocation.Arguments.Any(a => a is NamedArgumentExpression))
				return;
			var arguments = invocation.Arguments.ToList();
			int offset = resolved.IsExtensionMethodInvocation ? 1 : 0;
			var parameters = resolved.Member.Parameters;
			// only the last argument can go without naming the others
			int last = arguments.Count - 1;
			if (last < 0 || last + offset >= parameters.Count || !parameters[last + offset].IsOptional)
				return;
			var attribute = parameters[last + offset].GetAttributes()
				.FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute");
			if (attribute is null || attribute.FixedArguments.Length != 1 || attribute.FixedArguments[0].Value is not string name)
				return;
			int source = Enumerable.Range(0, arguments.Count).Where(i => i + offset < parameters.Count && parameters[i + offset].Name == name && i != last).DefaultIfEmpty(-1).First();
			if (source < 0 || arguments[last] is not PrimitiveExpression { Value: string text })
				return;
			var clone = arguments[source].Clone();
			clone.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true });
			if (clone.ToString(context.Settings.CSharpFormattingOptions).Trim() != text)
				return;
			context.Step("Remove caller argument expression", invocation);
			arguments[last].Remove();
		}

		/// <summary>Consecutive local function declarations in the order of their first source line.</summary>
		static void SortLocalFunctions(BlockStatement block, DebugInfo.IDebugInfoProvider provider)
		{
			var functions = block.Statements.OfType<LocalFunctionDeclarationStatement>().ToList();
			if (functions.Count < 2)
				return;
			var lines = functions.Select(f => (Function: f, Line: FirstLine(f, provider))).ToList();
			if (lines.Any(l => l.Line < 0))
				return;
			var sorted = lines.OrderBy(l => l.Line).Select(l => l.Function).ToList();
			if (sorted.SequenceEqual(functions))
				return;
			// they must be one run of statements (only local functions between the first and the last)
			var all = block.Statements.ToList();
			int start = all.IndexOf(functions[0]), end = all.IndexOf(functions[functions.Count - 1]);
			if (end - start + 1 != functions.Count)
				return;
			Statement? anchor = start > 0 ? all[start - 1] : null;
			foreach (var f in functions)
				f.Remove();
			foreach (var f in sorted)
			{
				block.Statements.InsertAfter(anchor, f);
				anchor = f;
			}
		}

		static int FirstLine(LocalFunctionDeclarationStatement function, DebugInfo.IDebugInfoProvider provider)
		{
			if (function.GetSymbol() is not IMethod method || method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition)
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
	}
}
