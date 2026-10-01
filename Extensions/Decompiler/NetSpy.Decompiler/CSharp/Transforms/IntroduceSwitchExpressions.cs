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
			if (isAsync || method is null || body.Descendants.Any(n => n is YieldReturnStatement or YieldBreakStatement))
				return;
			var parser = new ArmParser();
			var arms = parser.ParseList(body.Statements.ToList(), 0, out bool exhaustive);
			if (arms is null || !exhaustive || arms.Count < 2)
			{
				Trace(method, arms is null ? "not arms" : $"exhaustive={exhaustive} arms={arms.Count}");
				return;
			}
			if (!parser.SawNoMatchThrow && !ReturnsWereMerged(body, method))
			{
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
					default:
						return null;
				}
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
					if (negative && thenExhaustive && restExhaustive)
					{
						// "if (!A) { THEN } REST" → "A => REST" first: REST catches every A input, so THEN arms need no guard
						result.AddRange(Prefix(restArms, new ConditionItem(condition, negated: true)));
						result.AddRange(thenArms);
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
				var (first, firstExhaustive, second) = negative ? (elseArms, elseExhaustive, thenArms) : (thenArms, thenExhaustive, elseArms);
				result.AddRange(Prefix(first, new ConditionItem(condition, negated: negative)));
				// if the first branch always returns, the second one needs no guard
				result.AddRange(firstExhaustive ? second : Prefix(second, new ConditionItem(condition, negated: !negative)));
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
				var switchExpression = new SwitchExpression { Expression = subject };
				foreach (var (roots, when, arm) in built)
				{
					var patterns = subjects.Select(s => roots.TryGetValue(s, out var root) ? Emit(root) : null).ToList();
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
							node = new PNode();
							Roots.Add(id.Identifier, node);
						}
						owner.SeeRoot(id.Identifier);
						return node;
					case MemberReferenceExpression { TypeArguments.Count: 0 } member when member.GetSymbol() is IProperty or IField:
						return GetNode(member.Target)?.Member(member.MemberName);
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
			/// An implied relational condition replaces a weaker bound on the same side of the same value
			/// ("&gt;= -10" and an implied "not &lt; 10" → "&gt;= 10"); it is never added on its own.
			/// </summary>
			void TightenBound(Expression condition, bool negated)
			{
				if (Convert(condition, negated) is not { } c || Relational(c.Pattern) is not var (op, constant, value))
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
					if (d == System.Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue && !(d == 0 && double.IsNegative(d)))
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
			// sub-patterns
			var positional = node.Positional?.Select(Emit).ToList();
			bool keepPositional = positional is not null && (positional.Any(p => p is not null) || node.Positional!.Any(e => e.Designation is not null));
			var members = new List<Expression>();
			foreach (var member in node.Members)
			{
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
			var atoms = RemoveRedundantAtoms(node.Atoms.Select(a => a.Clone()).ToList());
			if (keepPositional || members.Count > 0)
			{
				var recursive = new RecursivePatternExpression { Type = node.TypeSyntax?.Clone(), IsPositional = keepPositional };
				if (keepPositional)
					recursive.SubPatterns.AddRange(positional!.Select((p, i) => p ?? DesignationOrDiscard(node.Positional![i])));
				else
					recursive.SubPatterns.AddRange(members);
				if (node.Designation is not null)
					recursive.Designation = new SingleVariableDesignation { Identifier = node.Designation };
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
					? new DeclarationExpression { Type = node.TypeSyntax.Clone(), Designation = new SingleVariableDesignation { Identifier = node.Designation } }
					: new TypeReferenceExpression(node.TypeSyntax.Clone()));
			}
			else if (node.Designation is not null)
			{
				parts.Add(node.NotNull
					? new RecursivePatternExpression { Designation = new SingleVariableDesignation { Identifier = node.Designation } }
					: new DeclarationExpression { Type = new SimpleType("var"), Designation = new SingleVariableDesignation { Identifier = node.Designation } });
			}
			bool impliesNotNull = parts.Count > 0 || atoms.Any(a => a is not UnaryOperatorExpression { Operator: UnaryOperatorType.PatternNot });
			if (node.NotNull && !impliesNotNull)
				parts.Add(new UnaryOperatorExpression(UnaryOperatorType.PatternNot, new NullReferenceExpression()));
			parts.AddRange(atoms);
			if (parts.Count == 0)
				return null;
			return parts.Aggregate((a, b) => new BinaryOperatorExpression(a, BinaryOperatorType.PatternAnd, b));
		}

		static Expression DesignationOrDiscard(PNode element)
		{
			return element.Designation is null
				? new IdentifierExpression("_")
				: new DeclarationExpression { Type = new SimpleType("var"), Designation = new SingleVariableDesignation { Identifier = element.Designation } };
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
