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

using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.Disassembler;
using NetSpy.Decompiler.Semantics;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: reconstructs C# 8+ pattern-based switch expressions.
	///
	/// The compiler lowers "x switch { p1 => a, p2 when c => b, _ => d }" into a decision tree, which the
	/// decompiler shows as nested ifs whose every path ends in "return value;" (or "throw").
	/// A statement list "if (C) { THEN } REST" means: the arms of THEN guarded by C, then the arms of REST
	/// (first matching arm wins), which is exactly the semantics of switch expression arms. The conditions
	/// of each arm are then turned back into a pattern on the tested value (the subject); what can't be a
	/// pattern becomes the arm's "when" clause.
	///
	/// Also: "T v; switch (x) { case 1: v = a; break; ... default: v = d; break; }" → "T v = x switch { ... };".
	/// Nothing is changed unless the whole body can be converted.
	/// </summary>
	public sealed class IntroduceSwitchExpressions : DepthFirstAstVisitor, IAstTransform
	{
		TransformContext context = null!;
		bool? isOptimized;

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.SwitchExpressions)
				return;
			this.context = context;
			rootNode.AcceptVisitor(this);
		}

		public override void VisitMethodDeclaration(MethodDeclaration methodDeclaration)
		{
			Process(methodDeclaration.Body, methodDeclaration.GetSymbol() as IMethod, methodDeclaration.HasModifier(Modifiers.Async));
			base.VisitMethodDeclaration(methodDeclaration);
		}

		public override void VisitOperatorDeclaration(OperatorDeclaration operatorDeclaration)
		{
			Process(operatorDeclaration.Body, operatorDeclaration.GetSymbol() as IMethod, false);
			base.VisitOperatorDeclaration(operatorDeclaration);
		}

		public override void VisitAccessor(Accessor accessor)
		{
			Process(accessor.Body, accessor.GetSymbol() as IMethod, false);
			base.VisitAccessor(accessor);
		}

		void Process(BlockStatement? body, IMethod? method, bool isAsync)
		{
			if (body is null)
				return;
			foreach (var block in body.DescendantsAndSelf.OfType<BlockStatement>().ToList())
				IntroduceValueSwitchExpressions(block);
			if (method is not null)
				IntroduceListIsPatterns(body, method);
			if (isAsync || method is null || body.Descendants.Any(n => n is YieldReturnStatement or YieldBreakStatement))
				return;
			var parser = new ArmParser { LineOf = SourceLines(method) };
			var arms = parser.ParseList(body.Statements.ToList(), 0, out bool exhaustive);
			if (arms is null || !exhaustive || arms.Count < 2)
			{
				Trace(method, arms is null ? "not arms" : $"exhaustive={exhaustive} arms={arms.Count}");
				return;
			}
			if (!parser.SawNoMatchThrow && !ReturnsWereMerged(body, method))
			{
				// NetSpy: every arm returns on its own: a pattern switch statement, if the PDB has "switch (subject)"
				if (TryIntroduceSwitchStatement(body, method, arms))
					return;
				Trace(method, $"no evidence (optimized={IsOptimized(method)}, ret={CountRetInstructions(method)})");
				return;
			}
			var switchExpression = new SwitchBuilder().Build(arms);
			if (switchExpression is null)
			{
				Trace(method, "patterns failed: " + Failure);
				return;
			}
			body.Statements.Clear();
			body.Statements.Add(new ReturnStatement { Expression = switchExpression });
		}

		/// <summary>Why the last conversion failed (for <see cref="Trace"/>).</summary>
		[ThreadStatic]
		static string? Failure;

		static readonly bool traceEnabled = Environment.GetEnvironmentVariable("NETSPY_DEBUG_SWITCH") == "1";

		/// <summary>Dev aid: NETSPY_DEBUG_SWITCH=1 prints why a method isn't a switch expression.</summary>
		static void Trace(IMethod method, string reason)
		{
			if (traceEnabled)
				Console.Error.WriteLine($"[switch] {method.FullName}: {reason}");
		}

		/// <summary>
		/// Guard clauses ("if (x == null) return 0; return x.Length;") have one 'ret' per return statement in an
		/// optimized build; the arms of a switch expression all store the result and share a single 'ret'.
		/// </summary>
		/// <summary>
		/// NetSpy: C# 7 pattern switch statements ("switch (o) { case int i when i &gt; 0: return a; ... }") compile to the
		/// same decision tree as a switch expression, but every case returns on its own. The arms become a switch
		/// statement when the PDB proves it: a sequence point spans exactly "switch (subject)".
		/// </summary>
		bool TryIntroduceSwitchStatement(BlockStatement body, IMethod method, List<Arm> arms)
		{
			if (arms.Any(a => a.Result is null && a.Thrown is null))
				return false;
			var points = VisibleSequencePoints(method);
			if (points is null)
				return false;
			// a "when" clause has a sequence point of its own; patterns don't: more points than the header and one
			// statement per case means guards
			var builder = new SwitchBuilder { PreferWhen = points.Count > 1 + arms.Count };
			var switchExpression = builder.Build(arms);
			if (switchExpression is null)
				return false;
			int width = "switch (".Length + switchExpression.Expression.ToString().Length + ")".Length;
			if (!points.Any(sp => sp.StartLine == sp.EndLine && sp.EndColumn - sp.StartColumn == width))
				return false;
			var statement = new SwitchStatement { Expression = switchExpression.Expression.Detach() };
			foreach (var arm in switchExpression.SwitchSections.ToList())
			{
				var label = new CaseLabel();
				bool discard = arm.Pattern is IdentifierExpression { Identifier: "_" };
				if (discard && arm.Condition is not null)
					return false; // "case var _ when c:" isn't what the source had
				if (!discard)
					label.Expression = arm.Pattern.Detach();
				if (arm.Condition is { } condition)
					label.Condition = condition.Detach();
				var section = new SwitchSection();
				section.CaseLabels.Add(label);
				section.Statements.Add(arm.Body is ThrowExpression thrown
					? new ThrowStatement(thrown.Expression.Detach())
					: new ReturnStatement(arm.Body.Detach()));
				statement.SwitchSections.Add(section);
			}
			context.Step("Introduce pattern switch statement", body);
			body.Statements.Clear();
			body.Statements.Add(statement);
			return true;
		}

		/// <summary>
		/// NetSpy: "if (x is [var a, ..]) { ... }" compiles to
		/// <code>
		/// if (x != null &amp;&amp; x.Length &gt;= 1) { int a = x[0]; ... }
		/// </code>
		/// The condition and the element reads at the start of the block are one arm with a list pattern.
		/// Only done when the PDB has a sequence point exactly as wide as "if (x is [var a, ..])".
		/// </summary>
		void IntroduceListIsPatterns(BlockStatement body, IMethod method)
		{
			List<DebugInfo.SequencePoint>? points = null;
			foreach (var ifElse in body.Descendants.OfType<IfElseStatement>().ToList())
			{
				if (ifElse.TrueStatement is not BlockStatement block || ifElse.Condition is not { } condition)
					continue;
				var bindings = block.Statements.TakeWhile(s => s is VariableDeclarationStatement { Variables.Count: 1, Modifiers: Modifiers.None } d
					&& d.Variables.Single().Initializer is IndexerExpression or IdentifierExpression).ToList();
				if (bindings.Count == 0 || bindings[0] is not VariableDeclarationStatement { } first || first.Variables.Single().Initializer is not IndexerExpression)
					continue;
				var names = bindings.Select(b => ((VariableDeclarationStatement)b).Variables.Single().Name).ToList();
				// the names the rest of the block uses get a designation in the pattern
				var used = block.Statements.Except(bindings).SelectMany(s => s.DescendantsAndSelf).OfType<IdentifierExpression>()
					.Where(id => names.Contains(id.Identifier)).Select(id => id.Identifier).Distinct().ToList();
				var arm = new Arm { Result = new InvocationExpression(new IdentifierExpression("_"), used.Select(n => new IdentifierExpression(n))) };
				arm.Path.Add(new ConditionItem(condition, negated: false));
				arm.Path.AddRange(bindings.Select(b => new BindingItem(b)));
				var built = new SwitchBuilder().Build(new List<Arm> { arm });
				if (built?.SwitchSections.SingleOrDefault() is not { Condition: null, Pattern: { } pattern }
					|| !pattern.DescendantsAndSelf.Any(n => n is CollectionExpression)
					|| built.Expression is not IdentifierExpression { Identifier: var subjectName })
					continue;
				var subject = condition.DescendantsAndSelf.OfType<IdentifierExpression>().FirstOrDefault(id => id.Identifier == subjectName);
				if (subject is null)
					continue;
				var isPattern = new BinaryOperatorExpression(subject.Clone(), BinaryOperatorType.IsPattern, pattern.Detach());
				points ??= VisibleSequencePoints(method);
				int width = "if (".Length + isPattern.ToString().Length + ")".Length;
				if (points is null || !points.Any(sp => sp.StartLine == sp.EndLine && sp.EndColumn - sp.StartColumn == width))
					continue;
				context.Step("Introduce list pattern", ifElse);
				ifElse.Condition = isPattern;
				foreach (var binding in bindings)
					binding.Remove();
			}
		}

		/// <summary>The method's visible sequence points; null without a PDB.</summary>
		List<DebugInfo.SequencePoint>? VisibleSequencePoints(IMethod method)
		{
			if (!context.Settings.SourceLayoutFromPdb || context.DecompileRun.DebugInfoProvider is not { } provider
				|| method.MetadataToken.IsNil || method.MetadataToken.Kind != System.Reflection.Metadata.HandleKind.MethodDefinition)
				return null;
			try
			{
				return provider.GetSequencePoints((System.Reflection.Metadata.MethodDefinitionHandle)method.MetadataToken)
					.Where(sp => !sp.IsHidden).ToList();
			}
			catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
			{
				return null;
			}
		}

		/// <summary>
		/// NetSpy: maps an expression to the source line of the sequence point its first IL instruction is in
		/// (-1 if unknown or hidden); null without a PDB.
		/// </summary>
		Func<Expression, int>? SourceLines(IMethod method)
		{
			if (!context.Settings.SourceLayoutFromPdb || context.DecompileRun.DebugInfoProvider is not { } provider
				|| method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition)
				return null;
			List<DebugInfo.SequencePoint> points;
			try
			{
				points = provider.GetSequencePoints((MethodDefinitionHandle)method.MetadataToken).OrderBy(sp => sp.Offset).ToList();
			}
			catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
			{
				return null;
			}
			if (points.Count == 0)
				return null;
			return expression => {
				int offset = StartOffset(expression);
				if (offset < 0)
					return -1;
				var point = points.LastOrDefault(sp => sp.Offset <= offset);
				return point is null || point.IsHidden ? -1 : point.StartLine;
			};
		}

		/// <summary>The lowest IL offset of the instructions an expression was built from (-1 if unknown).</summary>
		static int StartOffset(Expression expression)
		{
			int min = int.MaxValue;
			foreach (var node in expression.DescendantsAndSelf)
			{
				foreach (var inst in node.Annotations.OfType<IL.ILInstruction>())
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

		bool ReturnsWereMerged(BlockStatement body, IMethod method)
		{
			int returnStatements = body.Descendants.OfType<ReturnStatement>().Count(r => !IsInsideLambda(r, body));
			if (returnStatements < 2 || !IsOptimized(method))
				return false;
			return CountRetInstructions(method) == 1;
		}

		static bool IsInsideLambda(AstNode node, AstNode root)
		{
			for (var n = node.Parent; n != null && n != root; n = n.Parent)
			{
				if (n is LambdaExpression or AnonymousMethodExpression or LocalFunctionDeclarationStatement)
					return true;
			}
			return false;
		}

		bool IsOptimized(IMethod method)
		{
			if (isOptimized is { } value)
				return value;
			value = true;
			foreach (var attr in method.ParentModule!.GetAssemblyAttributes())
			{
				if (attr.AttributeType.FullName != "System.Diagnostics.DebuggableAttribute")
					continue;
				// DebuggableAttribute(DebuggingModes) or DebuggableAttribute(bool isJITTrackingEnabled, bool isJITOptimizerDisabled)
				if (attr.FixedArguments.Length == 1 && attr.FixedArguments[0].Value is int modes)
					value = (modes & 0x100) == 0; // DebuggingModes.DisableOptimizations
				else if (attr.FixedArguments.Length == 2 && attr.FixedArguments[1].Value is bool disabled)
					value = !disabled;
			}
			isOptimized = value;
			return value;
		}

		static int CountRetInstructions(IMethod method)
		{
			if (method.ParentModule is not MetadataModule module || method.MetadataToken.IsNil
				|| method.MetadataToken.Kind != HandleKind.MethodDefinition)
				return -1;
			var file = module.MetadataFile;
			var definition = file.Metadata.GetMethodDefinition((MethodDefinitionHandle)method.MetadataToken);
			if (definition.RelativeVirtualAddress == 0)
				return -1;
			var reader = file.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
			int count = 0;
			while (reader.RemainingBytes > 0)
			{
				var opCode = reader.DecodeOpCode();
				if (opCode == ILOpCode.Ret)
					count++;
				reader.SkipOperand(opCode);
			}
			return count;
		}

		#region Value switches: T v; switch (x) { case k: v = e; break; ... } → T v = x switch { k => e, ... };

		static void IntroduceValueSwitchExpressions(BlockStatement block)
		{
			var statements = block.Statements.ToList();
			for (int i = 0; i < statements.Count; i++)
			{
				if (statements[i] is not SwitchStatement switchStatement || !MatchValueSwitch(switchStatement, out var variable, out var sections))
					continue;
				var switchExpression = new SwitchExpression { Expression = switchStatement.Expression.Clone() };
				SwitchExpressionSection? defaultSection = null;
				foreach (var (labels, value) in sections)
				{
					var section = new SwitchExpressionSection { Body = value.Clone() };
					if (labels.Any(l => l.Expression is null))
					{
						section.Pattern = new IdentifierExpression("_");
						defaultSection = section;
						continue;
					}
					Expression? pattern = null;
					foreach (var label in labels)
						pattern = pattern is null ? label.Expression!.Clone() : new BinaryOperatorExpression(pattern, BinaryOperatorType.PatternOr, label.Expression!.Clone());
					section.Pattern = pattern!;
					switchExpression.SwitchSections.Add(section);
				}
				if (defaultSection is null)
					continue; // without a default the variable may stay unassigned
				switchExpression.SwitchSections.Add(defaultSection);

				// "T v;" right before the switch becomes "T v = x switch { ... };"
				Statement replacement;
				if (i > 0 && statements[i - 1] is VariableDeclarationStatement { Variables.Count: 1 } declaration
					&& declaration.Variables.Single() is { Initializer: null } declared && declared.Name == variable)
				{
					// "T v = ...; return v;" → "return ...;" when v isn't used anywhere else
					if (i + 1 < statements.Count && statements[i + 1] is ReturnStatement { Expression: IdentifierExpression returned }
						&& returned.Identifier == variable
						&& block.Descendants.OfType<IdentifierExpression>().Count(id => id.Identifier == variable) == sections.Count + 1)
					{
						declaration.Remove();
						switchStatement.Remove();
						returned.ReplaceWith(switchExpression);
						continue;
					}
					declared.Initializer = switchExpression;
					switchStatement.Remove();
					continue;
				}
				replacement = new ExpressionStatement(new AssignmentExpression(new IdentifierExpression(variable), switchExpression));
				switchStatement.ReplaceWith(replacement);
			}
		}

		/// <summary>
		/// Every section is "v = value; break;" for the same local v, every label is a constant or 'default'.
		/// </summary>
		static bool MatchValueSwitch(SwitchStatement switchStatement, out string variable, out List<(List<CaseLabel> Labels, Expression Value)> sections)
		{
			variable = "";
			sections = new List<(List<CaseLabel>, Expression)>();
			foreach (var section in switchStatement.SwitchSections)
			{
				var statements = section.Statements.ToList();
				if (statements.Count != 2 || statements[1] is not BreakStatement
					|| statements[0] is not ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign, Left: IdentifierExpression target } assignment })
					return false;
				if (target.GetILVariable() is null || (variable.Length > 0 && variable != target.Identifier))
					return false;
				variable = target.Identifier;
				if (section.CaseLabels.Any(l => l.Expression is not null && !IsConstant(l.Expression)))
					return false;
				sections.Add((section.CaseLabels.ToList(), assignment.Right));
			}
			return sections.Count > 1;
		}

		#endregion

		#region Arm detection

		abstract class PathItem { }

		/// <summary>The arm is only reached if <see cref="Condition"/> is true (false if <see cref="Negated"/>).</summary>
		sealed class ConditionItem : PathItem
		{
			public readonly Expression Condition;
			public readonly bool Negated;
			/// <summary>
			/// Known from the arm order (an earlier sibling arm took the other inputs), so it isn't needed in the
			/// pattern; it only tightens a bound the arm already has ("&gt;= -10" + "not &lt; 10" → "&gt;= 10").
			/// </summary>
			public readonly bool Implied;

			public ConditionItem(Expression condition, bool negated, bool implied = false)
			{
				Condition = condition;
				Negated = negated;
				Implied = implied;
			}
		}

		/// <summary>A local declared on the arm's path ("T a = x.Prop;", "x.Deconstruct(out var a, out var b);").</summary>
		sealed class BindingItem : PathItem
		{
			public readonly Statement Statement;

			public BindingItem(Statement statement) => Statement = statement;
		}

		sealed class Arm
		{
			public List<PathItem> Path = new List<PathItem>();
			public Expression? Result;
			public Expression? Thrown;

			public Arm Prepend(PathItem item) => new Arm { Path = new[] { item }.Concat(Path).ToList(), Result = Result, Thrown = Thrown };
		}

		sealed class ArmParser
		{
			public bool SawNoMatchThrow;

			/// <summary>
			/// NetSpy: the source line of an arm's result (from the PDB; -1 if unknown). Roslyn emits every arm's value
			/// with a sequence point of its own, so the lines give back the source order of the arms.
			/// </summary>
			public Func<Expression, int>? LineOf;

			/// <summary>The first source line of the arms; null if any line is unknown.</summary>
			int? MinLine(List<Arm> arms)
			{
				if (LineOf is null || arms.Count == 0)
					return null;
				int min = int.MaxValue;
				foreach (var arm in arms)
				{
					int line = LineOf((arm.Result ?? arm.Thrown)!);
					if (line < 0)
						return null;
					min = Math.Min(min, line);
				}
				return min;
			}

			/// <summary>
			/// Both orders of "C => FIRST, SECOND" are valid when both sides catch everything they get; the source order
			/// comes from the PDB lines, else from the shape of the condition (the decompiler inverts "!A" tests).
			/// </summary>
			bool SecondComesFirst(List<Arm> first, List<Arm> second, bool negative)
			{
				return MinLine(first) is int a && MinLine(second) is int b && a != b ? b < a : negative;
			}

			public List<Arm>? ParseStatement(Statement statement, out bool exhaustive)
			{
				if (statement is BlockStatement block)
					return ParseList(block.Statements.ToList(), 0, out exhaustive);
				return ParseList(new List<Statement> { statement }, 0, out exhaustive);
			}

			/// <summary>
			/// The arms of statements[index..]; null if they aren't arms. <paramref name="exhaustive"/> = no path falls through.
			/// </summary>
			public List<Arm>? ParseList(List<Statement> statements, int index, out bool exhaustive)
			{
				exhaustive = false;
				if (index >= statements.Count)
					return new List<Arm>();
				switch (statements[index])
				{
					case ReturnStatement { Expression: { } result }:
						exhaustive = true;
						return new List<Arm> { new Arm { Result = result } };
					case ThrowStatement { Expression: { } thrown }:
						exhaustive = true;
						if (IsNoMatchException(thrown))
						{
							// the compiler's "no arm matched"; it's implicit in a switch expression
							SawNoMatchThrow = true;
							return new List<Arm>();
						}
						return new List<Arm> { new Arm { Thrown = thrown } };
					case VariableDeclarationStatement { Variables.Count: 1, Modifiers: Modifiers.None } declaration
						when declaration.Variables.Single().Initializer is not null:
					case ExpressionStatement { Expression: InvocationExpression { Target: MemberReferenceExpression { MemberName: "Deconstruct" } } }:
					{
						var binding = new BindingItem(statements[index]);
						var rest = ParseList(statements, index + 1, out exhaustive);
						return rest?.Select(a => a.Prepend(binding)).ToList();
					}
					case IfElseStatement ifElse:
						return ParseIf(ifElse, statements, index, out exhaustive);
					case SwitchStatement switchStatement:
						return ParseSwitch(switchStatement, statements, index, out exhaustive);
					default:
						return null;
				}
			}

			/// <summary>
			/// NetSpy: a switch on constants inside the decision tree (list patterns: "switch (values.Length)").
			/// Each section is "subject == k1 || subject == k2"; 'default' takes what the cases didn't, so its arms go last.
			/// Every section has to return or throw on all paths.
			/// </summary>
			List<Arm>? ParseSwitch(SwitchStatement switchStatement, List<Statement> statements, int index, out bool exhaustive)
			{
				exhaustive = false;
				// only the compiler's own switch (on a temporary or a length); a user's switch statement stays one
				bool compilerSwitch = switchStatement.Expression switch {
					IdentifierExpression id => id.GetILVariable() is { HasGeneratedName: true },
					MemberReferenceExpression { MemberName: "Length" or "Count" } => true,
					_ => false
				};
				if (!compilerSwitch)
					return null;
				var cases = new List<(List<Arm> Arms, int Line, int Order)>();
				List<Arm>? defaultArms = null;
				foreach (var section in switchStatement.SwitchSections)
				{
					var body = section.Statements.ToList();
					var arms = body.Count == 1 ? ParseStatement(body[0], out bool sectionExhaustive) : ParseList(body, 0, out sectionExhaustive);
					if (arms is null || !sectionExhaustive)
						return null;
					if (section.CaseLabels.Any(l => l.Expression is null))
					{
						defaultArms = arms;
						continue;
					}
					if (section.CaseLabels.Any(l => l.Condition is not null || !IsConstant(l.Expression!)))
						return null;
					var condition = section.CaseLabels
						.Select(l => (Expression)new BinaryOperatorExpression(switchStatement.Expression.Clone(), BinaryOperatorType.Equality, l.Expression!.Clone()))
						.Aggregate((a, b) => new BinaryOperatorExpression(a, BinaryOperatorType.ConditionalOr, b));
					cases.Add((Prefix(arms, new ConditionItem(condition, negated: false)).ToList(), MinLine(arms) ?? int.MaxValue, cases.Count));
				}
				// the cases test different values: any order is valid, the source one is the best
				var result = cases.OrderBy(c => c.Line).ThenBy(c => c.Order).SelectMany(c => c.Arms).ToList();
				if (defaultArms is not null)
				{
					result.AddRange(defaultArms);
					exhaustive = true;
					return result;
				}
				var rest = ParseList(statements, index + 1, out exhaustive);
				if (rest is null)
					return null;
				result.AddRange(rest);
				return result;
			}

			List<Arm>? ParseIf(IfElseStatement ifElse, List<Statement> statements, int index, out bool exhaustive)
			{
				exhaustive = false;
				var condition = ifElse.Condition;
				bool negative = IsNegative(condition);
				var thenArms = ParseStatement(ifElse.TrueStatement, out bool thenExhaustive);
				if (thenArms is null)
					return null;
				var result = new List<Arm>();
				if (ifElse.FalseStatement is null)
				{
					var restArms = ParseList(statements, index + 1, out bool restExhaustive);
					if (restArms is null)
						return null;
					if (thenExhaustive && restExhaustive && SecondComesFirst(thenArms, restArms, negative))
					{
						// "if (!A) { THEN } REST" → "A => REST" first: REST catches every A input, so THEN arms need no guard
						result.AddRange(Prefix(restArms, new ConditionItem(condition, negated: true)));
						result.AddRange(Prefix(thenArms, new ConditionItem(condition, negated: false, implied: true)));
						exhaustive = true;
						return result;
					}
					result.AddRange(Prefix(thenArms, new ConditionItem(condition, negated: false)));
					// NetSpy: REST is only reached when C was false; that can tighten REST's bounds
					result.AddRange(thenExhaustive ? Prefix(restArms, new ConditionItem(condition, negated: true, implied: true)) : restArms);
					exhaustive = restExhaustive;
					return result;
				}
				var elseArms = ParseStatement(ifElse.FalseStatement, out bool elseExhaustive);
				if (elseArms is null)
					return null;
				// "if (!A) X else Y" is written as "if (A) Y else X"
				bool swap = thenExhaustive && elseExhaustive ? SecondComesFirst(thenArms, elseArms, negative) : negative;
				var (first, firstExhaustive, second) = swap ? (elseArms, elseExhaustive, thenArms) : (thenArms, thenExhaustive, elseArms);
				result.AddRange(Prefix(first, new ConditionItem(condition, negated: swap)));
				// if the first branch always returns, the second one needs no guard
				result.AddRange(firstExhaustive ? second : Prefix(second, new ConditionItem(condition, negated: !swap)));
				if (thenExhaustive && elseExhaustive)
				{
					exhaustive = true;
					return result;
				}
				var remaining = ParseList(statements, index + 1, out exhaustive);
				if (remaining is null)
					return null;
				result.AddRange(remaining);
				return result;
			}

			static IEnumerable<Arm> Prefix(IEnumerable<Arm> arms, PathItem item) => arms.Select(a => a.Prepend(item));
		}

		/// <summary>"!x", "a != b", "a != b &amp;&amp; c != d", "x is not T": the decompiler inverted the condition.</summary>
		static bool IsNegative(Expression condition)
		{
			return condition switch {
				UnaryOperatorExpression { Operator: UnaryOperatorType.Not } => true,
				BinaryOperatorExpression { Operator: BinaryOperatorType.InEquality } => true,
				BinaryOperatorExpression { Operator: BinaryOperatorType.ConditionalAnd } and => IsNegative(and.Left!) && IsNegative(and.Right!),
				BinaryOperatorExpression { Operator: BinaryOperatorType.IsPattern, Right: UnaryOperatorExpression { Operator: UnaryOperatorType.PatternNot } } => true,
				_ => false
			};
		}

		static bool IsNoMatchException(Expression thrown)
		{
			return thrown is ObjectCreateExpression oce
				&& oce.GetResolveResult().Type.FullName == "System.Runtime.CompilerServices.SwitchExpressionException";
		}

		static bool IsConstant(Expression expression)
		{
			return expression is NullReferenceExpression || expression.GetResolveResult() is { IsCompileTimeConstant: true };
		}

		#endregion

		#region Pattern building

		/// <summary>
		/// One value reachable from the subject: the subject itself, a property of it ("Address"), or an element
		/// of its deconstruction; with everything the arm requires of that value.
		/// </summary>
		sealed class PNode
		{
			public PNode? Parent;
			public string? MemberName;
			public IType? Type;
			public AstType? TypeSyntax;
			public bool NotNull;
			public readonly List<Expression> Atoms = new List<Expression>();
			public readonly List<PNode> Members = new List<PNode>();
			public List<PNode>? Positional;
			/// <summary>Names of locals that are this value (declared on the path or by a designation).</summary>
			public readonly List<string> Names = new List<string>();
			public string? Designation;
			/// <summary>NetSpy: the local the designation declares (for its PDB name).</summary>
			public IL.ILVariable? DesignationVariable;
			/// <summary>NetSpy: list pattern elements read as "x[i]" (by index) and "x[x.Length - i]" (by distance from the end).</summary>
			public SortedDictionary<int, PNode>? Elements, ElementsFromEnd;
			/// <summary>NetSpy: the "x[start..^end]" slice of a list pattern.</summary>
			public PNode? Slice;
			public int SliceStart, SliceEnd;
			/// <summary>NetSpy: another arm reads elements of this value, so a test of its length is a list pattern too ("[]").</summary>
			public bool ListMode;
			/// <summary>NetSpy: the type of the expression the value was read with (before any type test).</summary>
			public IType? StaticType;

			public bool HasListParts => Elements is not null || ElementsFromEnd is not null || Slice is not null;

			public PNode Element(int index, bool fromEnd)
			{
				var elements = fromEnd ? (ElementsFromEnd ??= new SortedDictionary<int, PNode>()) : (Elements ??= new SortedDictionary<int, PNode>());
				if (!elements.TryGetValue(index, out var element))
				{
					element = new PNode { Parent = this };
					elements.Add(index, element);
				}
				return element;
			}

			public PNode? SliceOf(int start, int end)
			{
				if (Slice is null)
				{
					Slice = new PNode { Parent = this };
					SliceStart = start;
					SliceEnd = end;
				}
				return SliceStart == start && SliceEnd == end ? Slice : null;
			}

			/// <summary>The "Length"/"Count" member, if the arm uses it.</summary>
			public PNode? LengthMember => Members.FirstOrDefault(m => m.MemberName is "Length" or "Count");

			public PNode Member(string name)
			{
				var member = Members.FirstOrDefault(m => m.MemberName == name);
				if (member is null)
				{
					member = new PNode { Parent = this, MemberName = name };
					Members.Add(member);
				}
				return member;
			}
		}

		sealed class SwitchBuilder
		{
			readonly List<string> rootOrder = new List<string>();

			/// <summary>
			/// NetSpy: only type and null tests become patterns; every other condition stays a "when" clause
			/// (C# 7 switch statements: "case int i when i &gt; 100:").
			/// </summary>
			public bool PreferWhen;

			public SwitchExpression? Build(List<Arm> arms)
			{
				var built = new List<(Dictionary<string, PNode> Roots, List<Expression> When, Arm Arm)>();
				foreach (var arm in arms)
				{
					var armBuilder = new ArmBuilder(this);
					if (!armBuilder.Process(arm))
						return null;
					built.Add((armBuilder.Roots, armBuilder.When, arm));
				}
				// the subject: the local(s) tested by patterns
				var subjects = rootOrder.Where(name => built.Any(b => b.Roots.TryGetValue(name, out var root) && HasConstraint(root))).ToList();
				if (subjects.Count == 0)
				{
					Failure = "no subject";
						return null;
				}
				Expression subject;
				if (subjects.Count == 1)
				{
					subject = new IdentifierExpression(subjects[0]);
				}
				else
				{
					var tuple = new TupleExpression();
					tuple.Elements.AddRange(subjects.Select(s => new IdentifierExpression(s)));
					subject = tuple;
				}
				// NetSpy: if an arm reads elements of a subject, the length tests of the other arms are list patterns too
				foreach (var s in subjects)
				{
					var nodes = built.Select(b => b.Roots.TryGetValue(s, out var root) ? root : null).OfType<PNode>().ToList();
					if (nodes.Any(n => n.HasListParts))
						nodes.ForEach(n => n.ListMode = true);
				}
				try
				{
					return Emit(subject, subjects, built);
				}
				catch (PatternFailure failure)
				{
					Failure = failure.Message;
					return null;
				}
			}

			static SwitchExpression Emit(Expression subject, List<string> subjects, List<(Dictionary<string, PNode> Roots, List<Expression> When, Arm Arm)> built)
			{
				var switchExpression = new SwitchExpression { Expression = subject };
				foreach (var (roots, when, arm) in built)
				{
					var patterns = subjects.Select(s => roots.TryGetValue(s, out var root) ? IntroduceSwitchExpressions.Emit(root) : null).ToList();
					Expression pattern;
					if (patterns.All(p => p is null))
						pattern = new IdentifierExpression("_");
					else if (subjects.Count == 1)
						pattern = patterns[0]!;
					else
					{
						var tuplePattern = new RecursivePatternExpression { IsPositional = true };
						tuplePattern.SubPatterns.AddRange(patterns.Select(p => p ?? new IdentifierExpression("_")));
						pattern = tuplePattern;
					}
					var section = new SwitchExpressionSection {
						Pattern = pattern,
						Body = arm.Result is not null ? arm.Result.Clone() : new ThrowExpression { Expression = arm.Thrown!.Clone() }
					};
					if (when.Count > 0)
						section.Condition = when.Aggregate((a, b) => new BinaryOperatorExpression(a, BinaryOperatorType.ConditionalAnd, b));
					switchExpression.SwitchSections.Add(section);
				}
				return switchExpression;
			}

			public void SeeRoot(string name)
			{
				if (!rootOrder.Contains(name))
					rootOrder.Add(name);
			}

			static bool HasConstraint(PNode node) => node.Type is not null || node.NotNull || node.Atoms.Count > 0
				|| node.Members.Any(HasConstraint) || node.Positional?.Any(HasConstraint) == true;
		}

		sealed class ArmBuilder
		{
			readonly SwitchBuilder owner;
			public readonly Dictionary<string, PNode> Roots = new Dictionary<string, PNode>();
			public readonly List<Expression> When = new List<Expression>();
			readonly Dictionary<string, PNode> bound = new Dictionary<string, PNode>();
			/// <summary>NetSpy: <see cref="GetNode"/> only finds existing nodes (for implied conditions).</summary>
			bool lookupOnly;

			public ArmBuilder(SwitchBuilder owner) => this.owner = owner;

			public bool Process(Arm arm)
			{
				foreach (var item in arm.Path)
				{
					if (item is BindingItem binding)
					{
						if (!Bind(binding.Statement))
							return false;
					}
					else if (item is ConditionItem { Implied: false } condition)
					{
						AddCondition(condition.Condition, condition.Negated);
					}
				}
				foreach (var implied in arm.Path.OfType<ConditionItem>().Where(c => c.Implied))
					TightenBound(implied.Condition, implied.Negated);
				// locals used by the result or by the when clause need a designation in the pattern
				var used = new List<AstNode>();
				if (arm.Result is not null)
					used.Add(arm.Result);
				if (arm.Thrown is not null)
					used.Add(arm.Thrown);
				used.AddRange(When);
				foreach (var id in used.SelectMany(u => u.DescendantsAndSelf).OfType<IdentifierExpression>())
				{
					if (!bound.TryGetValue(id.Identifier, out var node) || Roots.ContainsKey(id.Identifier))
						continue;
					if (node.Designation is not null && node.Designation != id.Identifier)
					{
						Failure = "two names for one value: " + id.Identifier;
							return false;
					}
					node.Designation = id.Identifier;
					node.DesignationVariable ??= id.GetILVariable();
				}
				return true;
			}

			bool Bind(Statement statement)
			{
				if (statement is VariableDeclarationStatement declaration)
				{
					var variable = declaration.Variables.Single();
					var node = GetNode(variable.Initializer!);
					if (node is null)
					{
						Failure = "binding " + variable;
							return false;
					}
					var initializerType = variable.Initializer!.GetResolveResult().Type;
					if (!declaration.Type.IsVar() && declaration.Type.GetResolveResult() is { Type: var declaredType }
						&& declaredType.Kind != TypeKind.Unknown && !NormalizeTypeVisitor.IgnoreNullability.EquivalentTypes(declaredType, initializerType))
					{
						Failure = "binding conversion " + variable;
							return false; // a conversion, not just a name for the value
					}
					node.Names.Add(variable.Name);
					bound[variable.Name] = node;
					return true;
				}
				// x.Deconstruct(out var a, out var b)
				var invocation = (InvocationExpression)((ExpressionStatement)statement).Expression;
				var target = GetNode(((MemberReferenceExpression)invocation.Target).Target);
				if (target is null || target.Positional is not null)
					return false;
				target.Positional = new List<PNode>();
				foreach (var argument in invocation.Arguments)
				{
					var element = new PNode { Parent = target };
					target.Positional.Add(element);
					string? name = argument switch {
						OutVarDeclarationExpression outVar => outVar.Variable.Name,
						DirectionExpression { FieldDirection: FieldDirection.Out, Expression: DeclarationExpression { Designation: SingleVariableDesignation designation } } => designation.Identifier,
						DirectionExpression { FieldDirection: FieldDirection.Out, Expression: IdentifierExpression { Identifier: "_" } } => "_",
						_ => null
					};
					if (name is null)
					{
						Failure = "deconstruct argument " + argument;
						return false;
					}
					if (name != "_")
					{
						element.Names.Add(name);
						bound[name] = element;
					}
				}
				return true;
			}

			/// <summary>The node for a value read from the subject (locals, properties, casts); null otherwise.</summary>
			PNode? GetNode(Expression expression)
			{
				switch (expression)
				{
					case IdentifierExpression { TypeArguments.Count: 0 } id:
						if (bound.TryGetValue(id.Identifier, out var node))
							return node;
						if (id.GetILVariable() is null)
							return null;
						if (!Roots.TryGetValue(id.Identifier, out node))
						{
							if (lookupOnly)
								return null;
							node = new PNode { StaticType = id.GetResolveResult().Type };
							Roots.Add(id.Identifier, node);
						}
						owner.SeeRoot(id.Identifier);
						return node;
					case MemberReferenceExpression { TypeArguments.Count: 0 } member when member.GetSymbol() is IProperty or IField:
						var container = GetNode(member.Target);
						if (lookupOnly)
							return container?.Members.FirstOrDefault(m => m.MemberName == member.MemberName);
						var memberNode = container?.Member(member.MemberName);
						if (memberNode is not null)
							memberNode.StaticType ??= member.GetResolveResult().Type;
						return memberNode;
					case IndexerExpression { Target: { } target, Arguments.Count: 1 } indexer:
						return GetListPart(target, indexer.Arguments.Single());
					case CastExpression cast:
						var castNode = GetNode(cast.Expression);
						if (castNode is null || castNode.Type is null)
							return null; // only a cast right after a type test is a type test
						Narrow(castNode, cast.Type, cast.GetResolveResult().Type);
						return castNode;
					default:
						return null;
				}
			}

			/// <summary>
			/// NetSpy: "x[2]", "x[n - 1]" (n = x.Length), "x[^1]" and "x[2..^1]" as list pattern parts of x.
			/// A list pattern always tests the length first, so the length must have been read on the arm's path:
			/// an element read without it isn't a list pattern (it could throw, a pattern can't).
			/// </summary>
			PNode? GetListPart(Expression target, Expression argument)
			{
				var list = GetNode(target);
				if (list?.LengthMember is not { } length)
					return null;
				switch (argument)
				{
					case PrimitiveExpression { Value: int index } when index >= 0:
						return Element(list, index, fromEnd: false);
					case UnaryOperatorExpression { Operator: UnaryOperatorType.IndexFromEnd, Expression: PrimitiveExpression { Value: int fromEnd } } when fromEnd > 0:
						return Element(list, fromEnd, fromEnd: true);
					case BinaryOperatorExpression { Operator: BinaryOperatorType.Subtract, Left: { } count, Right: PrimitiveExpression { Value: int fromEnd } }
						when fromEnd > 0 && IsLengthRead(count, length):
						return Element(list, fromEnd, fromEnd: true);
					case BinaryOperatorExpression { Operator: BinaryOperatorType.Range } range
						when RangeBound(range.Left, fromEnd: false) is int start && RangeBound(range.Right, fromEnd: true) is int end:
						if (lookupOnly)
							return list.Slice is not null && list.SliceStart == start && list.SliceEnd == end ? list.Slice : null;
						return list.SliceOf(start, end);
					default:
						return null;
				}
			}

			PNode? Element(PNode list, int index, bool fromEnd)
			{
				if (!lookupOnly)
					return list.Element(index, fromEnd);
				var elements = fromEnd ? list.ElementsFromEnd : list.Elements;
				return elements is not null && elements.TryGetValue(index, out var element) ? element : null;
			}

			/// <summary>"n" (a local bound to the length) or "x.Length" itself.</summary>
			bool IsLengthRead(Expression expression, PNode length)
			{
				return expression switch {
					IdentifierExpression id => bound.TryGetValue(id.Identifier, out var node) && node == length,
					MemberReferenceExpression { MemberName: "Length" or "Count" } member => GetNode(member) == length,
					_ => false
				};
			}

			/// <summary>A range bound as a distance from the start (or from the end); null (no bound) = 0.</summary>
			static int? RangeBound(Expression? bound, bool fromEnd)
			{
				switch (bound)
				{
					case null:
						return 0;
					case PrimitiveExpression { Value: int value } when !fromEnd && value >= 0:
						return value;
					case UnaryOperatorExpression { Operator: UnaryOperatorType.IndexFromEnd, Expression: PrimitiveExpression { Value: int value } } when fromEnd && value >= 0:
						return value;
					// "new Index(2)", "new Index(2, false)", "new Index(1, true)"
					case ObjectCreateExpression { Arguments.Count: 1 or 2 } create when create.GetResolveResult().Type.IsKnownType(KnownTypeCode.Index)
						&& create.Arguments.First() is PrimitiveExpression { Value: int value } && value >= 0:
						bool isFromEnd = create.Arguments.Count == 2 && create.Arguments.Last() is PrimitiveExpression { Value: true };
						if (create.Arguments.Count == 2 && create.Arguments.Last() is not PrimitiveExpression { Value: bool })
							return null;
						return isFromEnd == fromEnd ? value : null;
					default:
						return null;
				}
			}

			/// <summary>
			/// An implied relational condition replaces a weaker bound on the same side of the same value
			/// ("&gt;= -10" and an implied "not &lt; 10" → "&gt;= 10"); it is never added on its own.
			/// </summary>
			void TightenBound(Expression condition, bool negated)
			{
				// NetSpy: only looks at values the arm already has; an implied test must not add elements to a list pattern
				(PNode Node, Expression Pattern)? converted;
				lookupOnly = true;
				try
				{
					converted = Convert(condition, negated);
				}
				finally
				{
					lookupOnly = false;
				}
				if (converted is not { } c || Relational(c.Pattern) is not var (op, constant, value))
					return;
				bool lower = op is UnaryOperatorType.PatternRelationalGreaterThan or UnaryOperatorType.PatternRelationalGreaterThanOrEqual;
				for (int i = 0; i < c.Node.Atoms.Count; i++)
				{
					if (Relational(c.Node.Atoms[i]) is not var (existingOp, _, existingValue))
						continue;
					bool existingLower = existingOp is UnaryOperatorType.PatternRelationalGreaterThan or UnaryOperatorType.PatternRelationalGreaterThanOrEqual;
					if (existingLower != lower)
						continue;
					bool stricter = lower
						? value > existingValue || (value == existingValue && op == UnaryOperatorType.PatternRelationalGreaterThan)
						: value < existingValue || (value == existingValue && op == UnaryOperatorType.PatternRelationalLessThan);
					if (stricter)
						c.Node.Atoms[i] = new UnaryOperatorExpression(op, constant.Clone());
					return;
				}
			}

			/// <summary>"&lt; K", "&gt;= K"... and "not (&lt; K)" (= "&gt;= K") as (operator, K, value of K).</summary>
			static (UnaryOperatorType Op, Expression Constant, double Value)? Relational(Expression pattern)
			{
				bool not = false;
				if (pattern is UnaryOperatorExpression { Operator: UnaryOperatorType.PatternNot, Expression: var inner })
				{
					not = true;
					pattern = inner;
				}
				if (pattern is not UnaryOperatorExpression { Expression: { } constant } relational
					|| relational.Operator is not (UnaryOperatorType.PatternRelationalLessThan or UnaryOperatorType.PatternRelationalLessThanOrEqual
						or UnaryOperatorType.PatternRelationalGreaterThan or UnaryOperatorType.PatternRelationalGreaterThanOrEqual))
					return null;
				var constantValue = constant.GetResolveResult()?.ConstantValue ?? (constant as PrimitiveExpression)?.Value;
				if (constantValue is null or bool or string)
					return null;
				double value;
				try
				{
					value = System.Convert.ToDouble(constantValue, System.Globalization.CultureInfo.InvariantCulture);
				}
				catch (System.Exception ex) when (ex is System.InvalidCastException or System.FormatException or System.OverflowException)
				{
					return null;
				}
				var op = relational.Operator;
				if (not)
				{
					op = op switch {
						UnaryOperatorType.PatternRelationalLessThan => UnaryOperatorType.PatternRelationalGreaterThanOrEqual,
						UnaryOperatorType.PatternRelationalLessThanOrEqual => UnaryOperatorType.PatternRelationalGreaterThan,
						UnaryOperatorType.PatternRelationalGreaterThan => UnaryOperatorType.PatternRelationalLessThanOrEqual,
						_ => UnaryOperatorType.PatternRelationalLessThan
					};
				}
				return (op, constant, value);
			}

			void AddCondition(Expression condition, bool negated)
			{
				if (!negated && condition is BinaryOperatorExpression { Operator: BinaryOperatorType.ConditionalAnd } and)
				{
					AddCondition(and.Left!, false);
					AddCondition(and.Right!, false);
					return;
				}
				if (owner.PreferWhen && !IsTypeOrNullTest(condition))
				{
					var guard = condition.Clone();
					When.Add(negated ? Negate(guard) : guard);
					return;
				}
				var converted = Convert(condition, negated);
				if (converted is { } c)
				{
					Absorb(c.Node, c.Pattern, underNot: false);
				}
				else
				{
					var clone = condition.Clone();
					When.Add(negated ? Negate(clone) : clone);
				}
			}

			/// <summary>"x is T", "x is T v", "x == null", "x != null", "x is null", "x is not null" (and their negations).</summary>
			static bool IsTypeOrNullTest(Expression condition)
			{
				switch (condition)
				{
					case UnaryOperatorExpression { Operator: UnaryOperatorType.Not, Expression: { } inner }:
						return IsTypeOrNullTest(inner);
					case ParenthesizedExpression { Expression: { } inner }:
						return IsTypeOrNullTest(inner);
					case IsExpression:
						return true;
					case BinaryOperatorExpression { Operator: BinaryOperatorType.Equality or BinaryOperatorType.InEquality } eq:
						return eq.Right is NullReferenceExpression || eq.Left is NullReferenceExpression;
					case BinaryOperatorExpression { Operator: BinaryOperatorType.IsPattern, Right: { } pattern }:
						return pattern is DeclarationExpression or TypeReferenceExpression or NullReferenceExpression
							|| pattern is UnaryOperatorExpression { Operator: UnaryOperatorType.PatternNot, Expression: NullReferenceExpression }
							|| pattern is RecursivePatternExpression { SubPatterns.Count: 0, IsPositional: false };
					default:
						return false;
				}
			}

			static Expression Negate(Expression condition)
			{
				return condition switch {
					UnaryOperatorExpression { Operator: UnaryOperatorType.Not } not => not.Expression.Detach(),
					BinaryOperatorExpression { Operator: BinaryOperatorType.InEquality } ne => new BinaryOperatorExpression(ne.Left!.Detach(), BinaryOperatorType.Equality, ne.Right!.Detach()),
					BinaryOperatorExpression { Operator: BinaryOperatorType.Equality } eq => new BinaryOperatorExpression(eq.Left!.Detach(), BinaryOperatorType.InEquality, eq.Right!.Detach()),
					_ => new UnaryOperatorExpression(UnaryOperatorType.Not, condition)
				};
			}

			/// <summary>A condition as a pattern on one node (negated = the condition must be false).</summary>
			(PNode Node, Expression Pattern)? Convert(Expression condition, bool negated)
			{
				switch (condition)
				{
					case UnaryOperatorExpression { Operator: UnaryOperatorType.Not } not:
						return Convert(not.Expression, !negated);
					case BinaryOperatorExpression { Operator: BinaryOperatorType.ConditionalAnd or BinaryOperatorType.ConditionalOr } logical:
					{
						var left = Convert(logical.Left!, negated);
						var right = Convert(logical.Right!, negated);
						if (left is null || right is null)
							return null;
						var ancestor = CommonAncestor(left.Value.Node, right.Value.Node);
						if (ancestor is null)
							return null;
						var l = Lift(left.Value.Node, left.Value.Pattern, ancestor);
						var r = Lift(right.Value.Node, right.Value.Pattern, ancestor);
						if (l is null || r is null)
							return null;
						bool isAnd = (logical.Operator == BinaryOperatorType.ConditionalAnd) != negated;
						return (ancestor, new BinaryOperatorExpression(l, isAnd ? BinaryOperatorType.PatternAnd : BinaryOperatorType.PatternOr, r));
					}
					case BinaryOperatorExpression { Operator: BinaryOperatorType.IsPattern } isPattern:
					{
						var node = GetNode(isPattern.Left!);
						if (node is null)
							return null;
						return (node, Not(isPattern.Right!.Clone(), negated));
					}
					case IsExpression isExpression:
					{
						var node = GetNode(isExpression.Expression);
						if (node is null)
							return null;
						return (node, Not(new TypeReferenceExpression(isExpression.Type.Clone()), negated));
					}
					case BinaryOperatorExpression binary:
						return ConvertComparison(binary, negated);
					case InvocationExpression { Target: MemberReferenceExpression { MemberName: "SequenceEqual" } sequenceEqual, Arguments.Count: 1 } invocation
						when invocation.Arguments.Single() is PrimitiveExpression { Value: string }
							&& sequenceEqual.Target.GetResolveResult().Type is { Name: "ReadOnlySpan" or "Span", TypeArguments.Count: 1 } spanType
							&& spanType.TypeArguments[0].IsKnownType(KnownTypeCode.Char):
					{
						// span is "text" (C# 11)
						var node = GetNode(sequenceEqual.Target);
						if (node is null)
							return null;
						return (node, Not(invocation.Arguments.Single().Clone(), negated));
					}
					case MemberReferenceExpression member when member.GetResolveResult().Type.IsKnownType(KnownTypeCode.Boolean):
					{
						// x.IsEmpty → { IsEmpty: true }
						var node = GetNode(member);
						if (node is null)
							return null;
						return (node, new PrimitiveExpression(!negated));
					}
					default:
						return null;
				}
			}

			(PNode Node, Expression Pattern)? ConvertComparison(BinaryOperatorExpression binary, bool negated)
			{
				Expression value, constant;
				var op = binary.Operator;
				if (IsConstant(binary.Right!) && !IsConstant(binary.Left!))
				{
					value = binary.Left!;
					constant = binary.Right!;
				}
				else if (IsConstant(binary.Left!) && !IsConstant(binary.Right!))
				{
					value = binary.Right!;
					constant = binary.Left!;
					op = op switch {
						BinaryOperatorType.LessThan => BinaryOperatorType.GreaterThan,
						BinaryOperatorType.LessThanOrEqual => BinaryOperatorType.GreaterThanOrEqual,
						BinaryOperatorType.GreaterThan => BinaryOperatorType.LessThan,
						BinaryOperatorType.GreaterThanOrEqual => BinaryOperatorType.LessThanOrEqual,
						_ => op
					};
				}
				else
				{
					return null;
				}
				// a user-defined operator isn't what a constant pattern calls (string/decimal equality is)
				if (binary.GetResolveResult() is OperatorResolveResult { UserDefinedOperatorMethod: { } userOperator }
					&& !userOperator.DeclaringType.IsKnownType(KnownTypeCode.String) && !userOperator.DeclaringType.IsKnownType(KnownTypeCode.Decimal))
					return null;
				var valueType = value.GetResolveResult().Type;
				Expression pattern;
				switch (op)
				{
					case BinaryOperatorType.Equality:
						pattern = constant.Clone();
						break;
					case BinaryOperatorType.InEquality:
						pattern = new UnaryOperatorExpression(UnaryOperatorType.PatternNot, constant.Clone());
						break;
					case BinaryOperatorType.LessThan:
					case BinaryOperatorType.LessThanOrEqual:
					case BinaryOperatorType.GreaterThan:
					case BinaryOperatorType.GreaterThanOrEqual:
						if (!(valueType.GetDefinition()?.KnownTypeCode is >= KnownTypeCode.Char and <= KnownTypeCode.Decimal) && valueType.Kind != TypeKind.Enum)
							return null;
						var relational = op switch {
							BinaryOperatorType.LessThan => UnaryOperatorType.PatternRelationalLessThan,
							BinaryOperatorType.LessThanOrEqual => UnaryOperatorType.PatternRelationalLessThanOrEqual,
							BinaryOperatorType.GreaterThan => UnaryOperatorType.PatternRelationalGreaterThan,
							_ => UnaryOperatorType.PatternRelationalGreaterThanOrEqual
						};
						// NetSpy: "not (<= 100)" is "> 100" (but not for float/double: NaN is neither)
						if (negated && !valueType.IsKnownType(KnownTypeCode.Single) && !valueType.IsKnownType(KnownTypeCode.Double))
						{
							negated = false;
							relational = relational switch {
								UnaryOperatorType.PatternRelationalLessThan => UnaryOperatorType.PatternRelationalGreaterThanOrEqual,
								UnaryOperatorType.PatternRelationalLessThanOrEqual => UnaryOperatorType.PatternRelationalGreaterThan,
								UnaryOperatorType.PatternRelationalGreaterThan => UnaryOperatorType.PatternRelationalLessThanOrEqual,
								_ => UnaryOperatorType.PatternRelationalLessThan
							};
						}
						pattern = new UnaryOperatorExpression(relational, IntegralLiteral(constant, valueType));
						break;
					default:
						return null;
				}
				var node = GetNode(value);
				if (node is null)
					return null;
				return (node, Not(pattern, negated));
			}

			/// <summary>NetSpy: "-10.0" on a double/float value is written "-10", like the source (int converts implicitly).</summary>
			static Expression IntegralLiteral(Expression constant, IType valueType)
			{
				if ((valueType.IsKnownType(KnownTypeCode.Double) || valueType.IsKnownType(KnownTypeCode.Single))
					&& (constant.GetResolveResult()?.ConstantValue ?? (constant as PrimitiveExpression)?.Value) is { } value
					&& value is double or float)
				{
					double d = System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
					if (d == System.Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue && !(d == 0 && System.BitConverter.DoubleToInt64Bits(d) < 0))
					{
						var literal = new PrimitiveExpression((int)d);
						literal.AddAnnotation(new ConstantResolveResult(valueType, value));
						return literal;
					}
				}
				return constant.Clone();
			}

			static Expression Not(Expression pattern, bool negated)
			{
				if (!negated)
					return pattern;
				if (pattern is UnaryOperatorExpression { Operator: UnaryOperatorType.PatternNot } not)
					return not.Expression.Detach();
				return new UnaryOperatorExpression(UnaryOperatorType.PatternNot, StripDesignations(pattern));
			}

			/// <summary>"not int i" can't declare i: the designation only matters on the other path.</summary>
			static Expression StripDesignations(Expression pattern)
			{
				switch (pattern)
				{
					case DeclarationExpression declaration:
						return declaration.Type.IsVar() ? new IdentifierExpression("_") : new TypeReferenceExpression(declaration.Type.Detach());
					case RecursivePatternExpression recursive:
						recursive.Designation = null;
						foreach (var sub in recursive.SubPatterns.ToList())
						{
							if (sub is NamedArgumentExpression named)
								named.Expression = StripDesignations(named.Expression.Detach());
							else
								sub.ReplaceWith(StripDesignations(sub.Detach()));
						}
						return recursive;
					case BinaryOperatorExpression { Operator: BinaryOperatorType.PatternAnd or BinaryOperatorType.PatternOr } binary:
						binary.Left = StripDesignations(binary.Left!.Detach());
						binary.Right = StripDesignations(binary.Right!.Detach());
						return binary;
					default:
						return pattern;
				}
			}

			static PNode? CommonAncestor(PNode a, PNode b)
			{
				var ancestors = new HashSet<PNode>();
				for (var n = a; n != null; n = n.Parent)
					ancestors.Add(n);
				for (var n = b; n != null; n = n.Parent)
				{
					if (ancestors.Contains(n))
						return n;
				}
				return null;
			}

			/// <summary>A pattern on <paramref name="node"/> as a pattern on its ancestor: "0" on X.Width → "{ Width: 0 }" on X.</summary>
			static Expression? Lift(PNode node, Expression pattern, PNode ancestor)
			{
				for (var n = node; n != ancestor; n = n.Parent!)
				{
					if (n.MemberName is null)
						return null; // positional element
					pattern = new RecursivePatternExpression { SubPatterns = { new NamedArgumentExpression { Name = n.MemberName, Expression = pattern } } };
				}
				return pattern;
			}

			/// <summary>Merges a pattern into the node (type tests, designations and sub-patterns become structure).</summary>
			void Absorb(PNode node, Expression pattern, bool underNot)
			{
				switch (pattern)
				{
					case TypeReferenceExpression typeReference:
						Narrow(node, typeReference.Type, typeReference.Type.GetResolveResult().Type);
						break;
					case DeclarationExpression { Designation: SingleVariableDesignation designation } declaration:
						if (!declaration.Type.IsVar())
							Narrow(node, declaration.Type, declaration.Type.GetResolveResult().Type);
						node.Names.Add(designation.Identifier);
						bound[designation.Identifier] = node;
						break;
					case RecursivePatternExpression recursive:
						if (recursive.Type is not null)
							Narrow(node, recursive.Type, recursive.Type.GetResolveResult().Type);
						else
							node.NotNull = true;
						if (recursive.Designation is SingleVariableDesignation recursiveDesignation)
						{
							node.Names.Add(recursiveDesignation.Identifier);
							bound[recursiveDesignation.Identifier] = node;
						}
						if (recursive.IsPositional)
						{
							// only merged if nothing else was deconstructed on this path
							if (node.Positional is not null)
							{
								node.Atoms.Add(pattern);
								break;
							}
							node.Positional = new List<PNode>();
							foreach (var sub in recursive.SubPatterns)
							{
								var element = new PNode { Parent = node };
								node.Positional.Add(element);
								Absorb(element, sub.Clone(), underNot);
							}
							break;
						}
						foreach (var sub in recursive.SubPatterns)
						{
							if (sub is not NamedArgumentExpression named)
							{
								node.Atoms.Add(pattern);
								break;
							}
							var target = node;
							foreach (var part in named.Name.Split('.'))
								target = target.Member(part);
							Absorb(target, named.Expression.Clone(), underNot);
						}
						break;
					case IdentifierExpression { Identifier: "_" }:
						break;
					case UnaryOperatorExpression { Operator: UnaryOperatorType.PatternNot, Expression: NullReferenceExpression }:
						node.NotNull = true;
						break;
					default:
						node.Atoms.Add(pattern);
						break;
				}
			}

			static void Narrow(PNode node, AstType syntax, IType type)
			{
				if (node.Type is null || type.Kind != TypeKind.Unknown && IsSubtype(type, node.Type))
				{
					node.Type = type;
					node.TypeSyntax = syntax.Clone();
				}
				else if (type.Kind == TypeKind.Unknown || !IsSubtype(node.Type, type))
				{
					node.Atoms.Add(new TypeReferenceExpression(syntax.Clone()));
				}
			}

			static bool IsSubtype(IType type, IType baseType) => type.GetAllBaseTypes().Any(t => t.Equals(baseType));
		}

		/// <summary>The pattern for a node; null if nothing is required of the value.</summary>
		static Expression? Emit(PNode node)
		{
			var parts = new List<Expression>();
			// NetSpy: list pattern (it takes over the length member)
			PNode? listLength = null;
			Expression? listPattern = null;
			if (node.HasListParts || (node.ListMode && node.LengthMember is { Atoms.Count: > 0 }))
			{
				listLength = node.LengthMember;
				listPattern = EmitList(node, listLength);
				if (node.Designation is not null)
					throw new PatternFailure("designated list pattern");
			}
			// NetSpy: the "" pattern on a string or a char span compiles to a length test: { Length: 0 } → ""
			var atoms = RemoveRedundantAtoms(node.Atoms.Select(a => a.Clone()).ToList());
			// (after a type test the source has "string { Length: 0 }": the test is the narrowing, not the constant)
			if (listLength is null && node.LengthMember is { } emptyLength && node.Type is null && IsCharSequence(node.StaticType)
				&& emptyLength.Atoms.Count == 1 && ConstantInt(emptyLength.Atoms[0]) == 0 && emptyLength.Designation is null
				&& emptyLength.Type is null && emptyLength.Members.Count == 0 && emptyLength.Positional is null && !emptyLength.HasListParts
				&& !atoms.Any(a => a is PrimitiveExpression { Value: string }))
			{
				listLength = emptyLength;
				atoms.Add(new PrimitiveExpression(""));
			}
			// sub-patterns
			var positional = node.Positional?.Select(Emit).ToList();
			bool keepPositional = positional is not null && (positional.Any(p => p is not null) || node.Positional!.Any(e => e.Designation is not null));
			var members = new List<Expression>();
			foreach (var member in node.Members)
			{
				if (member == listLength)
					continue;
				var sub = Emit(member);
				if (sub is null)
					continue;
				// { A: { B: x } } → { A.B: x } (C# 10 extended property patterns)
				if (sub is RecursivePatternExpression { Type: null, Designation: null, IsPositional: false, SubPatterns.Count: 1 } nested
					&& nested.SubPatterns.Single() is NamedArgumentExpression inner)
				{
					members.Add(new NamedArgumentExpression { Name = member.MemberName + "." + inner.Name, Expression = inner.Expression.Detach() });
				}
				else
				{
					members.Add(new NamedArgumentExpression { Name = member.MemberName!, Expression = sub });
				}
			}
			if (keepPositional || members.Count > 0)
			{
				var recursive = new RecursivePatternExpression { Type = node.TypeSyntax?.Clone(), IsPositional = keepPositional };
				if (keepPositional)
					recursive.SubPatterns.AddRange(positional!.Select((p, i) => p ?? DesignationOrDiscard(node.Positional![i])));
				else
					recursive.SubPatterns.AddRange(members);
				if (node.Designation is not null)
					recursive.Designation = NewDesignation(node);
				parts.Add(recursive);
			}
			else if (node.TypeSyntax is not null)
			{
				// Rectangle and ({ Width: 0 } or { Height: 0 }) → Rectangle { Width: 0 } or Rectangle { Height: 0 }
				if (node.Designation is null && atoms.Count == 1 && atoms[0] is BinaryOperatorExpression { Operator: BinaryOperatorType.PatternOr } or
					&& DistributeType(or, node.TypeSyntax))
				{
					return atoms[0];
				}
				parts.Add(node.Designation is not null
					? new DeclarationExpression { Type = node.TypeSyntax.Clone(), Designation = NewDesignation(node) }
					: new TypeReferenceExpression(node.TypeSyntax.Clone()));
			}
			else if (node.Designation is not null)
			{
				parts.Add(node.NotNull
					? new RecursivePatternExpression { Designation = NewDesignation(node) }
					: new DeclarationExpression { Type = new SimpleType("var"), Designation = NewDesignation(node) });
			}
			if (listPattern is not null)
				parts.Add(listPattern);
			bool impliesNotNull = parts.Count > 0 || atoms.Any(a => a is not UnaryOperatorExpression { Operator: UnaryOperatorType.PatternNot });
			if (node.NotNull && !impliesNotNull)
				parts.Add(new UnaryOperatorExpression(UnaryOperatorType.PatternNot, new NullReferenceExpression()));
			parts.AddRange(atoms);
			if (parts.Count == 0)
				return null;
			return parts.Aggregate((a, b) => new BinaryOperatorExpression(a, BinaryOperatorType.PatternAnd, b));
		}

		/// <summary>A pattern that can't be written exactly: the switch is left as it is.</summary>
		sealed class PatternFailure : Exception
		{
			public PatternFailure(string message) : base(message) { }
		}

		/// <summary>
		/// NetSpy: C# 11 list pattern from the elements and the length test: "Length == 2" + x[0] → "[p0, _]";
		/// no exact length → "[p0, .., q1]" (the slice is ".. var rest" when "x[1..^1]" is used).
		/// </summary>
		static Expression EmitList(PNode node, PNode? length)
		{
			if (length is not null && (length.Type is not null || length.NotNull || length.Members.Count > 0 || length.HasListParts
				|| length.Designation is not null || length.Positional is not null))
				throw new PatternFailure("list length used otherwise");
			int? exact = null, atLeast = null;
			foreach (var atom in length?.Atoms ?? new List<Expression>())
			{
				if (ConstantInt(atom) is int k && exact is null)
					exact = k;
				else if (atom is UnaryOperatorExpression { Operator: UnaryOperatorType.PatternRelationalGreaterThanOrEqual, Expression: { } min } && ConstantInt(min) is int m)
					atLeast = Math.Max(atLeast ?? 0, m);
				else if (atom is UnaryOperatorExpression { Operator: UnaryOperatorType.PatternRelationalGreaterThan, Expression: { } above } && ConstantInt(above) is int a)
					atLeast = Math.Max(atLeast ?? 0, a + 1);
				else
					throw new PatternFailure("list length test " + atom);
			}
			var collection = new CollectionExpression();
			if (exact is int count)
			{
				if (node.Slice is not null || count < 0 || (atLeast is int least && least > count))
					throw new PatternFailure("exact length with slice");
				var slots = new PNode?[count];
				foreach (var pair in node.Elements ?? new SortedDictionary<int, PNode>())
				{
					if (pair.Key >= count)
						throw new PatternFailure("element past the length");
					slots[pair.Key] = pair.Value;
				}
				foreach (var pair in node.ElementsFromEnd ?? new SortedDictionary<int, PNode>())
				{
					int index = count - pair.Key;
					if (index < 0 || slots[index] is not null)
						throw new PatternFailure("element from the end");
					slots[index] = pair.Value;
				}
				foreach (var slot in slots)
					collection.Elements.Add(slot is null ? new IdentifierExpression("_") : Emit(slot) ?? DesignationOrDiscard(slot));
				return collection;
			}
			int prefix = node.Elements is { Count: > 0 } elements ? elements.Keys.Max() + 1 : 0;
			int suffix = node.ElementsFromEnd is { Count: > 0 } fromEndElements ? fromEndElements.Keys.Max() : 0;
			if (node.Slice is not null)
			{
				if (node.SliceStart < prefix || node.SliceEnd < suffix)
					throw new PatternFailure("slice overlaps elements");
				prefix = node.SliceStart;
				suffix = node.SliceEnd;
			}
			if (atLeast is int minimum && minimum != prefix + suffix)
				throw new PatternFailure("list length bound");
			for (int i = 0; i < prefix; i++)
				collection.Elements.Add(ListElement(node.Elements, i));
			Expression? slice = null;
			if (node.Slice is { } sliceNode)
				slice = Emit(sliceNode) ?? (sliceNode.Designation is null ? null : DesignationOrDiscard(sliceNode));
			collection.Elements.Add(new SpreadElement { Expression = slice });
			for (int i = suffix; i > 0; i--)
				collection.Elements.Add(ListElement(node.ElementsFromEnd, i));
			return collection;
		}

		static Expression ListElement(SortedDictionary<int, PNode>? elements, int key)
		{
			if (elements is null || !elements.TryGetValue(key, out var element))
				return new IdentifierExpression("_");
			return Emit(element) ?? DesignationOrDiscard(element);
		}

		/// <summary>string, ReadOnlySpan&lt;char&gt; or Span&lt;char&gt;: the types a string constant pattern applies to.</summary>
		static bool IsCharSequence(IType? type)
		{
			return type is not null && (type.IsKnownType(KnownTypeCode.String)
				|| type is { Name: "ReadOnlySpan" or "Span", Namespace: "System", TypeArguments.Count: 1 } && type.TypeArguments[0].IsKnownType(KnownTypeCode.Char));
		}

		static int? ConstantInt(Expression e)
		{
			var value = (e as PrimitiveExpression)?.Value ?? e.GetResolveResult()?.ConstantValue;
			return value is int i ? i : null;
		}

		static SingleVariableDesignation NewDesignation(PNode node)
		{
			var designation = new SingleVariableDesignation { Identifier = node.Designation! };
			if (node.DesignationVariable is { } variable)
				designation.AddAnnotation(new ILVariableResolveResult(variable, variable.Type));
			return designation;
		}

		static Expression DesignationOrDiscard(PNode element)
		{
			return element.Designation is null
				? new IdentifierExpression("_")
				: new DeclarationExpression { Type = new SimpleType("var"), Designation = NewDesignation(element) };
		}

		/// <summary>"not x" next to a different constant is implied: ("paper", ...) needs no "not "rock"".</summary>
		static List<Expression> RemoveRedundantAtoms(List<Expression> atoms)
		{
			var constants = atoms.Where(a => a is PrimitiveExpression || (a is not UnaryOperatorExpression && a is not BinaryOperatorExpression && IsConstant(a)))
				.Select(a => a.ToString()).ToList();
			if (constants.Count == 0)
				return atoms;
			return atoms.Where(a => !(a is UnaryOperatorExpression { Operator: UnaryOperatorType.PatternNot, Expression: var negated }
				&& (negated is PrimitiveExpression || IsConstant(negated)) && !constants.Contains(negated.ToString()))).ToList();
		}

		/// <summary>Adds the type to every "{ ... }" operand of an 'or' pattern; false if an operand isn't one.</summary>
		static bool DistributeType(BinaryOperatorExpression or, AstType type)
		{
			var operands = new List<Expression>();
			void Collect(Expression e)
			{
				if (e is BinaryOperatorExpression { Operator: BinaryOperatorType.PatternOr } nested)
				{
					Collect(nested.Left!);
					Collect(nested.Right!);
				}
				else
				{
					operands.Add(e);
				}
			}
			Collect(or);
			if (!operands.All(o => o is RecursivePatternExpression { Type: null }))
				return false;
			foreach (RecursivePatternExpression operand in operands)
				operand.Type = type.Clone();
			return true;
		}

		#endregion
	}
}
