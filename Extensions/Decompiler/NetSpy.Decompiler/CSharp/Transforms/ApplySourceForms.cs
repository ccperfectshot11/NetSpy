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
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;

using NetSpy.Decompiler.CSharp.OutputVisitor;
using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.CSharp.Syntax.PatternMatching;
using NetSpy.Decompiler.IL;
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
	/// <summary>NetSpy: a query's clauses start this many spaces after the statement's column.</summary>
	public sealed class QueryAlignmentAnnotation
	{
		public readonly int ExtraSpaces;

		public QueryAlignmentAnnotation(int extraSpaces) => ExtraSpaces = extraSpaces;
	}

	/// <summary>NetSpy: the constructor's ": base(...)" / ": this(...)" stays on the declaration's line.</summary>
	public sealed class InitializerOnSameLineAnnotation
	{
		public static readonly InitializerOnSameLineAnnotation Instance = new InitializerOnSameLineAnnotation();

		InitializerOnSameLineAnnotation()
		{
		}
	}

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
			foreach (var assignment in rootNode.Descendants.OfType<AssignmentExpression>().ToList())
				IntroduceDelegateCompoundAssignment(assignment);
			if (context.Settings.SourceLayoutFromPdb && context.Settings.TupleTypes)
			{
				foreach (var block in rootNode.Descendants.OfType<BlockStatement>().ToList())
				{
					IntroduceTupleSwaps(block);
					IntroduceTupleDeclarations(block);
				}
			}
			if (context.Settings.SourceLayoutFromPdb)
			{
				foreach (var block in rootNode.Descendants.OfType<BlockStatement>().ToList())
				{
					IntroduceStringConcatenation(block);
					if (context.Settings.GetMinimumRequiredVersion() >= LanguageVersion.CSharp8_0)
						IntroduceNullCoalescingAssignmentReturn(block);
				}
			}
			foreach (var type in rootNode.Descendants.OfType<TypeDeclaration>().ToList())
				HoistUnsafe(type);
			foreach (var attribute in rootNode.Descendants.OfType<Syntax.Attribute>().ToList())
			{
				UseNameofInAttribute(attribute);
				// no sequence points for attribute arguments: a string with backslashes (a regex, a path) is written @"..."
				foreach (var literal in attribute.Descendants.OfType<PrimitiveExpression>())
				{
					if (literal.Value is string s && literal.Format == LiteralFormat.None && s.IndexOf('\\') >= 0 && !s.Any(c => char.IsControl(c)))
						literal.Format = LiteralFormat.VerbatimStringLiteral;
				}
			}
			foreach (var usingStatement in rootNode.Descendants.OfType<UsingStatement>().ToList())
				IntroduceLockOnLockObject(usingStatement);
			if (context.Settings.SourceLayoutFromPdb)
			{
				foreach (var whileStatement in rootNode.Descendants.OfType<WhileStatement>().ToList())
					IntroduceForLoop(whileStatement);
				foreach (var yieldBreak in rootNode.Descendants.OfType<YieldBreakStatement>().ToList())
					RemoveCompilerYieldBreak(yieldBreak);
				foreach (var ifElse in rootNode.Descendants.OfType<IfElseStatement>().ToList())
					IntroduceNestedReturnTernary(ifElse);
				foreach (var ifElse in rootNode.Descendants.OfType<IfElseStatement>().ToList())
					IntroduceEarlyExit(ifElse);
				foreach (var ifElse in rootNode.Descendants.OfType<IfElseStatement>().ToList())
					IntroduceLateExit(ifElse);
				foreach (var whileStatement in rootNode.Descendants.OfType<WhileStatement>().ToList())
					IntroduceOutVarInLoopCondition(whileStatement);
				foreach (var declaration in rootNode.Descendants.OfType<VariableDeclarationStatement>().ToList())
				{
					IntroduceIsVar(declaration);
					UseDynamicCreationType(declaration);
				}
				foreach (var block in rootNode.Descendants.OfType<BlockStatement>().ToList())
					CollapseDynamicIncrement(block);
				foreach (var checkedStatement in rootNode.Descendants.OfType<CheckedStatement>().ToList())
					SplitCheckedBlock(checkedStatement);
				foreach (var ifElse in rootNode.Descendants.OfType<IfElseStatement>().ToList())
					IntroduceAsWithNullCheck(ifElse);
				foreach (var block in rootNode.Descendants.OfType<BlockStatement>().ToList())
				{
					IntroduceStructWith(block);
					IntroduceInterpolatedHandler(block);
				}
				foreach (var creation in rootNode.Descendants.OfType<AnonymousTypeCreateExpression>().ToList())
					IntroduceAnonymousWith(creation);
				foreach (var query in rootNode.Descendants.OfType<QueryExpression>().ToList())
					AlignQuery(query);
			}
			// "++x;" â†’ "x++;" (statement level: the same; the usual spelling)
			foreach (var statement in rootNode.Descendants.OfType<ExpressionStatement>().ToList())
			{
				if (statement.Expression is UnaryOperatorExpression { Operator: UnaryOperatorType.Increment or UnaryOperatorType.Decrement } step)
					step.Operator = step.Operator == UnaryOperatorType.Increment ? UnaryOperatorType.PostIncrement : UnaryOperatorType.PostDecrement;
			}
			if (context.Settings.SourceLayoutFromPdb)
			{
				foreach (var entity in rootNode.Descendants.OfType<EntityDeclaration>().Where(e => e is not TypeDeclaration).ToList())
					RestoreSourceNames(entity);
			}
			if (context.Settings.SourceLayoutFromPdb && context.DecompileRun.DebugInfoProvider is { } debugInfo)
			{
				foreach (var constructor in rootNode.Descendants.OfType<ConstructorDeclaration>().ToList())
					DecideInitializerLine(constructor, debugInfo);
			}
			if (context.Settings.SourceLayoutFromPdb && context.DecompileRun.DebugInfoProvider is { } provider)
			{
				foreach (var block in rootNode.Descendants.OfType<BlockStatement>().ToList())
					SortLocalFunctions(block, provider);
			}
		}

		/// <summary>"x ?? (x = y)" â†’ "x ??= y"</summary>
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

		/// <summary>"if (x == null) { x = y; }" â†’ "x ??= y;" (the PDB has one sequence point for both).</summary>
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

		/// <summary>
		/// "T t = b; b = a; a = t;" â†’ "(a, b) = (b, a);" when the three statements are one sequence point (one source
		/// statement) and t is a compiler temporary used nowhere else.
		/// </summary>
		void IntroduceTupleSwaps(BlockStatement block)
		{
			var statements = block.Statements.ToList();
			for (int i = 0; i + 2 < statements.Count; i++)
			{
				if (statements[i] is not VariableDeclarationStatement { Variables.Count: 1, Modifiers: Modifiers.None } declaration
					|| declaration.Variables.Single() is not { Initializer: { } b } temp
					|| temp.GetILVariable() is not { HasGeneratedName: true } tempVariable
					|| statements[i + 1] is not ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } first }
					|| statements[i + 2] is not ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } second })
					continue;
				var a = first.Right;
				if (!IsSimpleTarget(b) || !IsSimpleTarget(a) || !first.Left.IsMatch(b) || !second.Left.IsMatch(a)
					|| second.Right is not IdentifierExpression read || read.GetILVariable() != tempVariable)
					continue;
				// one source statement: the three have the same sequence point
				var layouts = new[] { statements[i], statements[i + 1], statements[i + 2] }.Select(s => s.Annotation<SourceLayoutAnnotation>()).ToList();
				if (layouts.Any(l => l is null) || layouts.Any(l => l!.StartLine != layouts[0]!.StartLine || l.StartColumn != layouts[0]!.StartColumn))
					continue;
				var entity = block.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
				if (entity is null || entity.Descendants.OfType<IdentifierExpression>().Count(id => id.GetILVariable() == tempVariable) != 1)
					continue;
				context.Step("Introduce tuple swap", declaration);
				var left = new TupleExpression();
				left.Elements.Add(a.Clone());
				left.Elements.Add(b.Clone());
				var right = new TupleExpression();
				right.Elements.Add(b.Detach());
				right.Elements.Add(a.Detach());
				var swap = new ExpressionStatement(new AssignmentExpression(left, right));
				swap.CopyAnnotationsFrom(declaration);
				declaration.ReplaceWith(swap);
				statements[i + 1].Remove();
				statements[i + 2].Remove();
				statements = block.Statements.ToList();
			}
		}

		/// <summary>
		/// "string a = "x"; int b = 2;" with one sequence point for both â†’ "var (a, b) = ("x", 2);" (two declarations
		/// of a source statement each have their own).
		/// </summary>
		void IntroduceTupleDeclarations(BlockStatement block)
		{
			var statements = block.Statements.ToList();
			for (int i = 0; i < statements.Count; i++)
			{
				if (Single(statements[i]) is not { } firstVariable || statements[i].Annotation<SourceLayoutAnnotation>() is not { } layout)
					continue;
				var group = new List<VariableDeclarationStatement> { (VariableDeclarationStatement)statements[i] };
				for (int j = i + 1; j < statements.Count && Single(statements[j]) is not null
					&& statements[j].Annotation<SourceLayoutAnnotation>() is { } other
					&& other.StartLine == layout.StartLine && other.StartColumn == layout.StartColumn; j++)
					group.Add((VariableDeclarationStatement)statements[j]);
				if (group.Count < 2)
					continue;
				var variables = group.Select(g => g.Variables.Single()).ToList();
				// a tuple literal evaluates every value before assigning: no value may read the variables
				var ilVariables = variables.Select(v => v.GetILVariable()).ToList();
				if (ilVariables.Any(v => v is null || v.HasGeneratedName) || variables.Any(v => v.Initializer!.DescendantsAndSelf.OfType<IdentifierExpression>().Any(id => ilVariables.Contains(id.GetILVariable()))))
					continue;
				context.Step("Introduce tuple declaration", group[0]);
				var designation = new ParenthesizedVariableDesignation();
				var values = new TupleExpression();
				foreach (var variable in variables)
				{
					var single = new SingleVariableDesignation { Identifier = variable.Name };
					single.CopyAnnotationsFrom(variable);
					designation.VariableDesignations.Add(single);
					values.Elements.Add(variable.Initializer!.Detach());
				}
				var statement = new ExpressionStatement(new AssignmentExpression(new DeclarationExpression { Type = new SimpleType("var"), Designation = designation }, values));
				statement.CopyAnnotationsFrom(group[0]);
				group[0].ReplaceWith(statement);
				foreach (var g in group.Skip(1))
					g.Remove();
				statements = block.Statements.ToList();
			}

			static VariableInitializer? Single(Statement statement) => statement is VariableDeclarationStatement { Variables.Count: 1, Modifiers: Modifiers.None } d
				&& d.Variables.Single() is { Initializer: not null } v && v.Initializer is not (LambdaExpression or AnonymousMethodExpression or StackAllocExpression) ? v : null;
		}

		/// <summary>
		/// A concatenation of more than four pieces compiles to "string.Concat(string[])", and a piece of a generic type
		/// to "T t2 = t; array[k] = t2?.ToString();":
		/// <code>
		/// string[] a = new string[] { x, "/", null };
		/// T t2 = t;
		/// a[2] = t2?.ToString();
		/// return string.Concat(a);
		/// </code>
		/// is "return x + "/" + t;" (one source statement: one sequence point for all of them).
		/// </summary>
		void IntroduceStringConcatenation(BlockStatement block)
		{
			var statements = block.Statements.ToList();
			for (int i = 0; i < statements.Count; i++)
			{
				if (statements[i] is not VariableDeclarationStatement { Variables.Count: 1 } declaration
					|| declaration.Variables.Single() is not { Initializer: ArrayCreateExpression { Initializer: { } initializer } } arrayVariable
					|| arrayVariable.GetILVariable() is not { HasGeneratedName: true, Type: ArrayType { ElementType: var elementType } } array
					|| !elementType.IsKnownType(KnownTypeCode.String)
					|| statements[i].Annotation<SourceLayoutAnnotation>() is not { } layout)
					continue;
				var pieces = initializer.Elements.ToList<Expression?>();
				var used = new List<Statement> { statements[i] };
				var temps = new Dictionary<ILVariable, Expression>();
				int j = i + 1;
				for (; j < statements.Count; j++)
				{
					var statement = statements[j];
					if (statement.Annotation<SourceLayoutAnnotation>() is { } l && (l.StartLine != layout.StartLine || l.StartColumn != layout.StartColumn))
						break;
					if (statement is VariableDeclarationStatement { Variables.Count: 1 } copy && copy.Variables.Single() is { Initializer: { } value } temp
						&& temp.GetILVariable() is { HasGeneratedName: true } tempVariable && value is IdentifierExpression or MemberReferenceExpression)
					{
						temps[tempVariable] = value;
						used.Add(statement);
						continue;
					}
					if (statement is ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign,
						Left: IndexerExpression { Target: IdentifierExpression target, Arguments.Count: 1 } indexer } store }
						&& target.GetILVariable() == array && indexer.Arguments.Single() is PrimitiveExpression { Value: int k }
						&& k >= 0 && k < pieces.Count && pieces[k] is NullReferenceExpression)
					{
						pieces[k] = Piece(store.Right, temps);
						used.Add(statement);
						continue;
					}
					break;
				}
				if (j >= statements.Count || pieces.Any(p => p is null or NullReferenceExpression))
					continue;
				// the one use of the array: "string.Concat(array)" in the next statement
				var concat = statements[j].DescendantsAndSelf.OfType<InvocationExpression>().FirstOrDefault(inv =>
					inv.GetSymbol() is IMethod { Name: "Concat", DeclaringType.FullName: "System.String" }
					&& inv.Arguments.Count == 1 && inv.Arguments.Single() is IdentifierExpression id && id.GetILVariable() == array);
				// the array is read only by the stores and by "string.Concat"
				var entity = block.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
				int stores = used.Count - 1 - temps.Count;
				if (concat is null || entity is null || entity.Descendants.OfType<IdentifierExpression>().Count(id => id.GetILVariable() == array) != stores + 1)
					continue;
				// the chain must start with a string so that "+" is string concatenation
				if (!pieces[0]!.GetResolveResult().Type.IsKnownType(KnownTypeCode.String) && !pieces[1]!.GetResolveResult().Type.IsKnownType(KnownTypeCode.String))
					continue;
				context.Step("Introduce string concatenation", statements[j]);
				Expression chain = pieces[0]!.Detach();
				foreach (var piece in pieces.Skip(1))
					chain = new BinaryOperatorExpression(chain, BinaryOperatorType.Add, piece!.Parent is null ? piece : piece.Detach());
				concat.ReplaceWith(chain);
				statements[j].CopyAnnotationsFrom(statements[i]);
				foreach (var statement in used)
					statement.Remove();
				DecideBodyStyle(block);
				statements = block.Statements.ToList();
				i = -1;
			}

			// "t2?.ToString()" with t2 a copy of t â†’ t
			static Expression? Piece(Expression value, Dictionary<ILVariable, Expression> temps)
			{
				string text = value.ToString().Replace(" ", "");
				foreach (var temp in temps)
				{
					if (text == temp.Key.Name + "?.ToString()")
						return temp.Value.Clone();
				}
				// any other use of a temporary: it would disappear with its declaration
				if (value.DescendantsAndSelf.OfType<IdentifierExpression>().Any(id => id.GetILVariable() is { } v && temps.ContainsKey(v)))
					return null;
				return value;
			}
		}

		/// <summary>
		/// "int i = 0; int j = 10; while (i &lt; j) { ...; i += 3; j--; }" whose declarations and last statements are on
		/// one source line (each part of a for header has a sequence point of its own, all on the header's line) is
		/// "for (int i = 0, j = 10; i &lt; j; i += 3, j--) { ... }".
		/// </summary>
		void IntroduceForLoop(WhileStatement whileStatement)
		{
			if (whileStatement.Parent is not BlockStatement block || whileStatement.EmbeddedStatement is not BlockStatement body
				|| body.Descendants.Any(n => n is ContinueStatement continueStatement && !continueStatement.Ancestors.TakeWhile(a => a != body)
					.Any(a => a is WhileStatement or ForStatement or ForeachStatement or DoWhileStatement)))
				return;
			var statements = block.Statements.ToList();
			int index = statements.IndexOf(whileStatement);
			// the declarations right before the loop, on one line
			var declarations = new List<VariableDeclarationStatement>();
			int? line = null;
			for (int i = index - 1; i >= 0; i--)
			{
				if (statements[i] is not VariableDeclarationStatement { Modifiers: Modifiers.None } declaration
					|| declaration.Variables.Any(v => v.Initializer is null)
					|| declaration.Annotation<SourceLayoutAnnotation>() is not { } layout || (line is not null && layout.StartLine != line))
					break;
				line = layout.StartLine;
				declarations.Insert(0, declaration);
			}
			if (line is null || declarations.Select(d => d.Type.ToString()).Distinct().Count() != 1)
				return;
			// the iterators: the body's last statements on that same line
			var bodyStatements = body.Statements.ToList();
			var iterators = new List<Statement>();
			for (int i = bodyStatements.Count - 1; i >= 0; i--)
			{
				if (bodyStatements[i] is not ExpressionStatement iterator || iterator.Annotation<SourceLayoutAnnotation>()?.StartLine != line)
					break;
				iterators.Insert(0, iterator);
			}
			if (iterators.Count == 0 || iterators.Count == bodyStatements.Count)
				return;
			// the variables must not be used after the loop (a for loop scopes them)
			var variables = declarations.SelectMany(d => d.Variables).Select(v => v.GetILVariable()).ToList();
			if (variables.Any(v => v is null)
				|| statements.Skip(index + 1).Any(s => s.DescendantsAndSelf.OfType<IdentifierExpression>().Any(id => variables.Contains(id.GetILVariable()))))
				return;
			context.Step("Introduce for loop", whileStatement);
			var forStatement = new ForStatement { Condition = whileStatement.Condition.Detach(), EmbeddedStatement = body.Detach() };
			var merged = new VariableDeclarationStatement { Type = declarations[0].Type.Clone() };
			foreach (var declaration in declarations)
			{
				foreach (var variable in declaration.Variables.ToList())
					merged.Variables.Add(variable.Detach());
				declaration.Remove();
			}
			forStatement.Initializers.Add(merged);
			foreach (var iterator in iterators)
				forStatement.Iterators.Add(iterator.Detach());
			forStatement.CopyAnnotationsFrom(whileStatement);
			whileStatement.ReplaceWith(forStatement);
		}

		/// <summary>
		/// "var q = from c in cs\n        where ...": a clause's lambda has a sequence point at its expression ("where c.X"
		/// â†’ at "c.X"), which gives the column the source put the clauses at. "var q = ..." declarations of a query get "var".
		/// </summary>
		void AlignQuery(QueryExpression query)
		{
			if (query.Ancestors.OfType<Statement>().FirstOrDefault() is not { } statement || statement.Annotation<SourceLayoutAnnotation>() is not { } layout
				|| context.DecompileRun.DebugInfoProvider is not { } provider)
				return;
			if (statement is VariableDeclarationStatement { Variables.Count: 1 } declaration && !declaration.Type.IsVar()
				&& declaration.Variables.Single().Initializer == query && context.Settings.ImplicitlyTypedLocals
				&& declaration.Variables.Single().GetILVariable()?.Type is ParameterizedType { Namespace: "System.Linq" or "System.Collections.Generic" })
			{
				var var = new SimpleType("var");
				var.CopyAnnotationsFrom(declaration.Type);
				declaration.Type = var;
			}
			foreach (var clause in query.Clauses.Skip(1))
			{
				var (keyword, expression) = clause switch {
					QueryWhereClause where => ("where ", where.Condition),
					QuerySelectClause select => ("select ", select.Expression),
					QueryOrderClause order => ("orderby ", order.Orderings.FirstOrDefault()?.Expression),
					_ => ("", (Expression?)null)
				};
				if (expression is null)
					continue;
				var method = expression.DescendantsAndSelf.SelectMany(n => n.Annotations.OfType<ILInstruction>())
					.Select(Function).FirstOrDefault(f => f?.Method is not null)?.Method;
				if (method is null || method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition)
					continue;
				DebugInfo.SequencePoint? point;
				try
				{
					point = provider.GetSequencePoints((MethodDefinitionHandle)method.MetadataToken).FirstOrDefault(sp => !sp.IsHidden);
				}
				catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
				{
					continue;
				}
				// the statement's own line: the last point of the method before the clause's line
				var statementPoint = ApplySourceLayout.MethodPoints(statement, context)?.Where(sp => point is not null && sp.StartLine < point.StartLine)
					.OrderByDescending(sp => sp.StartLine).ThenBy(sp => sp.StartColumn).FirstOrDefault();
				if (point is null || statementPoint is null)
					return;
				int extra = point.StartColumn - keyword.Length - statementPoint.StartColumn;
				if (extra > 0)
					query.AddAnnotation(new QueryAlignmentAnnotation(extra));
				return;
			}

			static ILFunction? Function(ILInstruction instruction)
			{
				for (ILInstruction? i = instruction; i is not null; i = i.Parent)
				{
					if (i is ILFunction f)
						return f;
				}
				return null;
			}
		}

		/// <summary>The two statements belong to the same source statement (same sequence point).</summary>
		static bool SameSource(Statement a, Statement b)
		{
			return a.Annotation<SourceLayoutAnnotation>() is { } x && b.Annotation<SourceLayoutAnnotation>() is { } y
				&& x.StartLine == y.StartLine && x.StartColumn == y.StartColumn;
		}

		/// <summary>The number of identifiers of the member that read <paramref name="variable"/>.</summary>
		static int Uses(AstNode scope, ILVariable variable)
		{
			var entity = scope.Ancestors.OfType<EntityDeclaration>().FirstOrDefault() ?? scope;
			return entity.Descendants.OfType<IdentifierExpression>().Count(id => id.GetILVariable() == variable);
		}

		/// <summary>
		/// "T copy = s; copy.P = v; ...; use(copy);" (one source statement, a struct copy) is "use(s with { P = v })".
		/// </summary>
		void IntroduceStructWith(BlockStatement block)
		{
			if (!context.Settings.WithExpressions)
				return;
			var statements = block.Statements.ToList();
			for (int i = 0; i < statements.Count; i++)
			{
				if (statements[i] is not VariableDeclarationStatement { Variables.Count: 1 } declaration
					|| declaration.Variables.Single() is not { Initializer: { } source } copyVariable
					|| copyVariable.GetILVariable() is not { HasGeneratedName: true } copy || copy.Type.IsReferenceType != false
					|| !IsSimpleTarget(source))
					continue;
				var assignments = new List<(string Name, Expression Value)>();
				int j = i + 1;
				for (; j < statements.Count && SameSource(statements[i], statements[j]); j++)
				{
					if (statements[j] is ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign,
						Left: MemberReferenceExpression { Target: IdentifierExpression target } member } assignment }
						&& target.GetILVariable() == copy && !assignment.Right.DescendantsAndSelf.OfType<IdentifierExpression>().Any(id => id.GetILVariable() == copy))
					{
						assignments.Add((member.MemberName, assignment.Right));
						continue;
					}
					break;
				}
				if (assignments.Count == 0 || j >= statements.Count || !SameSource(statements[i], statements[j]))
					continue;
				int usesBefore = assignments.Count;
				var use = statements[j].DescendantsAndSelf.OfType<IdentifierExpression>().Where(id => id.GetILVariable() == copy).ToList();
				if (use.Count != 1 || Uses(block, copy) != usesBefore + 1)
					continue;
				context.Step("Introduce struct with", declaration);
				var initializer = new ArrayInitializerExpression();
				foreach (var (name, value) in assignments)
					initializer.Elements.Add(new NamedExpression(name, value.Detach()));
				var with = new WithInitializerExpression { Expression = source.Detach(), Initializer = initializer };
				with.CopyAnnotationsFrom(use[0]);
				use[0].ReplaceWith(with);
				statements[j].CopyAnnotationsFrom(statements[i]);
				for (int k = i; k < j; k++)
					statements[k].Remove();
				statements = block.Statements.ToList();
			}
		}

		/// <summary>
		/// "new { A = x.A, B = v }" where x has the same anonymous type is "x with { B = v }"; the values the compiler stored
		/// in temporaries first (same sequence point) are put back.
		/// </summary>
		void IntroduceAnonymousWith(AnonymousTypeCreateExpression creation)
		{
			if (!context.Settings.WithExpressions || creation.GetResolveResult()?.Type is not { } type || !type.ContainsAnonymousType()
				|| creation.Ancestors.OfType<Statement>().FirstOrDefault() is not { } statement)
				return;
			IdentifierExpression? source = null;
			var changed = new List<NamedExpression>();
			foreach (var initializer in creation.Initializers)
			{
				if (initializer is not NamedExpression named)
					return;
				if (named.Expression is MemberReferenceExpression { Target: IdentifierExpression x } member && member.MemberName == named.Name
					&& x.GetResolveResult()?.Type is { } xType && xType.Equals(type) && (source is null || source.Identifier == x.Identifier))
				{
					source = x;
					continue;
				}
				changed.Add(named);
			}
			if (source is null || changed.Count == 0)
				return;
			// temporaries the compiler made for the new values, declared right before (same source statement)
			var temps = new List<VariableDeclarationStatement>();
			foreach (var named in changed)
			{
				if (named.Expression is IdentifierExpression id && id.GetILVariable() is { HasGeneratedName: true } v
					&& statement.PrevSibling is VariableDeclarationStatement { Variables.Count: 1 } d && d.Variables.Single().GetILVariable() == v
					&& SameSource(d, statement) && Uses(statement, v) == 1)
					temps.Add(d);
			}
			context.Step("Introduce anonymous with", creation);
			var initializerList = new ArrayInitializerExpression();
			foreach (var named in changed)
			{
				var value = named.Expression;
				var temp = temps.FirstOrDefault(t => value is IdentifierExpression id && t.Variables.Single().GetILVariable() == id.GetILVariable());
				Expression actual = temp is not null ? temp.Variables.Single().Initializer!.Detach() : value.Detach();
				initializerList.Elements.Add(new NamedExpression(named.Name, actual));
			}
			var with = new WithInitializerExpression { Expression = source.Clone(), Initializer = initializerList };
			with.CopyAnnotationsFrom(creation);
			creation.ReplaceWith(with);
			foreach (var temp in temps)
			{
				statement.CopyAnnotationsFrom(temp);
				temp.Remove();
			}
		}

		/// <summary>
		/// "var h = new H(n, k); h.AppendLiteral("a"); h.AppendFormatted(x); M(h);" for an [InterpolatedStringHandler] H,
		/// all one source statement, is "M($"a{x}")".
		/// </summary>
		void IntroduceInterpolatedHandler(BlockStatement block)
		{
			var statements = block.Statements.ToList();
			for (int i = 0; i < statements.Count; i++)
			{
				if (statements[i] is not VariableDeclarationStatement { Variables.Count: 1 } declaration
					|| declaration.Variables.Single() is not { Initializer: ObjectCreateExpression { Arguments.Count: >= 2 } } handlerVariable
					|| handlerVariable.GetILVariable() is not { HasGeneratedName: true } handler
					|| handler.Type.GetDefinition()?.GetAttributes().Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.InterpolatedStringHandlerAttribute") != true)
					continue;
				var content = new List<InterpolatedStringContent>();
				int j = i + 1;
				bool valid = true;
				for (; j < statements.Count && SameSource(statements[i], statements[j]); j++)
				{
					if (statements[j] is not ExpressionStatement { Expression: InvocationExpression { Target: MemberReferenceExpression { Target: IdentifierExpression target } member } call }
						|| target.GetILVariable() != handler)
						break;
					var arguments = call.Arguments.ToList();
					if (member.MemberName == "AppendLiteral" && arguments.Count == 1 && arguments[0] is PrimitiveExpression { Value: string text })
					{
						content.Add(new InterpolatedStringText(text.Replace("{", "{{").Replace("}", "}}")));
					}
					else if (member.MemberName == "AppendFormatted" && arguments.Count is >= 1 and <= 3)
					{
						int alignment = 0;
						string? format = null;
						foreach (var extra in arguments.Skip(1))
						{
							if (extra is PrimitiveExpression { Value: int a })
								alignment = a;
							else if (extra is PrimitiveExpression { Value: string f })
								format = f;
							else
								valid = false;
						}
						content.Add(new Interpolation(arguments[0].Clone(), alignment, format));
					}
					else
					{
						break;
					}
				}
				if (!valid || content.Count == 0 || j >= statements.Count || !SameSource(statements[i], statements[j]))
					continue;
				var use = statements[j].DescendantsAndSelf.OfType<IdentifierExpression>().Where(id => id.GetILVariable() == handler).ToList();
				if (use.Count != 1 || Uses(block, handler) != j - i)
					continue;
				context.Step("Introduce interpolated string handler", declaration);
				var interpolated = new InterpolatedStringExpression();
				interpolated.Content.AddRange(content);
				interpolated.CopyAnnotationsFrom(use[0]);
				use[0].ReplaceWith(interpolated);
				statements[j].CopyAnnotationsFrom(statements[i]);
				for (int k = i; k < j; k++)
					statements[k].Remove();
				statements = block.Statements.ToList();
			}
		}

		/// <summary>
		/// "if (e is T v) { ... }" â†’ "T? v = e as T; if (v != null) { ... }" when the PDB has the two source lines: a point as
		/// wide as the declaration and, on the next line, one as wide as "if (v != null)".
		/// </summary>
		void IntroduceAsWithNullCheck(IfElseStatement ifElse)
		{
			if (ifElse.Parent is not BlockStatement block
				|| ifElse.Condition is not BinaryOperatorExpression { Operator: BinaryOperatorType.IsPattern, Left: { } value,
					Right: DeclarationExpression { Type: { } type, Designation: SingleVariableDesignation designation } }
				|| type.GetResolveResult()?.Type is not { IsReferenceType: true }
				|| ifElse.Annotation<SourceLayoutAnnotation>() is not { } layout
				|| ApplySourceLayout.MethodPoints(ifElse, context) is not { } points)
				return;
			var options = context.Settings.CSharpFormattingOptions;
			string name = designation.Identifier;
			string valueText = value.ToString(options).Trim(), typeText = type.ToString(options).Trim();
			int condition = ("if (" + name + " != null)").Length;
			bool nullable;
			if (Has(layout.StartLine, (typeText + "? " + name + " = " + valueText + " as " + typeText + ";").Length))
				nullable = true;
			else if (Has(layout.StartLine, (typeText + " " + name + " = " + valueText + " as " + typeText + ";").Length))
				nullable = false;
			else
				return;
			if (!Has(layout.StartLine + 1, condition))
				return;
			context.Step("Introduce as with null check", ifElse);
			AstType declaredType = nullable ? new ComposedType { BaseType = type.Clone(), HasNullableSpecifier = true } : type.Clone();
			var declarationValue = new AsExpression(value.Detach(), type.Clone());
			var declaration = new VariableDeclarationStatement(declaredType, name, declarationValue);
			declaration.Variables.Single().CopyAnnotationsFrom(designation);
			var test = new BinaryOperatorExpression(new IdentifierExpression(name).CopyAnnotationsFrom(designation), BinaryOperatorType.InEquality, new NullReferenceExpression());
			ifElse.Condition = test;
			block.Statements.InsertBefore(ifElse, declaration);

			bool Has(int line, int width) => points.Any(sp => sp.StartLine == line && sp.EndLine == line && sp.EndColumn - sp.StartColumn == width);
		}

		/// <summary>
		/// "checked { S1; S2; }": a leading or trailing statement whose sequence points are as wide as its checked
		/// operations written "checked(...)" was outside the block in the source: it moves out, with checked expressions.
		/// </summary>
		void SplitCheckedBlock(CheckedStatement checkedStatement)
		{
			if (checkedStatement.Parent is not BlockStatement)
				return;
			var statements = checkedStatement.Body.Statements.ToList();
			int prefix = 0;
			while (prefix < statements.Count && Preference(statements[prefix]) is true)
				prefix++;
			int suffix = 0;
			while (suffix < statements.Count - prefix && Preference(statements[statements.Count - 1 - suffix]) is true)
				suffix++;
			if (prefix == 0 && suffix == 0)
				return;
			context.Step("Split checked block", checkedStatement);
			foreach (var statement in statements.Take(prefix))
			{
				statement.Remove();
				WrapChecked(statement);
				((BlockStatement)checkedStatement.Parent!).Statements.InsertBefore(checkedStatement, statement);
			}
			foreach (var statement in statements.Skip(statements.Count - suffix).Reverse())
			{
				statement.Remove();
				WrapChecked(statement);
				((BlockStatement)checkedStatement.Parent!).Statements.InsertAfter(checkedStatement, statement);
			}
			if (checkedStatement.Body.Statements.Count == 0)
				checkedStatement.Remove();

			// true: the statement's leaf statements with checked operations all match their widths only with checked(...);
			// false: one matches only without; null: no evidence
			bool? Preference(Statement statement)
			{
				bool? result = null;
				foreach (var leaf in statement.DescendantsAndSelf.OfType<Statement>().Where(s => s is not BlockStatement))
				{
					if (leaf.Annotation<SourceLayoutAnnotation>() is not { Width: > 0 } layout || !TopChecked(leaf).Any())
						continue;
					if (leaf is not (ExpressionStatement or ReturnStatement or VariableDeclarationStatement))
						continue;
					int bare = Width(leaf);
					var clone = (Statement)leaf.Clone();
					WrapChecked(clone);
					int wrapped = Width(clone);
					if (wrapped == layout.Width && bare != layout.Width)
						result ??= true;
					else if (bare == layout.Width)
						return false;
					else
						return null;
				}
				return result;
			}

			int Width(AstNode node)
			{
				var clone = node.Clone();
				clone.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true });
				var text = clone.ToString(context.Settings.CSharpFormattingOptions).Trim();
				return text.IndexOf('\n') >= 0 ? -1 : text.Length;
			}
		}

		/// <summary>The outermost expressions of a statement that need a checked context (not counting nested statements' own).</summary>
		static IEnumerable<Expression> TopChecked(Statement statement)
		{
			return statement.Descendants.OfType<Expression>().Where(e => e.Annotations.Contains(AddCheckedBlocks.CheckedAnnotation)
				&& !e.Ancestors.TakeWhile(a => a != statement).Any(a => a is Statement || a.Annotations.Contains(AddCheckedBlocks.CheckedAnnotation)));
		}

		static void WrapChecked(Statement statement)
		{
			foreach (var leaf in statement.DescendantsAndSelf.OfType<Statement>().ToList())
			{
				foreach (var expression in TopChecked(leaf).ToList())
				{
					var wrapped = new CheckedExpression();
					expression.ReplaceWith(wrapped);
					wrapped.Expression = expression;
				}
			}
		}

		/// <summary>
		/// "if (C) { REST }" as the last statement of a loop body (or of a void method) whose REST starts at the if's own
		/// column was "if (!C) { continue; } REST" (or "return;"): nested statements would be indented one level more.
		/// </summary>
		void IntroduceEarlyExit(IfElseStatement ifElse)
		{
			if (ifElse.FalseStatement is not null || ifElse.TrueStatement is not BlockStatement { Statements.Count: > 0 } rest
				|| ifElse.Parent is not BlockStatement body || body.Statements.LastOrDefault() != ifElse
				|| ifElse.Annotation<SourceLayoutAnnotation>() is not { } layout
				|| rest.Statements.First().Annotation<SourceLayoutAnnotation>() is not { } inner || inner.StartColumn != layout.StartColumn)
				return;
			Statement exit;
			if (body.Parent is WhileStatement or ForStatement or ForeachStatement or DoWhileStatement)
				exit = new ContinueStatement();
			else if (body.Parent is MethodDeclaration { ReturnType: Syntax.PrimitiveType { Keyword: "void" } } or Accessor { Kind: AccessorKind.Setter or AccessorKind.Init or AccessorKind.Adder or AccessorKind.Remover }
				or ConstructorDeclaration)
				exit = new ReturnStatement();
			else
				return;
			context.Step("Introduce early exit", ifElse);
			var statements = rest.Statements.ToList();
			ifElse.Condition = Invert(ifElse.Condition!.Detach());
			ifElse.TrueStatement = new BlockStatement { exit };
			foreach (var statement in statements)
				body.Statements.Add(statement.Detach());
		}

		/// <summary>
		/// "var x = new List&lt;object&gt; { ... }" whose local the PDB types "List&lt;dynamic&gt;": the source created a
		/// "new List&lt;dynamic&gt;" (dynamic exists only in the local's debug info).
		/// </summary>
		void UseDynamicCreationType(VariableDeclarationStatement declaration)
		{
			if (declaration.Variables.Count != 1 || declaration.Variables.Single() is not { Initializer: ObjectCreateExpression { Type: not null } creation } variable
				|| variable.GetILVariable()?.Type is not { } localType || creation.GetResolveResult()?.Type is not { } created
				|| !NormalizeTypeVisitor.TypeErasure.EquivalentTypes(localType, created)
				|| NormalizeTypeVisitor.IgnoreNullabilityAndTuples.EquivalentTypes(localType, created))
				return;
			context.Step("Dynamic creation type", creation);
			creation.Type = context.TypeSystemAstBuilder.ConvertType(localType);
		}

		/// <summary>
		/// "dynamic t = x; dynamic u = t.P; t.P = ++u;" (the compiler's temporaries, one source statement) is "x.P++;".
		/// </summary>
		void CollapseDynamicIncrement(BlockStatement block)
		{
			var statements = block.Statements.ToList();
			for (int i = 0; i + 2 < statements.Count; i++)
			{
				if (statements[i] is not VariableDeclarationStatement { Variables.Count: 1 } first
					|| first.Variables.Single() is not { Initializer: IdentifierExpression or MemberReferenceExpression or ThisReferenceExpression } target
					|| target.GetILVariable() is not { HasGeneratedName: true } targetVariable
					|| statements[i + 1] is not VariableDeclarationStatement { Variables.Count: 1 } second
					|| second.Variables.Single() is not { Initializer: MemberReferenceExpression { Target: IdentifierExpression read } member } value
					|| read.GetILVariable() != targetVariable || value.GetILVariable() is not { HasGeneratedName: true } valueVariable
					|| statements[i + 2] is not ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign,
						Left: MemberReferenceExpression { Target: IdentifierExpression write } written,
						Right: UnaryOperatorExpression { Operator: UnaryOperatorType.Increment or UnaryOperatorType.Decrement, Expression: IdentifierExpression stepped } step } }
					|| write.GetILVariable() != targetVariable || written.MemberName != member.MemberName || stepped.GetILVariable() != valueVariable
					|| block.Descendants.OfType<IdentifierExpression>().Count(id => id.GetILVariable() == targetVariable) != 2
					|| block.Descendants.OfType<IdentifierExpression>().Count(id => id.GetILVariable() == valueVariable) != 1)
					continue;
				context.Step("Collapse dynamic increment", first);
				var operation = new UnaryOperatorExpression(step.Operator == UnaryOperatorType.Increment ? UnaryOperatorType.PostIncrement : UnaryOperatorType.PostDecrement,
					new MemberReferenceExpression(target.Initializer.Detach(), member.MemberName));
				var statement = new ExpressionStatement(operation);
				statement.CopyAnnotationsFrom(first);
				first.ReplaceWith(statement);
				statements[i + 1].Remove();
				statements[i + 2].Remove();
				statements = block.Statements.ToList();
			}
		}

		/// <summary>
		/// "T x = e; S1; S2;" whose sequence point is as wide as "if (e is var x)" and whose next statements are indented
		/// one level more was "if (e is var x) { S1; S2; }" (always true: the compiler dropped the test).
		/// </summary>
		void IntroduceIsVar(VariableDeclarationStatement declaration)
		{
			if (declaration.Parent is not BlockStatement block || declaration.Variables.Count != 1 || declaration.Modifiers != Modifiers.None
				|| declaration.Variables.Single() is not { Initializer: { } value } variable
				|| declaration.Annotation<SourceLayoutAnnotation>() is not { Width: > 0 } layout
				|| layout.Width != ("if (" + value.ToString(context.Settings.CSharpFormattingOptions).Trim() + " is var " + variable.Name + ")").Length)
				return;
			var body = new List<Statement>();
			for (var next = declaration.NextSibling as Statement; next?.Annotation<SourceLayoutAnnotation>() is { } inner
				&& inner.StartColumn == layout.StartColumn + 4 && inner.StartLine > layout.StartLine; next = next.NextSibling as Statement)
				body.Add(next);
			if (body.Count == 0)
				return;
			context.Step("Introduce is var", declaration);
			var designation = new SingleVariableDesignation { Identifier = variable.Name };
			designation.CopyAnnotationsFrom(variable);
			var condition = new BinaryOperatorExpression(value.Detach(), BinaryOperatorType.IsPattern,
				new DeclarationExpression { Type = new SimpleType("var"), Designation = designation });
			var inside = new BlockStatement();
			foreach (var statement in body)
				inside.Statements.Add(statement.Detach());
			var ifElse = new IfElseStatement(condition, inside);
			ifElse.CopyAnnotationsFrom(declaration);
			declaration.ReplaceWith(ifElse);
		}

		/// <summary>
		/// "T x; while (M(out x)) { ... }" where the PDB scopes x to the loop's IL was "while (M(out T x)) { ... }"
		/// (a declaration before the loop would belong to the enclosing block's scope).
		/// </summary>
		void IntroduceOutVarInLoopCondition(WhileStatement loop)
		{
			if (loop.PrevSibling is not VariableDeclarationStatement { Variables.Count: 1, Modifiers: Modifiers.None } declaration
				|| declaration.Variables.Single() is not { Initializer: null } variable || variable.GetILVariable() is not { Kind: VariableKind.Local, HasGeneratedName: false } local
				|| loop.Condition.Descendants.OfType<DirectionExpression>().Where(d => d.FieldDirection == FieldDirection.Out
					&& d.Expression is IdentifierExpression id && id.GetILVariable() == local).ToList() is not { Count: 1 } outs
				|| loop.Ancestors.OfType<EntityDeclaration>().FirstOrDefault()?.Annotation<ILFunction>() is not { } function
				|| (function.MoveNextMethod ?? function.Method) is not { MetadataToken: { IsNil: false, Kind: HandleKind.MethodDefinition } token }
				|| context.DecompileRun.DebugInfoProvider is not { } provider)
				return;
			var scope = provider.GetVariables((MethodDefinitionHandle)token).Where(v => v.Index == local.Index && v.Name == local.SourceName).ToList();
			var ranges = loop.DescendantsAndSelf.SelectMany(n => n.Annotations.OfType<ILInstruction>()).SelectMany(i => i.ILRanges).Where(r => !r.IsEmpty).ToList();
			if (scope.Count != 1 || scope[0].ScopeStart < 0 || ranges.Count == 0
				|| scope[0].ScopeStart < ranges.Min(r => r.Start) || scope[0].ScopeEnd > ranges.Max(r => r.InclusiveEnd) + 1)
				return;
			context.Step("Out variable in loop condition", loop);
			var outVar = new OutVarDeclarationExpression(declaration.Type.Detach(), variable.Name);
			outVar.Variable.CopyAnnotationsFrom(variable);
			outs[0].ReplaceWith(outVar);
			declaration.Remove();
		}

		/// <summary>
		/// "if (!C) { return x; } REST" closing a block, where the source put the exit after REST (its sequence point is on a
		/// later line), was "if (C) { REST } else { return x; }"; as the only statement of an else it is an "else if".
		/// </summary>
		void IntroduceLateExit(IfElseStatement ifElse)
		{
			if (ifElse.FalseStatement is not null || ifElse.TrueStatement is not BlockStatement { Statements.Count: > 0 } exit
				|| exit.Statements.Last() is not (ReturnStatement or ThrowStatement or ContinueStatement or BreakStatement or GotoStatement)
				|| ifElse.Parent is not BlockStatement body || ifElse.NextSibling is null
				|| exit.Statements.First().Annotation<SourceLayoutAnnotation>() is not { } exitLayout)
				return;
			var rest = new List<Statement>();
			for (var next = ifElse.NextSibling as Statement; next is not null; next = next.NextSibling as Statement)
				rest.Add(next);
			var restLines = rest.SelectMany(s => s.DescendantsAndSelf).Select(n => n.Annotation<SourceLayoutAnnotation>()).Where(l => l is not null).ToList();
			if (restLines.Count == 0 || restLines.Any(l => l!.StartLine >= exitLayout.StartLine)
				|| rest.Any(s => s is LabelStatement || s.DescendantsAndSelf.OfType<VariableDeclarationStatement>().Any(d => d.Parent == body)))
				return;
			context.Step("Introduce late exit", ifElse);
			var restBlock = new BlockStatement();
			foreach (var statement in rest)
				restBlock.Statements.Add(statement.Detach());
			ifElse.Condition = IntroduceReturnTernaries.Negate(ifElse.Condition!.Detach());
			ifElse.TrueStatement = restBlock;
			ifElse.FalseStatement = exit.Detach();
			if (body.Parent is IfElseStatement outer && outer.FalseStatement == body && body.Statements.Count == 1)
				body.ReplaceWith(ifElse.Detach());
		}

		/// <summary>
		/// "if (c) { return a; } return b;" anywhere (a switch section, a loop body) whose two statements are one sequence
		/// point is "return c ? a : b;" (the value emitted later in the IL is the "true" one, as for whole bodies).
		/// </summary>
		void IntroduceNestedReturnTernary(IfElseStatement ifElse)
		{
			if (ifElse.FalseStatement is not null || ifElse.Parent is not BlockStatement && ifElse.Parent is not Syntax.SwitchSection
				|| ifElse.NextSibling is not ReturnStatement { Expression: { } whenFalse } next
				|| (ifElse.TrueStatement is BlockStatement { Statements.Count: 1 } block ? block.Statements.Single() : ifElse.TrueStatement)
					is not ReturnStatement { Expression: { } whenTrue }
				|| ifElse.Annotation<SourceLayoutAnnotation>() is not { } first || next.Annotation<SourceLayoutAnnotation>() is not { } second
				|| first.StartLine != second.StartLine || first.StartColumn != second.StartColumn)
				return;
			var trueType = whenTrue.GetResolveResult()?.Type;
			var falseType = whenFalse.GetResolveResult()?.Type;
			if (whenTrue is not NullReferenceExpression && whenFalse is not NullReferenceExpression
				&& (trueType is null || falseType is null || !trueType.Equals(falseType)))
				return;
			context.Step("Introduce nested return ternary", ifElse);
			var condition = ifElse.Condition!.Detach();
			int trueOffset = IntroduceReturnTernaries.StartOffset(whenTrue), falseOffset = IntroduceReturnTernaries.StartOffset(whenFalse);
			Expression ternary = trueOffset >= 0 && falseOffset > trueOffset
				? new ConditionalExpression(IntroduceReturnTernaries.Negate(condition), whenFalse.Detach(), whenTrue.Detach())
				: new ConditionalExpression(condition, whenTrue.Detach(), whenFalse.Detach());
			var statement = new ReturnStatement(ternary);
			statement.CopyAnnotationsFrom(next);
			next.Remove();
			ifElse.ReplaceWith(statement);
		}

		/// <summary>
		/// "if (C) { yield break; } REST" at the end of an iterator, where the yield break has no sequence point (the
		/// source didn't write it: it is the iterator's end), is "if (!C) { REST }".
		/// </summary>
		void RemoveCompilerYieldBreak(YieldBreakStatement yieldBreak)
		{
			if (yieldBreak.Annotation<SourceLayoutAnnotation>() is not null
				|| yieldBreak.Parent is not BlockStatement { Statements.Count: 1 } thenBlock || thenBlock.Parent is not IfElseStatement { FalseStatement: null, Condition: { } condition } ifElse
				|| ifElse.Parent is not BlockStatement body || body.Parent is not (MethodDeclaration or Accessor or LocalFunctionDeclarationStatement))
				return;
			var rest = body.Statements.SkipWhile(s => s != ifElse).Skip(1).ToList();
			if (rest.Count == 0 || rest.Any(s => s.DescendantsAndSelf.OfType<YieldBreakStatement>().Any()))
				return;
			context.Step("Remove compiler yield break", ifElse);
			var inner = new BlockStatement();
			foreach (var statement in rest)
				inner.Statements.Add(statement.Detach());
			ifElse.Condition = Invert(condition.Detach());
			ifElse.TrueStatement = inner;
		}

		/// <summary>
		/// "item2" â†’ "item": a local renamed to avoid a clash gets its source name back (from the iterator/async state
		/// field "&lt;item&gt;5__3", or the PDB local name) when no declaration of that name shares its scope and the name means nothing else there.
		/// </summary>
		void RestoreSourceNames(EntityDeclaration entity)
		{
			var declarations = new List<(AstNode Node, ILVariable Variable, AstNode Scope, Action<string> Rename)>();
			foreach (var node in entity.Descendants)
			{
				switch (node)
				{
					case ForeachStatement { VariableDesignation: SingleVariableDesignation designation } foreachStatement
						when designation.Annotation<ILVariableResolveResult>()?.Variable is { } v:
						declarations.Add((designation, v, foreachStatement, n => designation.Identifier = n));
						break;
					case VariableInitializer { Parent: VariableDeclarationStatement { Parent: BlockStatement scope } } variable
						when variable.GetILVariable() is { } v:
						declarations.Add((variable, v, scope, n => variable.Name = n));
						break;
					case VariableInitializer { Parent: VariableDeclarationStatement { Parent: ForStatement forStatement } } loopVariable
						when loopVariable.GetILVariable() is { } v:
						declarations.Add((loopVariable, v, forStatement, n => loopVariable.Name = n));
						break;
					// "case int i:", "o is string text", "out var x": the enclosing block (or switch section) holds them
					case SingleVariableDesignation { Parent: not ForeachStatement } patternVariable
						when patternVariable.Annotation<ILVariableResolveResult>()?.Variable is { } v
						&& PatternScope(patternVariable) is { } patternScope:
						declarations.Add((patternVariable, v, patternScope, n => patternVariable.Identifier = n));
						break;
					case CatchClause { VariableName.Length: > 0 } catchClause
						when catchClause.Annotation<ILVariableResolveResult>()?.Variable is { } v:
						declarations.Add((catchClause, v, catchClause, n => catchClause.VariableName = n));
						break;
				}
			}
			foreach (var (node, variable, scope, rename) in declarations)
			{
				string current = variable.Name ?? "";
				var source = SourceName(variable) ?? variable.SourceName;
				// a foreach variable "item2" next to a foreach variable "item" of the same type: the clash-avoiding suffix
				if (source is null && variable.Kind == VariableKind.ForeachLocal && current.Length > 1 && char.IsDigit(current[current.Length - 1]))
				{
					string stripped = current.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
					if (stripped.Length > 0 && declarations.Any(d => d.Node != node && GetName(d.Node) == stripped && d.Variable.Type.Equals(variable.Type)))
						source = stripped;
				}
				if (source is null || source == current || !current.StartsWith(source, StringComparison.Ordinal)
					|| !current.Substring(source.Length).All(char.IsDigit))
					continue;
				// no other declaration of the name in an overlapping scope, no other meaning of the name inside the scope
				bool clash = declarations.Any(d => d.Node != node && GetName(d.Node) == source
					&& (d.Scope == scope || d.Scope.Ancestors.Contains(scope) || scope.Ancestors.Contains(d.Scope)))
					|| scope.Descendants.OfType<IdentifierExpression>().Any(id => id.Identifier == source)
					|| entity.Descendants.OfType<ParameterDeclaration>().Any(p => p.Name == source);
				if (clash)
					continue;
				context.Step("Restore source name", node);
				rename(source);
				foreach (var id in entity.Descendants.OfType<IdentifierExpression>().Where(id => id.GetILVariable() == variable))
					id.Identifier = source;
				variable.Name = source;
			}

			// a case label's variables belong to its switch section, others to the block around their statement
			static AstNode? PatternScope(SingleVariableDesignation designation)
			{
				foreach (var section in designation.Ancestors.OfType<Syntax.SwitchSection>())
				{
					if (section.CaseLabels.Any(label => designation.Ancestors.Contains(label)))
						return section;
				}
				return designation.Ancestors.OfType<Statement>().FirstOrDefault()?.Parent;
			}

			static string? GetName(AstNode node) => node switch {
				SingleVariableDesignation d => d.Identifier,
				VariableInitializer v => v.Name,
				CatchClause c => c.VariableName,
				_ => null
			};
		}

		/// <summary>The name the source gave a hoisted local ("&lt;item&gt;5__3" â†’ item).</summary>
		static string? SourceName(ILVariable variable)
		{
			var field = variable.StateMachineField?.Name;
			if (field is null || !field.StartsWith("<", StringComparison.Ordinal))
				return null;
			int end = field.IndexOf('>');
			return end > 1 ? field.Substring(1, end - 1) : null;
		}

		static Expression Invert(Expression condition)
		{
			switch (condition)
			{
				case UnaryOperatorExpression { Operator: UnaryOperatorType.Not } not:
					return not.Expression.Detach();
				case BinaryOperatorExpression { Operator: BinaryOperatorType.Equality or BinaryOperatorType.InEquality } comparison:
					comparison.Operator = comparison.Operator == BinaryOperatorType.Equality ? BinaryOperatorType.InEquality : BinaryOperatorType.Equality;
					return comparison;
				default:
					return new UnaryOperatorExpression(UnaryOperatorType.Not, condition);
			}
		}

		/// <summary>"using (gate.EnterScope()) { ... }" with gate a System.Threading.Lock is what C# 13 makes of "lock (gate) { ... }".</summary>
		void IntroduceLockOnLockObject(UsingStatement usingStatement)
		{
			if (usingStatement.IsAsync || usingStatement.IsEnhanced
				|| usingStatement.ResourceAcquisition is not InvocationExpression { Arguments.Count: 0, Target: MemberReferenceExpression { MemberName: "EnterScope" } member } call
				|| call.GetSymbol() is not IMethod { DeclaringType.FullName: "System.Threading.Lock" })
				return;
			// the source may have written the using itself: then it has a sequence point for "using (gate.EnterScope())";
			// a lock statement has none for its header
			string resource = usingStatement.ResourceAcquisition.ToString(context.Settings.CSharpFormattingOptions).Trim();
			if (usingStatement.Annotation<SourceLayoutAnnotation>() is not { } layout || layout.Width == "using (".Length + resource.Length + 1)
				return;
			context.Step("Introduce lock on Lock", usingStatement);
			var lockStatement = new LockStatement { Expression = member.Target.Detach(), EmbeddedStatement = usingStatement.EmbeddedStatement.Detach() };
			lockStatement.CopyAnnotationsFrom(usingStatement);
			usingStatement.ReplaceWith(lockStatement);
		}

		static readonly HashSet<string> NameAttributes = new HashSet<string> {
			"System.Diagnostics.CodeAnalysis.MemberNotNullAttribute",
			"System.Diagnostics.CodeAnalysis.MemberNotNullWhenAttribute",
			"System.Diagnostics.CodeAnalysis.NotNullIfNotNullAttribute",
			"System.Runtime.CompilerServices.CallerArgumentExpressionAttribute",
		};

		/// <summary>
		/// "[MemberNotNull("cached")]" â†’ "[MemberNotNull(nameof(cached))]": the attributes whose strings name a member or a
		/// parameter are written with nameof (the metadata only has the string).
		/// </summary>
		void UseNameofInAttribute(Syntax.Attribute attribute)
		{
			if (attribute.GetResolveResult()?.Type.FullName is not { } name || !NameAttributes.Contains(name))
				return;
			var owner = attribute.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
			var type = attribute.Ancestors.OfType<TypeDeclaration>().FirstOrDefault()?.GetSymbol() as ITypeDefinition;
			var parameters = owner?.Descendants.OfType<ParameterDeclaration>().Select(p => p.Name ?? "").ToList() ?? new List<string>();
			foreach (var argument in attribute.Arguments.ToList())
			{
				if (argument is not PrimitiveExpression { Value: string text } literal)
					continue;
				bool known = parameters.Contains(text) || type?.GetMembers(m => m.Name == text).Any() == true;
				if (!known)
					continue;
				context.Step("Use nameof in attribute", literal);
				var nameof = new InvocationExpression(new IdentifierExpression("nameof"), new IdentifierExpression(text));
				nameof.CopyAnnotationsFrom(literal);
				literal.ReplaceWith(nameof);
			}
		}

		/// <summary>
		/// "C() : base(x)" on one line or ": base(x)" on the next: the base call's sequence point ("base(x)") starts right
		/// after an indented ": " on its own line, anywhere else on the declaration's line.
		/// </summary>
		void DecideInitializerLine(ConstructorDeclaration constructor, DebugInfo.IDebugInfoProvider provider)
		{
			if (constructor.Initializer is not { } initializer || constructor.GetSymbol() is not IMethod method
				|| method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition
				|| constructor.Ancestors.OfType<TypeDeclaration>().FirstOrDefault()?.GetSymbol() is not ITypeDefinition type)
				return;
			List<DebugInfo.SequencePoint> points;
			try
			{
				points = provider.GetSequencePoints((MethodDefinitionHandle)method.MetadataToken).Where(sp => !sp.IsHidden).ToList();
			}
			catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
			{
				return;
			}
			int width = initializer.ToString(context.Settings.CSharpFormattingOptions).Trim().TrimStart(':').Trim().Length;
			var call = points.OrderBy(sp => sp.Offset).FirstOrDefault(sp => sp.StartLine == sp.EndLine && sp.EndColumn - sp.StartColumn == width);
			if (call is null || ApplySourceLayout.DocumentBlockColumn(context, provider, type, call.DocumentUrl) is not int column)
				return;
			if (call.StartColumn != column + ": ".Length)
				constructor.AddAnnotation(InitializerOnSameLineAnnotation.Instance);
		}

		/// <summary>
		/// A body that became a single statement here: "=> x" if its sequence point is as wide as x, a block if it is as
		/// wide as "return x;".
		/// </summary>
		void DecideBodyStyle(BlockStatement body)
		{
			if (body.Parent is not (MethodDeclaration or Accessor or OperatorDeclaration) || body.Statements.Count != 1
				|| body.Statements.Single() is not { } statement || statement.Annotation<SourceLayoutAnnotation>() is not { Width: > 0 } layout
				|| ExpressionBodiedMembers.GetExpression(body) is not { } expression)
				return;
			// "T P => x;": the getter of a get-only property becomes the property's expression body
			if (body.Parent is Accessor { Kind: AccessorKind.Getter } getter && AnnotateAccessorStyles.StyleOf(body) == AccessorStyle.PropertyExpressionBody
				&& statement is ReturnStatement { Expression: { } value } && getter.Attributes.Count == 0 && (getter.Modifiers & ~Modifiers.Readonly) == 0)
			{
				switch (getter.Parent)
				{
					case PropertyDeclaration { Setter: null } property:
						property.Modifiers |= getter.Modifiers;
						property.ExpressionBody = value.Detach();
						property.CopyAnnotationsFrom(getter);
						getter.Remove();
						return;
					case IndexerDeclaration { Setter: null } indexer:
						indexer.Modifiers |= getter.Modifiers;
						indexer.ExpressionBody = value.Detach();
						indexer.CopyAnnotationsFrom(getter);
						getter.Remove();
						return;
				}
			}
			body.RemoveAnnotations<BodyStyleAnnotation>();
			if (Width(expression) == layout.Width)
				body.AddAnnotation(BodyStyleAnnotation.ExpressionBody);
			else if (Width(statement) == layout.Width)
				body.AddAnnotation(BodyStyleAnnotation.Block);

			int Width(AstNode node)
			{
				var clone = node.Clone();
				clone.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true });
				var text = clone.ToString(context.Settings.CSharpFormattingOptions).Trim();
				return text.IndexOf('\n') >= 0 ? -1 : text.Length;
			}
		}

		/// <summary>
		/// "T t = x; if (t == null) { T a = e; T b = a; x = a; t = b; } return t;" (or the shorter forms) is
		/// "return x ??= e;"
		/// </summary>
		void IntroduceNullCoalescingAssignmentReturn(BlockStatement block)
		{
			if (block.Statements.Count != 3 || block.Statements.ElementAt(0) is not VariableDeclarationStatement { Variables.Count: 1 } declaration
				|| declaration.Variables.Single() is not { Initializer: { } target } result || result.GetILVariable() is not { } resultVariable
				|| !IsSimpleTarget(target)
				|| block.Statements.ElementAt(1) is not IfElseStatement { FalseStatement: null, Condition: BinaryOperatorExpression { Operator: BinaryOperatorType.Equality, Left: IdentifierExpression tested, Right: NullReferenceExpression } } ifElse
				|| tested.GetILVariable() != resultVariable
				|| block.Statements.ElementAt(2) is not ReturnStatement { Expression: IdentifierExpression returned } || returned.GetILVariable() != resultVariable)
				return;
			var inner = (ifElse.TrueStatement as BlockStatement)?.Statements.ToList() ?? new List<Statement> { ifElse.TrueStatement };
			// the value: the first statement's; then "x = <value or a copy>" and "t = <value or a copy>"
			Expression? value = null;
			var copies = new HashSet<ILVariable>();
			bool storedTarget = false, storedResult = false;
			foreach (var statement in inner)
			{
				switch (statement)
				{
					case VariableDeclarationStatement { Variables.Count: 1 } d when d.Variables.Single() is { Initializer: { } init } v && v.GetILVariable() is { HasGeneratedName: true } local:
						if (value is null)
							value = init;
						else if (init is IdentifierExpression copy && copies.Contains(copy.GetILVariable()!))
						{ }
						else
							return;
						copies.Add(local);
						break;
					case ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } assignment }:
						bool isCopy = assignment.Right is IdentifierExpression r && r.GetILVariable() is { } rv && copies.Contains(rv);
						if (assignment.Left is IdentifierExpression l && l.GetILVariable() == resultVariable)
						{
							if (value is null) { value = assignment.Right; copies.Add(resultVariable); }
							else if (!isCopy) return;
							storedResult = true;
						}
						else if (assignment.Left.IsMatch(target) && (isCopy || assignment.Right is IdentifierExpression r2 && r2.GetILVariable() == resultVariable))
						{
							storedTarget = true;
						}
						else
						{
							return;
						}
						break;
					default:
						return;
				}
			}
			if (value is null || !storedTarget || !storedResult)
				return;
			context.Step("Introduce return ??=", block);
			var replacement = new ReturnStatement(new AssignmentExpression(target.Detach(), value.Detach()) { Operator = AssignmentOperatorType.NullCoalescing });
			replacement.CopyAnnotationsFrom(declaration);
			block.Statements.Clear();
			block.Statements.Add(replacement);
			DecideBodyStyle(block);
		}

		/// <summary>
		/// "unsafe" on two or more members (or on a fixed buffer) is written once on the type, as is usual: both
		/// compile alike.
		/// </summary>
		void HoistUnsafe(TypeDeclaration type)
		{
			var members = type.Members.Where(m => (m.Modifiers & Modifiers.Unsafe) != 0).ToList();
			if ((type.Modifiers & Modifiers.Unsafe) != 0 || members.Count < 2 && !members.Any(m => m is FixedFieldDeclaration))
				return;
			context.Step("Unsafe type", type);
			foreach (var member in members)
				member.Modifiers &= ~Modifiers.Unsafe;
			type.Modifiers |= Modifiers.Unsafe;
		}

		/// <summary>"d = (T)Delegate.Combine(d, v)" â†’ "d += v" (and Remove â†’ "-="): what the compiler makes of them.</summary>
		void IntroduceDelegateCompoundAssignment(AssignmentExpression assignment)
		{
			if (assignment.Operator != AssignmentOperatorType.Assign || !IsSimpleTarget(assignment.Left)
				|| assignment.Right is not CastExpression { Expression: InvocationExpression { Arguments.Count: 2 } call } cast
				|| call.GetSymbol() is not IMethod { Name: "Combine" or "Remove", DeclaringType.FullName: "System.Delegate", IsStatic: true } method
				|| !call.Arguments.First().IsMatch(assignment.Left)
				|| !NormalizeTypeVisitor.IgnoreNullability.EquivalentTypes(cast.GetResolveResult().Type, assignment.Left.GetResolveResult().Type))
				return;
			context.Step("Use delegate compound assignment", assignment);
			assignment.Operator = method.Name == "Combine" ? AssignmentOperatorType.Add : AssignmentOperatorType.Subtract;
			assignment.Right = call.Arguments.Last().Detach();
		}

		/// <summary>A local, a parameter or a field of this/a local/a type: reading it twice is the same as once.</summary>
		static bool IsSimpleTarget(Expression e) => e switch {
			IdentifierExpression id => id.GetILVariable() is not null || id.GetSymbol() is IField,
			MemberReferenceExpression { Target: ThisReferenceExpression or TypeReferenceExpression } member => member.GetSymbol() is IField,
			MemberReferenceExpression { Target: IdentifierExpression target } member => member.GetSymbol() is IField && target.GetILVariable() is not null,
			_ => false
		};

		/// <summary>"Check(a > 0, "a > 0")" â†’ "Check(a > 0)" for "[CallerArgumentExpression("condition")] string expression = """.</summary>
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
