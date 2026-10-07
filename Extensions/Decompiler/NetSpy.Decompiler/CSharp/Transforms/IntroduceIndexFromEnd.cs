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
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: "x[^1]" on a value the compiler copies first (e.g. a span field) comes back as
	/// <code>
	/// ReadOnlySpan&lt;int&gt; span = values;
	/// return span[span.Length - 1];
	/// </code>
	/// The copy is a compiler temporary used only in "t[t.Length - K]" ("t.Count"), so this is "values[^K]".
	/// </summary>
	public sealed class IntroduceIndexFromEnd : DepthFirstAstVisitor, IAstTransform
	{
		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.Ranges)
				return;
			rootNode.AcceptVisitor(this);
		}

		public override void VisitBlockStatement(BlockStatement block)
		{
			base.VisitBlockStatement(block);
			var statements = block.Statements.ToList();
			for (int i = 0; i + 1 < statements.Count; i++)
			{
				if (statements[i] is not VariableDeclarationStatement { Variables.Count: 1, Modifiers: Modifiers.None } decl
					|| decl.Variables.Single() is not { Initializer: { } source } variable
					|| variable.GetILVariable() is not { HasGeneratedName: true } temp
					|| !IsSimpleRead(source))
					continue;
				var tempType = decl.Type.GetResolveResult().Type;
				if (tempType.Kind == TypeKind.Unknown || !source.GetResolveResult().Type.Equals(tempType))
					continue;
				// the only two uses: "t[t.Length - K]" in the next statement
				var uses = block.Ancestors.OfType<EntityDeclaration>().FirstOrDefault() is { } entity
					? entity.Descendants.OfType<IdentifierExpression>().Where(id => id.GetILVariable() == temp).ToList()
					: null;
				if (uses is not { Count: 2 })
					continue;
				// "t.Substring(0, t.Length - K)" is "x[..^K]" (a string range)
				if (uses[0].Parent is MemberReferenceExpression { MemberName: "Substring", Parent: InvocationExpression { Arguments.Count: 2 } substring } substringTarget
					&& substringTarget.Target == uses[0] && substring.Ancestors.Contains(statements[i + 1])
					&& substring.Arguments.First() is PrimitiveExpression { Value: 0 }
					&& substring.Arguments.Last() is BinaryOperatorExpression { Operator: BinaryOperatorType.Subtract,
						Left: MemberReferenceExpression { MemberName: "Length" } stringLength, Right: PrimitiveExpression { Value: int end } endLiteral }
					&& stringLength.Target == uses[1] && end > 0 && tempType.IsKnownType(KnownTypeCode.String))
				{
					var range = new BinaryOperatorExpression(null!, BinaryOperatorType.Range,
						new UnaryOperatorExpression(UnaryOperatorType.IndexFromEnd, endLiteral.Detach()));
					substring.ReplaceWith(new IndexerExpression(source.Detach(), range));
					decl.Remove();
					continue;
				}
				if (uses[0].Parent is not IndexerExpression indexer || indexer.Target != uses[0] || indexer.Arguments.Count != 1
					|| !indexer.Ancestors.Contains(statements[i + 1]))
					continue;
				if (indexer.Arguments.Single() is not BinaryOperatorExpression { Operator: BinaryOperatorType.Subtract } subtract
					|| subtract.Left is not MemberReferenceExpression { MemberName: "Length" or "Count" } length
					|| length.Target != uses[1]
					|| subtract.Right is not PrimitiveExpression { Value: int k } fromEnd || k <= 0)
					continue;
				indexer.Target = source.Detach();
				subtract.ReplaceWith(new UnaryOperatorExpression(UnaryOperatorType.IndexFromEnd, fromEnd.Detach()));
				decl.Remove();
			}
		}

		/// <summary>A local, parameter, "this.field" or field: reading it twice is the same as once.</summary>
		static bool IsSimpleRead(Expression e) => e switch {
			IdentifierExpression or PrimitiveExpression => true,
			MemberReferenceExpression { Target: ThisReferenceExpression or BaseReferenceExpression } member => member.GetSymbol() is IField,
			MemberReferenceExpression member => member.GetSymbol() is IField && IsSimpleRead(member.Target!),
			_ => false
		};
	}
}
