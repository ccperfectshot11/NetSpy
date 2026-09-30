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

namespace ICSharpCode.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: C# 14 null-conditional assignment. "a.B?.C = v;" compiles to
	/// <code>
	/// T tmp = a.B;
	/// if (tmp != null) { tmp.C = v; }
	/// </code>
	/// with a compiler temporary; chains ("a?.B?.C = v") nest those ifs, indexers ("a?[i] = v") too.
	/// </summary>
	public sealed class IntroduceNullConditionalAssignment : DepthFirstAstVisitor, IAstTransform
	{
		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.NullConditionalAssignment)
				return;
			rootNode.AcceptVisitor(this);
		}

		public override void VisitBlockStatement(BlockStatement blockStatement)
		{
			// inner blocks first: a chain is reduced from its innermost if outwards
			base.VisitBlockStatement(blockStatement);
			var statements = blockStatement.Statements.ToList();
			for (int i = statements.Count - 2; i >= 0; i--)
			{
				if (TryReduce(statements[i], statements[i + 1]) is { } reduced)
				{
					statements[i].Remove();
					statements[i + 1].ReplaceWith(reduced);
				}
			}
		}

		/// <summary>"T tmp = E; if (tmp != null) { chain(tmp) = v; }" → "chain(E?) = v;"</summary>
		static Statement? TryReduce(Statement declaration, Statement next)
		{
			if (declaration is not VariableDeclarationStatement { Variables.Count: 1, Modifiers: Modifiers.None } decl
				|| decl.Variables.Single() is not { Initializer: { } value } variable
				|| variable.GetILVariable() is not { HasGeneratedName: true } temporary)
				return null;
			if (next is not IfElseStatement { FalseStatement: null } ifStatement
				|| ifStatement.Condition is not BinaryOperatorExpression { Operator: BinaryOperatorType.InEquality, Left: IdentifierExpression tested, Right: NullReferenceExpression }
				|| tested.GetILVariable() != temporary)
				return null;
			var body = ifStatement.TrueStatement is BlockStatement { Statements.Count: 1 } block ? block.Statements.Single() : ifStatement.TrueStatement;
			if (body is not ExpressionStatement { Expression: AssignmentExpression assignment })
				return null;
			// the temporary is the root of the assigned access chain, and used nowhere else
			var root = ChainRoot(assignment.Left!);
			if (root is not IdentifierExpression rootIdentifier || rootIdentifier.GetILVariable() != temporary)
				return null;
			if (assignment.Right!.DescendantsAndSelf.OfType<IdentifierExpression>().Any(id => id.GetILVariable() == temporary))
				return null;
			var conditional = new UnaryOperatorExpression(UnaryOperatorType.NullConditional, value.Detach());
			rootIdentifier.ReplaceWith(conditional);
			var result = (ExpressionStatement)body.Detach();
			return result;
		}

		/// <summary>The start of "x.A?.B[i].C": x (member, indexer and null-conditional steps).</summary>
		static Expression? ChainRoot(Expression? expression)
		{
			switch (expression)
			{
				case MemberReferenceExpression member:
					return member.Target is IdentifierExpression id ? id : ChainRoot(member.Target);
				case IndexerExpression indexer:
					return indexer.Target is IdentifierExpression id2 ? id2 : ChainRoot(indexer.Target);
				case UnaryOperatorExpression { Operator: UnaryOperatorType.NullConditional } nullConditional:
					return nullConditional.Expression is IdentifierExpression id3 ? id3 : ChainRoot(nullConditional.Expression);
				default:
					return null;
			}
		}
	}
}
