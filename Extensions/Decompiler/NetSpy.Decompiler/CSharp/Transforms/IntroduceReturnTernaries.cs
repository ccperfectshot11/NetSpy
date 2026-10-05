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

using NetSpy.Decompiler.CSharp.Resolver;
using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.IL;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: "=> c ? a : b" with both values returned directly compiles to two "ret" paths, so it comes back as
	/// <code>
	/// if (!c) { return b; }
	/// return a;
	/// </code>
	/// With a PDB that has a single sequence point for the member (one expression, not three statements),
	/// the body becomes "return c ? a : b;" again (ExpressionBodiedMembers then writes "=>").
	/// Roslyn emits the "false" value first, so the value with the higher IL offset is the "true" one.
	/// </summary>
	public sealed class IntroduceReturnTernaries : DepthFirstAstVisitor, IAstTransform
	{
		TransformContext context = null!;
		CSharpConversions conversions = null!;

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.SourceLayoutFromPdb || context.DecompileRun.DebugInfoProvider is null)
				return;
			this.context = context;
			this.conversions = CSharpConversions.Get(context.TypeSystem);
			rootNode.AcceptVisitor(this);
		}

		public override void VisitMethodDeclaration(MethodDeclaration methodDeclaration)
		{
			Process(methodDeclaration.Body, methodDeclaration.GetSymbol() as IMethod);
			base.VisitMethodDeclaration(methodDeclaration);
		}

		public override void VisitOperatorDeclaration(OperatorDeclaration operatorDeclaration)
		{
			Process(operatorDeclaration.Body, operatorDeclaration.GetSymbol() as IMethod);
			base.VisitOperatorDeclaration(operatorDeclaration);
		}

		public override void VisitAccessor(Accessor accessor)
		{
			if (Process(accessor.Body, accessor.GetSymbol() as IMethod))
				UseExpressionBody(accessor);
			base.VisitAccessor(accessor);
		}

		/// <summary>A get-only property/indexer whose getter now is "return x;": "=> x" (like NormalizeBlockStatements).</summary>
		void UseExpressionBody(Accessor getter)
		{
			if (AnnotateAccessorStyles.StyleOf(getter.Body) is { } style && style != AccessorStyle.PropertyExpressionBody)
				return;
			if (getter.Attributes.Count != 0 || (getter.Modifiers & ~Modifiers.Readonly) != 0
				|| getter.Body?.Statements.SingleOrDefault() is not ReturnStatement { Expression: { } value })
				return;
			switch (getter.Parent)
			{
				case PropertyDeclaration { Setter: null } property when property.Getter == getter:
					context.Step("Use expression-bodied property", property);
					property.Modifiers |= getter.Modifiers;
					property.ExpressionBody = value.Detach();
					property.CopyAnnotationsFrom(getter);
					getter.Remove();
					break;
				case IndexerDeclaration { Setter: null } indexer when indexer.Getter == getter:
					context.Step("Use expression-bodied indexer", indexer);
					indexer.Modifiers |= getter.Modifiers;
					indexer.ExpressionBody = value.Detach();
					indexer.CopyAnnotationsFrom(getter);
					getter.Remove();
					break;
			}
		}

		bool Process(BlockStatement? body, IMethod? method)
		{
			if (body is null || method is null)
				return false;
			if (!MatchIfReturn(body, out var ifElse, out var whenTrue, out var whenFalse))
				return false;
			if (SequencePointCount(method) != 1)
				return false;
			if (!HaveCommonType(whenTrue, whenFalse))
				return false;
			var condition = ifElse.Condition!;
			// the value that comes later in the IL is the "true" value of the source's "c ? a : b"
			int trueOffset = StartOffset(whenTrue), falseOffset = StartOffset(whenFalse);
			Expression test, first, second;
			if (trueOffset >= 0 && falseOffset >= 0 && falseOffset > trueOffset)
			{
				test = Negate(condition.Detach());
				first = whenFalse.Detach();
				second = whenTrue.Detach();
			}
			else
			{
				test = condition.Detach();
				first = whenTrue.Detach();
				second = whenFalse.Detach();
			}
			context.Step("Introduce return ternary", body);
			body.Statements.Clear();
			body.Statements.Add(new ReturnStatement(Logical(test, first, second) ?? new ConditionalExpression(test, first, second)));
			return true;
		}

		/// <summary>
		/// "c ? x : false" is "c &amp;&amp; x", "c ? x : true" is "!c || x", "c ? true : x" is "c || x", "c ? false : x" is "!c &amp;&amp; x"
		/// (for bool values: what "return a &amp;&amp; b;" compiles to).
		/// </summary>
		static Expression? Logical(Expression test, Expression whenTrue, Expression whenFalse)
		{
			if (!IsBool(whenTrue) || !IsBool(whenFalse))
				return null;
			switch (whenFalse, whenTrue)
			{
				case (PrimitiveExpression { Value: false }, _):
					return new BinaryOperatorExpression(test, BinaryOperatorType.ConditionalAnd, whenTrue);
				case (PrimitiveExpression { Value: true }, _):
					return new BinaryOperatorExpression(Negate(test), BinaryOperatorType.ConditionalOr, whenTrue);
				case (_, PrimitiveExpression { Value: true }):
					return new BinaryOperatorExpression(test, BinaryOperatorType.ConditionalOr, whenFalse);
				case (_, PrimitiveExpression { Value: false }):
					return new BinaryOperatorExpression(Negate(test), BinaryOperatorType.ConditionalAnd, whenFalse);
				default:
					return null;
			}
		}

		static bool IsBool(Expression e) => e.GetResolveResult().Type.IsKnownType(KnownTypeCode.Boolean);

		/// <summary>"if (c) { return a; } return b;" or "if (c) { return a; } else { return b; }"</summary>
		static bool MatchIfReturn(BlockStatement body, out IfElseStatement ifElse, out Expression whenTrue, out Expression whenFalse)
		{
			ifElse = null!;
			whenTrue = whenFalse = null!;
			var statements = body.Statements.ToList();
			if (statements.Count is < 1 or > 2 || statements[0] is not IfElseStatement first)
				return false;
			var falseStatement = statements.Count == 2 ? statements[1] : first.FalseStatement;
			if (statements.Count == 2 && first.FalseStatement is not null)
				return false;
			if (ReturnedValue(first.TrueStatement) is not { } t || ReturnedValue(falseStatement) is not { } f)
				return false;
			ifElse = first;
			whenTrue = t;
			whenFalse = f;
			return true;
		}

		static Expression? ReturnedValue(Statement? statement)
		{
			if (statement is BlockStatement { Statements.Count: 1 } block)
				statement = block.Statements.Single();
			return statement is ReturnStatement { Expression: { } value } && value is not DirectionExpression ? value : null;
		}

		bool HaveCommonType(Expression a, Expression b)
		{
			if (IsTypeless(a) || IsTypeless(b))
				return !(IsTypeless(a) && IsTypeless(b));
			var typeA = a.GetResolveResult().Type;
			var typeB = b.GetResolveResult().Type;
			if (typeA.Kind == TypeKind.Unknown || typeB.Kind == TypeKind.Unknown)
				return false;
			return typeA.Equals(typeB) || conversions.ImplicitConversion(typeA, typeB).IsValid || conversions.ImplicitConversion(typeB, typeA).IsValid;
		}

		/// <summary>"null" and "default" take the type of the other branch.</summary>
		static bool IsTypeless(Expression e) => e is NullReferenceExpression || e is DefaultValueExpression { Type: null };

		internal static Expression Negate(Expression condition)
		{
			switch (condition)
			{
				case UnaryOperatorExpression { Operator: UnaryOperatorType.Not, Expression: { } inner }:
					return inner.Detach();
				case BinaryOperatorExpression { Operator: BinaryOperatorType.Equality or BinaryOperatorType.InEquality } eq:
					eq.Operator = eq.Operator == BinaryOperatorType.Equality ? BinaryOperatorType.InEquality : BinaryOperatorType.Equality;
					return eq;
				case BinaryOperatorExpression { Left: { } left, Right: { } right } comparison
					when comparison.Operator is BinaryOperatorType.LessThan or BinaryOperatorType.LessThanOrEqual
						or BinaryOperatorType.GreaterThan or BinaryOperatorType.GreaterThanOrEqual
					&& !IsFloating(left) && !IsFloating(right):
					comparison.Operator = comparison.Operator switch {
						BinaryOperatorType.LessThan => BinaryOperatorType.GreaterThanOrEqual,
						BinaryOperatorType.LessThanOrEqual => BinaryOperatorType.GreaterThan,
						BinaryOperatorType.GreaterThan => BinaryOperatorType.LessThanOrEqual,
						_ => BinaryOperatorType.LessThan
					};
					return comparison;
				default:
					return new UnaryOperatorExpression(UnaryOperatorType.Not, condition);
			}
		}

		/// <summary>"!(a &lt;= b)" is not "a &gt; b" for NaN.</summary>
		static bool IsFloating(Expression e)
		{
			var type = e.GetResolveResult().Type;
			return type.IsKnownType(KnownTypeCode.Single) || type.IsKnownType(KnownTypeCode.Double) || type.Kind == TypeKind.Unknown;
		}

		/// <summary>The lowest IL offset of the instructions an expression was built from (-1 if unknown).</summary>
		internal static int StartOffset(Expression expression)
		{
			int min = int.MaxValue;
			foreach (var node in expression.DescendantsAndSelf)
			{
				foreach (var inst in node.Annotations.OfType<ILInstruction>())
				{
					foreach (var d in inst.Descendants)
					{
						if (!d.ILRangeIsEmpty && d.StartILOffset < min)
							min = d.StartILOffset;
					}
				}
			}
			return min == int.MaxValue ? -1 : min;
		}

		int SequencePointCount(IMethod method)
		{
			// a C# 14 extension member is metadata only: the sequence points belong to its implementation method
			if (method.MemberDefinition is IMethod definition
				&& definition.ResolveExtensionInfo()?.InfoOfExtensionMember(definition) is { } extension)
				method = extension.ImplementationMethod;
			if (context.DecompileRun.DebugInfoProvider is not { } provider || method.MetadataToken.IsNil
				|| method.MetadataToken.Kind != HandleKind.MethodDefinition)
				return -1;
			try
			{
				return provider.GetSequencePoints((MethodDefinitionHandle)method.MetadataToken).Count(sp => !sp.IsHidden);
			}
			catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
			{
				return -1;
			}
		}
	}
}
