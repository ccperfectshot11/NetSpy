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

using NetSpy.Decompiler.CSharp.Resolver;
using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: "(double)a / b" (a, b int) compiles to a conversion of both operands, so it comes back as
	/// "(double)a / (double)b". When both operands are cast to the same numeric type and the right operand
	/// converts to it implicitly, the right cast is dropped: the binary operator promotes it the same way.
	/// </summary>
	public sealed class RemoveRedundantOperandCasts : DepthFirstAstVisitor, IAstTransform
	{
		CSharpConversions conversions = null!;

		public void Run(AstNode rootNode, TransformContext context)
		{
			conversions = CSharpConversions.Get(context.TypeSystem);
			rootNode.AcceptVisitor(this);
		}

		public override void VisitBinaryOperatorExpression(BinaryOperatorExpression binary)
		{
			base.VisitBinaryOperatorExpression(binary);
			if (binary.Operator is not (BinaryOperatorType.Add or BinaryOperatorType.Subtract or BinaryOperatorType.Multiply
				or BinaryOperatorType.Divide or BinaryOperatorType.Modulus
				or BinaryOperatorType.LessThan or BinaryOperatorType.LessThanOrEqual
				or BinaryOperatorType.GreaterThan or BinaryOperatorType.GreaterThanOrEqual
				or BinaryOperatorType.Equality or BinaryOperatorType.InEquality))
				return;
			if (binary.Left is not CastExpression left || binary.Right is not CastExpression { Expression: { } rightOperand } right)
				return;
			var leftType = left.GetResolveResult().Type;
			var rightType = right.GetResolveResult().Type;
			if (!IsNumeric(leftType) || !leftType.Equals(rightType))
				return;
			var operandType = rightOperand.GetResolveResult().Type;
			if (!IsNumeric(operandType) || operandType.Equals(rightType))
				return;
			// only an implicit numeric conversion gives the same value as the explicit cast
			var conversion = conversions.ImplicitConversion(operandType, rightType);
			if (!conversion.IsImplicit || !conversion.IsNumericConversion)
				return;
			right.ReplaceWith(rightOperand.Detach());
		}

		static bool IsNumeric(IType type) =>
			type.GetDefinition()?.KnownTypeCode is KnownTypeCode.SByte or KnownTypeCode.Byte or KnownTypeCode.Int16
				or KnownTypeCode.UInt16 or KnownTypeCode.Int32 or KnownTypeCode.UInt32 or KnownTypeCode.Int64
				or KnownTypeCode.UInt64 or KnownTypeCode.Single or KnownTypeCode.Double or KnownTypeCode.Decimal
				or KnownTypeCode.Char;
	}
}
