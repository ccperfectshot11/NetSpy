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

using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: in "x + str" the compiler calls x's ToString itself ("x?.ToString()" for a class, a constrained
	/// "x.ToString()" for a struct). Next to a string operand (so the + stays a concatenation) the call is
	/// dropped again: "acc?.ToString() + \" | \"" → "acc + \" | \"".
	/// </summary>
	public sealed class RemoveConcatToString : DepthFirstAstVisitor, IAstTransform
	{
		public void Run(AstNode rootNode, TransformContext context)
		{
			rootNode.AcceptVisitor(this);
		}

		public override void VisitBinaryOperatorExpression(BinaryOperatorExpression binary)
		{
			base.VisitBinaryOperatorExpression(binary);
			if (binary.Operator != BinaryOperatorType.Add || binary.Left is null || binary.Right is null)
				return;
			// decide on the original operands, then change them
			bool leftIsString = IsString(binary.Left) && ToStringTarget(binary.Left) is null;
			bool rightIsString = IsString(binary.Right) && ToStringTarget(binary.Right) is null;
			if (leftIsString && ToStringTarget(binary.Right) is { } rightTarget)
				binary.Right = rightTarget.Detach();
			else if (rightIsString && ToStringTarget(binary.Left) is { } leftTarget)
				binary.Left = leftTarget.Detach();
		}

		static bool IsString(Expression expression)
		{
			// (a concatenation built by the transforms may have no resolve result: "a + str" is a string)
			return expression.GetResolveResult().Type.IsKnownType(KnownTypeCode.String)
				|| expression is PrimitiveExpression { Value: string }
				|| expression is BinaryOperatorExpression { Operator: BinaryOperatorType.Add, Left: { } l, Right: { } r } && (IsString(l) || IsString(r));
		}

		/// <summary>x of "x?.ToString()" (x a class) or "x.ToString()" (x a struct); null otherwise.</summary>
		static Expression? ToStringTarget(Expression expression)
		{
			// "x?.ToString()" is wrapped in a NullConditionalRewrap marker (the end of the ?. chain)
			if (expression is UnaryOperatorExpression { Operator: UnaryOperatorType.NullConditionalRewrap, Expression: var inner })
				expression = inner;
			if (expression is not InvocationExpression { Arguments.Count: 0, Target: MemberReferenceExpression { MemberName: "ToString", TypeArguments.Count: 0 } member })
				return null;
			if (member.Target is UnaryOperatorExpression { Operator: UnaryOperatorType.NullConditional, Expression: var classValue })
			{
				var type = classValue.GetResolveResult().Type;
				// (an unconstrained T too: the compiler writes "t?.ToString()" for it)
				return type.IsReferenceType != false && !type.IsKnownType(KnownTypeCode.String) ? classValue : null;
			}
			var valueType = member.Target.GetResolveResult().Type;
			// char + string is a concatenation too, but keep the other value types unambiguous: structs only
			return valueType.IsReferenceType == false && valueType.Kind is TypeKind.Struct or TypeKind.Enum
				&& !valueType.IsKnownType(KnownTypeCode.NullableOfT) ? member.Target : null;
		}
	}
}
