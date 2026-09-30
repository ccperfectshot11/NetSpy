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

using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: "return a &amp;&amp; b;" compiles to a branch; the decompiler writes it back as
	/// "if (a) { return b; } return false;". This combines it again:
	/// <code>
	/// if (a) return b; return false;   →  return a &amp;&amp; b;
	/// if (a) return true; return b;    →  return a || b;
	/// </code>
	/// </summary>
	public sealed class CombineBooleanReturns : DepthFirstAstVisitor, IAstTransform
	{
		public void Run(AstNode rootNode, TransformContext context)
		{
			rootNode.AcceptVisitor(this);
		}

		public override void VisitBlockStatement(BlockStatement blockStatement)
		{
			base.VisitBlockStatement(blockStatement);
			var statements = blockStatement.Statements.ToList();
			// from the end: the combined return can combine again with the if before it
			for (int i = statements.Count - 2; i >= 0; i--)
			{
				if (statements[i] is not IfElseStatement { FalseStatement: null } ifStatement
					|| statements[i + 1] is not ReturnStatement { Expression: { } rest } finalReturn
					|| SingleReturn(ifStatement.TrueStatement) is not { } then
					|| !IsBoolean(then) || !IsBoolean(rest))
					continue;
				Expression combined;
				if (rest is PrimitiveExpression { Value: false })
				{
					// if (a) return b; return false;
					combined = new BinaryOperatorExpression(ifStatement.Condition.Detach(), BinaryOperatorType.ConditionalAnd, then.Detach());
				}
				else if (then is PrimitiveExpression { Value: true } && !UsesPatternVariables(rest, ifStatement.Condition))
				{
					// if (a) return true; return b;
					combined = new BinaryOperatorExpression(ifStatement.Condition.Detach(), BinaryOperatorType.ConditionalOr, rest.Detach());
				}
				else
				{
					continue;
				}
				finalReturn.Expression = combined;
				ifStatement.Remove();
				statements.RemoveAt(i);
			}
		}

		static Expression? SingleReturn(Statement statement)
		{
			if (statement is BlockStatement { Statements.Count: 1 } block)
				statement = block.Statements.Single();
			return statement is ReturnStatement { Expression: { } e } ? e : null;
		}

		static bool IsBoolean(Expression expression)
		{
			return expression.GetResolveResult() is { Type: var type } && type.IsKnownType(KnownTypeCode.Boolean);
		}

		/// <summary>"a is T x || x.P" doesn't compile: x is only assigned when a is T.</summary>
		static bool UsesPatternVariables(Expression expression, Expression condition)
		{
			var declared = new System.Collections.Generic.HashSet<string>(condition.Descendants.OfType<SingleVariableDesignation>().Select(d => d.Identifier));
			return declared.Count > 0 && expression.DescendantsAndSelf.OfType<IdentifierExpression>().Any(id => declared.Contains(id.Identifier));
		}
	}
}
