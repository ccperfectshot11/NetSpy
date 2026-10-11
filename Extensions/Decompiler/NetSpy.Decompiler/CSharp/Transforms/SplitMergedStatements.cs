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
using System.Reflection.Metadata;

using NetSpy.Decompiler.CSharp.Resolver;
using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.IL;
using NetSpy.Decompiler.IL.Transforms;
using NetSpy.Decompiler.TypeSystem;
using NetSpy.Decompiler.Util;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: "var result = Compute(); return result;" compiles (optimized) to a value left on the stack, so the
	/// local is gone and the decompiler writes "return Compute();". The PDB still has two sequence points
	/// (two source statements): the part of the statement evaluated before the second one becomes a local again.
	/// </summary>
	public sealed class SplitMergedStatements : IAstTransform
	{
		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.SourceLayoutFromPdb || context.DecompileRun.DebugInfoProvider is not { } provider)
				return;
			var mainModule = context.TypeSystem.MainModule;
			foreach (var entity in rootNode.DescendantsAndSelf.OfType<EntityDeclaration>().ToList())
			{
				var body = entity switch {
					MethodDeclaration m => m.Body,
					ConstructorDeclaration c => c.Body,
					OperatorDeclaration o => o.Body,
					Accessor a => a.Body,
					_ => null
				};
				var function = entity.Annotation<ILFunction>();
				if (body is null || function is null || entity.GetSymbol() is not IMethod declaredMethod)
					continue;
				var method = function.MoveNextMethod ?? declaredMethod;
				if (method.ParentModule != mainModule || method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition)
					continue;
				var points = provider.GetSequencePoints((MethodDefinitionHandle)method.MetadataToken)
					.Where(sp => !sp.IsHidden && !ApplySourceLayout.IsBrace(sp)).OrderBy(sp => sp.Offset).ToList();
				if (points.Count < 2)
					continue;
				var usedNames = new HashSet<string>(body.Descendants.OfType<Identifier>().Select(i => i.Name));
				foreach (var statement in body.Descendants.OfType<Statement>().Where(s => s.Parent is BlockStatement).ToList())
				{
					if (IsInOtherFunction(statement, body))
						continue;
					TrySplit(statement, points, function, usedNames, context);
				}
			}
		}

		void TrySplit(Statement statement, List<DebugInfo.SequencePoint> points, ILFunction function, HashSet<string> usedNames, TransformContext context)
		{
			if (statement is not (ExpressionStatement or ReturnStatement or ThrowStatement or YieldReturnStatement
				or VariableDeclarationStatement { Variables.Count: 1 } or FixedStatement { Variables.Count: 1 }))
				return;
			// a fixed statement: only its header ("fixed (char* p = text)"), not its body
			var ranges = (statement is FixedStatement fixedStatement ? fixedStatement.Variables.SelectMany(Ranges) : Ranges(statement)).ToList();
			if (ranges.Count == 0)
				return;
			// a point overlapping the statement's IL, or starting inside it (IL without annotations, e.g. a spilled "ldstr")
			int first = ranges.Min(r => r.Start), last = ranges.Max(r => r.InclusiveEnd);
			var covering = points.Where(sp => ranges.Any(r => r.Start < sp.EndOffset && sp.Offset <= r.InclusiveEnd) || sp.Offset > first && sp.Offset <= last).ToList();
			if (covering.Count != 2 || covering[0].StartLine >= covering[1].StartLine)
				return;
			int split = covering[1].Offset;
			// the first subexpression (pre-order) whose IL is all before the second statement
			// an assigned element whose address computation runs up to the second point ("dup" there) comes first
			var candidate = statement.Descendants.OfType<Expression>().FirstOrDefault(e => IsRefTarget(e)
				&& Ranges(e).Any() && Ranges(e).All(r => r.Start < split && r.InclusiveEnd <= split))
				?? statement.Descendants.OfType<Expression>().FirstOrDefault(e => Ranges(e).Any() && Ranges(e).All(r => r.InclusiveEnd < split));
			// "string text = "pinned"; fixed (char* p = text)": the pinned value's IL runs into the header's
			if (candidate is null && statement is FixedStatement { Variables.Count: 1 } header
				&& header.Variables.Single().Initializer is { } pinned and not UnaryOperatorExpression { Operator: UnaryOperatorType.AddressOf })
				candidate = pinned;
			// "ref int r = ref a[0]; r += 5;" (the ref local optimized away): an element or field assigned to
			bool asRef = candidate is not null && IsRefTarget(candidate);
			if (candidate is null || !asRef && !CanBeLocal(candidate))
				return;
			// everything else (except the operations consuming it) is evaluated after it
			var ancestors = new HashSet<AstNode>(candidate.Ancestors);
			var inside = new HashSet<AstNode>(candidate.DescendantsAndSelf);
			foreach (var node in statement.DescendantsAndSelf)
			{
				if (inside.Contains(node) || ancestors.Contains(node) || IsInOtherFunction(node, statement))
					continue;
				if (OwnRanges(node).Any(r => r.Start < split))
					return;
			}
			if (candidate.GetResolveResult() is not { Type: var type } || type.Kind is TypeKind.Unknown or TypeKind.Void or TypeKind.Null)
				return;

			var valueContext = candidate.Annotation<ILInstruction>();
			string name = AssignVariableNames.GenerateVariableName(function, type, context.DecompileRun.UsingScope, valueContext);
			string baseName = name;
			for (int i = 2; usedNames.Contains(name); i++)
				name = baseName + i;
			usedNames.Add(name);
			var reference = new IdentifierExpression(name);
			reference.AddAnnotation(new Semantics.ResolveResult(type));
			candidate.ReplaceWith(reference);
			// "(object)42" made the conversion the declaration did: "object o = 42;"
			if (candidate is CastExpression { Expression: { } converted } && converted.GetResolveResult() is { Type: { Kind: not TypeKind.Unknown } convertedType }
				&& CSharpConversions.Get(context.TypeSystem) is var conversions
				&& conversions.ImplicitConversion(convertedType, type) is { IsValid: true, IsUserDefined: false })
				candidate = converted.Detach();
			// "(Func<int, int>)(x => x + 1)": the declaration gave the lambda its delegate type
			else if (candidate is CastExpression { Expression: LambdaExpression or AnonymousMethodExpression } delegateCast && type.Kind == TypeKind.Delegate)
				candidate = delegateCast.Expression.Detach();
			AstType declaredType = context.Settings.ImplicitlyTypedLocals && IsTypeApparent(candidate) || type.ContainsAnonymousType()
				? new SimpleType("var")
				: context.TypeSystemAstBuilder.ConvertType(type);
			if (asRef)
			{
				declaredType = new ComposedType { BaseType = declaredType, HasRefSpecifier = true };
				candidate = new DirectionExpression(FieldDirection.Ref, candidate);
			}
			var declaration = new VariableDeclarationStatement(declaredType, name, candidate);
			// its sequence point is the first one (a removed cast took the IL annotations the layout is found by)
			var point = covering[0];
			declaration.AddAnnotation(new SourceLayoutAnnotation {
				StartLine = point.StartLine,
				StartColumn = point.StartColumn,
				EndLine = point.EndLine,
				IsSingleLine = point.StartLine == point.EndLine,
				IsMultiLine = point.StartLine != point.EndLine,
				Document = point.DocumentUrl,
				Width = point.StartLine == point.EndLine ? point.EndColumn - point.StartColumn : -1
			});
			statement.Parent!.InsertChildBefore(statement, declaration, Slots.Statement);
		}

		static bool IsRefTarget(Expression e) => IsAssigned(e)
			&& (e is IndexerExpression { Target: { } array } && (array.GetResolveResult()?.Type is ArrayType
				|| e.GetSymbol() is IProperty { ReturnType.Kind: TypeKind.ByReference })
				|| e is MemberReferenceExpression && e.GetSymbol() is IField { IsConst: false }
				|| e is InvocationExpression && e.GetSymbol() is IMethod { ReturnType.Kind: TypeKind.ByReference })
			&& !e.Ancestors.Any(a => a is LambdaExpression or AnonymousMethodExpression or QueryExpression);

		static bool IsAssigned(Expression expression) => expression.Parent is AssignmentExpression assignment && assignment.Left == expression
			|| expression.Parent is UnaryOperatorExpression { Operator: UnaryOperatorType.Increment or UnaryOperatorType.Decrement
				or UnaryOperatorType.PostIncrement or UnaryOperatorType.PostDecrement };

		static bool CanBeLocal(Expression expression)
		{
			if (expression is LambdaExpression or AnonymousMethodExpression or DirectionExpression or NamedArgumentExpression
				or NamedExpression or ThisReferenceExpression or BaseReferenceExpression or TypeReferenceExpression
				or NullReferenceExpression or ArrayInitializerExpression)
				return false;
			// not something assigned to
			if (expression.Parent is AssignmentExpression assignment && assignment.Left == expression)
				return false;
			if (expression.Parent is UnaryOperatorExpression { Operator: UnaryOperatorType.Increment or UnaryOperatorType.Decrement
				or UnaryOperatorType.PostIncrement or UnaryOperatorType.PostDecrement or UnaryOperatorType.AddressOf })
				return false;
			return !expression.Ancestors.Any(a => a is LambdaExpression or AnonymousMethodExpression or QueryExpression);
		}

		static bool IsTypeApparent(Expression initializer)
		{
			return initializer is ObjectCreateExpression { Type: not null } or ArrayCreateExpression { Type: not null }
				or CastExpression or AsExpression or DefaultValueExpression or AnonymousTypeCreateExpression;
		}

		// a lambda's body has the IL offsets of its own method: they say nothing about this one's sequence points
		static IEnumerable<Interval> Ranges(AstNode node) => node.DescendantsAndSelf.Where(n => !IsInOtherFunction(n, node)).SelectMany(OwnRanges);

		static IEnumerable<Interval> OwnRanges(AstNode node) => node.Annotations.OfType<ILInstruction>().SelectMany(i => i.ILRanges).Where(r => !r.IsEmpty);

		static bool IsInOtherFunction(AstNode node, AstNode body)
		{
			for (var n = node.Parent; n != null && n != body; n = n.Parent)
			{
				if (n is LambdaExpression or AnonymousMethodExpression or LocalFunctionDeclarationStatement)
					return true;
			}
			return false;
		}
	}
}
