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
	/// NetSpy: C# 9 pattern combinators on one variable, e.g. "value is not (null or "special")", are compiled to
	/// plain conditions and come back as
	/// <code>
	/// bool flag = ((value == null || (value is string text &amp;&amp; text == "special")) ? true : false);
	/// return !flag;
	/// </code>
	/// This transform folds them back:
	/// "C ? true : false" → "C", a single-use temporary "bool t = E; return !t;" → "return !E;",
	/// "x == null || x is string t &amp;&amp; t == "c"" → "x is null or "c"" and "!(x is P)" → "x is not (P)".
	/// </summary>
	public sealed class IntroducePatternCombinators : DepthFirstAstVisitor, IAstTransform
	{
		TransformContext context = null!;

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.PatternCombinators)
				return;
			this.context = context;
			rootNode.AcceptVisitor(this);
		}

		public override void VisitConditionalExpression(ConditionalExpression conditional)
		{
			base.VisitConditionalExpression(conditional);
			// "C ? true : false" → "C", "C ? false : true" → "!C"
			if (conditional.TrueExpression is PrimitiveExpression { Value: bool whenTrue }
				&& conditional.FalseExpression is PrimitiveExpression { Value: bool whenFalse } && whenTrue != whenFalse)
			{
				var condition = Unparenthesize(conditional.Condition!).Detach();
				conditional.ReplaceWith(whenTrue ? condition : new UnaryOperatorExpression(UnaryOperatorType.Not, condition));
			}
		}

		public override void VisitBlockStatement(BlockStatement block)
		{
			base.VisitBlockStatement(block);
			// "bool t = E; return t;" / "return !t;" with a compiler temporary → "return E;" / "return !E;"
			var statements = block.Statements.ToList();
			for (int i = 0; i + 1 < statements.Count; i++)
			{
				if (statements[i] is not VariableDeclarationStatement { Variables.Count: 1 } decl
					|| decl.Variables.Single() is not { Initializer: { } value } variable
					|| variable.GetILVariable() is not { HasGeneratedName: true } temporary
					|| statements[i + 1] is not ReturnStatement { Expression: { } returned })
					continue;
				bool negated = returned is UnaryOperatorExpression { Operator: UnaryOperatorType.Not };
				var use = Unparenthesize(negated ? ((UnaryOperatorExpression)returned).Expression! : returned);
				if (use is not IdentifierExpression id || id.GetILVariable() != temporary)
					continue;
				if (block.Descendants.OfType<IdentifierExpression>().Count(x => x.GetILVariable() == temporary) != 1)
					continue;
				var expression = Unparenthesize(value).Detach();
				if (negated)
					expression = Negate(expression);
				decl.Remove();
				returned.ReplaceWith(expression);
			}
		}

		/// <summary>"!(x is P)" → "x is not (P)", otherwise "!E".</summary>
		static Expression Negate(Expression expression)
		{
			if (TryPattern(expression, out var subject, out var pattern) && pattern is not null)
			{
				var notPattern = new UnaryOperatorExpression(UnaryOperatorType.PatternNot,
					pattern is BinaryOperatorExpression ? new ParenthesizedExpression(pattern) : pattern);
				return new BinaryOperatorExpression(subject!.Detach(), BinaryOperatorType.IsPattern, notPattern);
			}
			return new UnaryOperatorExpression(UnaryOperatorType.Not, expression);
		}

		public override void VisitBinaryOperatorExpression(BinaryOperatorExpression binary)
		{
			base.VisitBinaryOperatorExpression(binary);
			// only the top of a "||" / "&&" chain: "x == null || x is string t && t == "c"" → "x is null or "c""
			if (binary.Operator is not (BinaryOperatorType.ConditionalOr or BinaryOperatorType.ConditionalAnd))
				return;
			if (binary.Parent is BinaryOperatorExpression { Operator: BinaryOperatorType.ConditionalOr or BinaryOperatorType.ConditionalAnd })
				return;
			if (TryPattern(binary, out var subject, out var pattern) && pattern is not null)
				binary.ReplaceWith(new BinaryOperatorExpression(subject!.Detach(), BinaryOperatorType.IsPattern, pattern));
		}

		/// <summary>
		/// Recognizes a condition on one variable that is the same as "subject is pattern" (the pattern is a new
		/// node; the subject is a node of the condition). Only "||" chains (with "&amp;&amp;" of a type test and its
		/// constant) are folded, so a lone "x == null" stays as it is.
		/// </summary>
		static bool TryPattern(Expression expression, out IdentifierExpression? subject, out Expression? pattern)
		{
			subject = null;
			pattern = null;
			expression = Unparenthesize(expression);
			if (expression is BinaryOperatorExpression { Operator: BinaryOperatorType.ConditionalOr } or)
			{
				if (!TryPart(Unparenthesize(or.Left!), out var leftSubject, out var leftPattern)
					&& !TryPattern(or.Left!, out leftSubject, out leftPattern))
					return false;
				if (!TryPart(Unparenthesize(or.Right!), out var rightSubject, out var rightPattern))
					return false;
				if (leftSubject is null || rightSubject is null || leftSubject.Identifier != rightSubject.Identifier
					|| leftSubject.GetILVariable() is not { } v || rightSubject.GetILVariable() != v)
					return false;
				subject = leftSubject;
				pattern = new BinaryOperatorExpression(leftPattern!, BinaryOperatorType.PatternOr, rightPattern!);
				return true;
			}
			if (expression is BinaryOperatorExpression { Operator: BinaryOperatorType.IsPattern, Left: IdentifierExpression s, Right: { } p }
				&& !p.DescendantsAndSelf.OfType<SingleVariableDesignation>().Any())
			{
				subject = s;
				pattern = p.Clone();
				return true;
			}
			return false;
		}

		/// <summary>One "or" operand: "x == null" → null, "x is string t &amp;&amp; t == "c"" → "c", "x is T" → T.</summary>
		static bool TryPart(Expression expression, out IdentifierExpression? subject, out Expression? pattern)
		{
			subject = null;
			pattern = null;
			switch (expression)
			{
				case BinaryOperatorExpression { Operator: BinaryOperatorType.Equality, Left: IdentifierExpression s, Right: NullReferenceExpression }
					when !HasUserDefinedEquality(s):
					subject = s;
					pattern = new NullReferenceExpression();
					return true;
				case BinaryOperatorExpression { Operator: BinaryOperatorType.ConditionalAnd } conjunction
					when Unparenthesize(conjunction.Left!) is BinaryOperatorExpression {
						Operator: BinaryOperatorType.IsPattern, Left: IdentifierExpression s,
						Right: DeclarationExpression { Designation: SingleVariableDesignation designation } declaration
					}
					&& Unparenthesize(conjunction.Right!) is BinaryOperatorExpression {
						Operator: BinaryOperatorType.Equality, Left: IdentifierExpression used, Right: PrimitiveExpression { Value: string } constant
					}
					&& used.Identifier == designation.Identifier
					&& declaration.Type is PrimitiveType { KnownTypeCode: KnownTypeCode.String }
					&& OnlyUse(conjunction, designation.Identifier):
					// "x is string t && t == "c"" (t used nowhere else) is "x is "c""
					subject = s;
					pattern = constant.Clone();
					return true;
				case BinaryOperatorExpression { Operator: BinaryOperatorType.IsPattern, Left: IdentifierExpression s, Right: { } p }
					when !p.DescendantsAndSelf.OfType<SingleVariableDesignation>().Any():
					subject = s;
					pattern = p.Clone();
					return true;
			}
			return false;
		}

		/// <summary>The pattern variable is used only in this "&amp;&amp;" (its declaration and one use).</summary>
		static bool OnlyUse(BinaryOperatorExpression conjunction, string name)
		{
			var body = conjunction.Ancestors.OfType<EntityDeclaration>().FirstOrDefault() as AstNode ?? conjunction.Ancestors.LastOrDefault();
			return body?.Descendants.OfType<IdentifierExpression>().Count(x => x.Identifier == name) == 1;
		}

		static bool HasUserDefinedEquality(IdentifierExpression subject)
		{
			var type = subject.GetResolveResult().Type;
			return type.GetMethods(m => m.Name == "op_Equality" && m.IsOperator, GetMemberOptions.IgnoreInheritedMembers).Any();
		}

		static Expression Unparenthesize(Expression expression)
		{
			while (expression is ParenthesizedExpression { Expression: { } inner })
				expression = inner;
			return expression;
		}
	}
}
