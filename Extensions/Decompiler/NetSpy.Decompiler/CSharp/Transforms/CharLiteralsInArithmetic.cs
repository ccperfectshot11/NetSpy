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

using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.Semantics;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: "c - '0'" compiles to "c - 48" (both operands become int). Next to a char operand, a printable
	/// ASCII constant is written as the character again: the result is the same (char op char is int too).
	/// </summary>
	public sealed class CharLiteralsInArithmetic : DepthFirstAstVisitor, IAstTransform
	{
		public void Run(AstNode rootNode, TransformContext context)
		{
			rootNode.AcceptVisitor(this);
		}

		public override void VisitBinaryOperatorExpression(BinaryOperatorExpression binary)
		{
			base.VisitBinaryOperatorExpression(binary);
			switch (binary.Operator)
			{
				case BinaryOperatorType.Add:
				case BinaryOperatorType.Subtract:
				case BinaryOperatorType.Equality:
				case BinaryOperatorType.InEquality:
				case BinaryOperatorType.LessThan:
				case BinaryOperatorType.LessThanOrEqual:
				case BinaryOperatorType.GreaterThan:
				case BinaryOperatorType.GreaterThanOrEqual:
					if (IsChar(binary.Left))
						ToCharLiteral(binary.Right);
					else if (IsChar(binary.Right))
						ToCharLiteral(binary.Left);
					break;
			}
		}

		static bool IsChar(Expression? expression)
		{
			return expression is not null and not PrimitiveExpression
				&& expression.GetResolveResult() is { Type: var type } && type.IsKnownType(KnownTypeCode.Char);
		}

		static void ToCharLiteral(Expression? expression)
		{
			if (expression is not PrimitiveExpression { Value: int value } primitive || value < 32 || value > 126)
				return;
			var literal = new PrimitiveExpression((char)value);
			literal.AddAnnotation(new ConstantResolveResult(primitive.GetResolveResult().Type, value));
			foreach (var annotation in primitive.Annotations)
			{
				if (annotation is IL.ILInstruction)
					literal.AddAnnotation(annotation);
			}
			primitive.ReplaceWith(literal);
		}
	}
}
