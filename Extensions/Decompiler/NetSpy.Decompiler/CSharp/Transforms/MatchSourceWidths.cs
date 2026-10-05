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
using System.Globalization;
using System.Linq;

using NetSpy.Decompiler.CSharp.OutputVisitor;
using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.Semantics;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: many spellings compile to the same IL ("x += 1" / "x = x + 1", "var" / an explicit type, "1" / "1.0"
	/// for a double, omitted optional arguments, "in x" / "ref x" / "x", inferred / explicit type arguments,
	/// "new T()" / "new()"). The PDB keeps one trace of the spelling: a statement's sequence point is exactly as
	/// wide as its source text. For every one-line statement, the equivalent spellings are tried and a
	/// combination is kept only if it gives the statement exactly the source's width, and is the only
	/// combination of that size that does.
	/// </summary>
	public sealed class MatchSourceWidths : IAstTransform
	{
		/// <summary>One place in a statement that can be spelled differently: each alternative applies a change and returns its undo.</summary>
		sealed class Site
		{
			public readonly List<Func<Action>> Alternatives = new List<Func<Action>>();
			public readonly List<int> Deltas = new List<int>();
		}

		TransformContext context = null!;
		CSharpFormattingOptions formatting = null!;

		const int MaxSites = 12;

		static readonly char[] Escaped = { '"', '\\', '\n', '\r', '\t' };
		static readonly char[] EscapedInInterpolation = { '"', '\\', '\n', '\r', '\t', '{', '}' };

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.SourceLayoutFromPdb || context.DecompileRun.DebugInfoProvider is null)
				return;
			this.context = context;
			this.formatting = context.Settings.CSharpFormattingOptions;
			foreach (var node in rootNode.Descendants.OfType<Statement>().ToList())
			{
				var statement = node;
				if (statement is BlockStatement || statement.Annotation<SourceLayoutAnnotation>() is not { Width: > 0 } layout)
					continue;
				int target = layout.Width;
				// "using (T r = e)": the sequence point is "T r = e" (no ';'); "using T r = e;" has it all
				if (statement is UsingStatement { ResourceAcquisition: VariableDeclarationStatement resource } usingStatement)
				{
					target += usingStatement.IsEnhanced ? -((usingStatement.IsAsync ? "await using " : "using ").Length) : 1;
					statement = resource;
				}
				else if (statement is not (ExpressionStatement or VariableDeclarationStatement or ReturnStatement or ThrowStatement or IfElseStatement { Condition: not null }))
				{
					continue;
				}
				// a string the source may have written as a raw or verbatim string has an unknown source width
				if (statement.Descendants.Any(n => n is PrimitiveExpression { Value: string text } && text.IndexOfAny(Escaped) >= 0
					|| n is InterpolatedStringText { Text: var part } && part.IndexOfAny(EscapedInInterpolation) >= 0))
					continue;
				Process(statement, target);
			}
			if (context.Settings.ImplicitlyTypedLocals)
			{
				foreach (var foreachStatement in rootNode.Descendants.OfType<ForeachStatement>().ToList())
					ProcessForeachVariable(foreachStatement);
			}
			// "T P => expr;": the getter's single sequence point is the expression
			foreach (var member in rootNode.Descendants.OfType<EntityDeclaration>().ToList())
			{
				var expressionBody = member switch {
					PropertyDeclaration p => p.ExpressionBody,
					IndexerDeclaration i => i.ExpressionBody,
					_ => null
				};
				if (expressionBody is null || (member.GetSymbol() as IProperty)?.Getter is not { } getter || GetterWidth(getter) is not int width)
					continue;
				if (expressionBody.DescendantsAndSelf.Any(n => n is PrimitiveExpression { Value: string text } && text.IndexOfAny(Escaped) >= 0))
					continue;
				Process(expressionBody, width);
			}
		}

		int? GetterWidth(IMethod getter)
		{
			if (context.DecompileRun.DebugInfoProvider is not { } provider || getter.MetadataToken.IsNil
				|| getter.MetadataToken.Kind != System.Reflection.Metadata.HandleKind.MethodDefinition)
				return null;
			try
			{
				var points = provider.GetSequencePoints((System.Reflection.Metadata.MethodDefinitionHandle)getter.MetadataToken).Where(sp => !sp.IsHidden).ToList();
				return points.Count == 1 && points[0].StartLine == points[0].EndLine ? points[0].EndColumn - points[0].StartColumn : null;
			}
			catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
			{
				return null;
			}
		}

		/// <summary>"foreach (var x in e)" / "foreach (T x in e)": the variable has a sequence point of its own.</summary>
		void ProcessForeachVariable(ForeachStatement foreachStatement)
		{
			if (foreachStatement.Annotation<SourceLayoutAnnotation>() is not { ForeachVariableWidth: > 0 } layout
				|| foreachStatement.VariableDesignation is not SingleVariableDesignation designation
				|| designation.Annotation<ILVariableResolveResult>()?.Variable.Type is not { } type || type.Kind is TypeKind.Unknown or TypeKind.ByReference || type.ContainsAnonymousType())
				return;
			int current = foreachStatement.VariableType.ToString(formatting).Length + 1 + designation.Identifier.Length;
			if (current == layout.ForeachVariableWidth)
				return;
			AstType alternative = foreachStatement.VariableType.IsVar() ? context.TypeSystemAstBuilder.ConvertType(type) : new SimpleType("var");
			if (alternative.ToString(formatting).Length + 1 + designation.Identifier.Length != layout.ForeachVariableWidth)
				return;
			context.Step("Match foreach variable width", foreachStatement);
			alternative.CopyAnnotationsFrom(foreachStatement.VariableType);
			foreachStatement.VariableType = alternative;
		}

		void Process(AstNode statement, int target)
		{
			int width = Measure(MeasuredNode(statement));
			if (width < 0 || width == target)
				return;
			var sites = new List<Site>();
			CollectSites(statement, sites);
			if (sites.Count == 0 || sites.Count > 4 * MaxSites)
				return;
			// the effect of each alternative on its own
			foreach (var site in sites)
			{
				foreach (var alternative in site.Alternatives)
				{
					var undo = alternative();
					int w = Measure(MeasuredNode(statement));
					undo();
					site.Deltas.Add(w < 0 ? int.MinValue : w - width);
				}
			}
			// places whose spelling changes nothing don't count
			sites.RemoveAll(s => s.Deltas.All(d => d == 0 || d == int.MinValue));
			if (sites.Count == 0 || sites.Count > MaxSites)
				return;
			// the combinations with the fewest changes that close the gap
			var best = new List<int[]>();
			int bestCount = int.MaxValue;
			var choice = new int[sites.Count];
			void Search(int index, int remaining, int count)
			{
				if (count > bestCount)
					return;
				if (index == sites.Count)
				{
					if (remaining != 0)
						return;
					if (count < bestCount)
					{
						bestCount = count;
						best.Clear();
					}
					best.Add((int[])choice.Clone());
					return;
				}
				choice[index] = -1;
				Search(index + 1, remaining, count);
				var site = sites[index];
				for (int i = 0; i < site.Deltas.Count; i++)
				{
					if (site.Deltas[i] == int.MinValue || site.Deltas[i] == 0)
						continue;
					choice[index] = i;
					Search(index + 1, remaining - site.Deltas[i], count + 1);
				}
				choice[index] = -1;
			}
			Search(0, target - width, 0);
			if (Environment.GetEnvironmentVariable("NETSPY_TMP") == "1")
				Console.Error.WriteLine($"[width] {width}->{target} sites=[{string.Join(" ", sites.Select(s => string.Join("/", s.Deltas)))}] best={best.Count}x{bestCount}: {statement.ToString().Split('\n')[0].Trim()}");
			if (best.Count != 1 || bestCount == 0)
				return;
			// apply it, and keep it only if the statement now has exactly the source's width
			var undos = new List<Action>();
			for (int i = 0; i < sites.Count; i++)
			{
				if (best[0][i] >= 0)
					undos.Add(sites[i].Alternatives[best[0][i]]());
			}
			if (Measure(MeasuredNode(statement)) == target)
			{
				context.Step("Match source width", statement);
				return;
			}
			for (int i = undos.Count - 1; i >= 0; i--)
				undos[i]();
		}

		/// <summary>The node the sequence point covers: the expression of "=> expr" bodies, else the statement.</summary>
		static AstNode MeasuredNode(AstNode statement)
		{
			if (statement.Parent is BlockStatement { Statements.Count: 1 } body
				&& (body.Annotation<BodyStyleAnnotation>()?.IsExpressionBody == true
					|| AnnotateAccessorStyles.StyleOf(body) is AccessorStyle.ExpressionBody or AccessorStyle.PropertyExpressionBody))
			{
				switch (statement)
				{
					case ReturnStatement { Expression: { } returned }:
						return returned;
					case ExpressionStatement { Expression: { } expression }:
						return expression;
				}
			}
			return statement;
		}

		/// <summary>The width of the node written on one line with the output's formatting, -1 if it takes several.</summary>
		int Measure(AstNode node)
		{
			var clone = node.Clone();
			clone.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true });
			var text = clone is IfElseStatement { Condition: { } condition }
				? "if (" + condition.ToString(formatting).Trim() + ")"
				: clone.ToString(formatting).Trim();
			if (node is Expression && node.Parent is ThrowStatement)
				text = "throw " + text;
			return text.IndexOf('\n') >= 0 ? -1 : text.Length;
		}

		#region Sites

		void CollectSites(AstNode statement, List<Site> sites)
		{
			if (statement is VariableDeclarationStatement declaration)
				AddDeclarationSite(declaration, sites);
			// an if: only its condition is in the sequence point
			AstNode root = statement is IfElseStatement { Condition: { } condition } ? condition : statement;
			foreach (var node in root.DescendantsAndSelf)
			{
				if (node is PrimitiveExpression { Value: string name })
					AddNameofSite((PrimitiveExpression)node, name, statement, sites);
				switch (node)
				{
					case AssignmentExpression assignment:
						AddCompoundAssignmentSite(assignment, sites);
						AddMinimalParenthesesSite(assignment, sites);
						AddDeconstructionSite(assignment, sites);
						break;
					case UnaryOperatorExpression { Operator: UnaryOperatorType.Not } not:
						AddEqualsFalseSite(not, sites);
						break;
					case UnaryOperatorExpression { Operator: UnaryOperatorType.Dereference } dereference:
						AddPointerIndexSite(dereference, sites);
						break;
					case PointerReferenceExpression arrow:
						AddArrowSite(arrow, sites);
						break;
					case CastExpression cast:
						AddImplicitCastSite(cast, sites);
						break;
					case ConditionalExpression or VariableInitializer:
						AddMinimalParenthesesSite(node, sites);
						break;
					case MemberReferenceExpression member:
						AddNullForgivingSite(member, sites);
						AddTupleRestSite(member, sites);
						break;
					case OutVarDeclarationExpression outVar:
						AddOutVarTypeSite(outVar, sites);
						break;
					case PrimitiveExpression literal:
						AddIntegralLiteralSite(literal, sites);
						break;
					case DirectionExpression direction:
						AddDirectionSite(direction, sites);
						if (direction.FieldDirection == FieldDirection.Ref && direction.Expression is ConditionalExpression)
							AddParenthesesSite(direction, sites);
						break;
					case InvocationExpression invocation:
						AddTypeArgumentsSite(invocation, sites);
						AddOmittedArgumentsSite(invocation, invocation.Arguments, sites);
						AddNullForgivingResultSite(invocation, sites);
						break;
					case ObjectCreateExpression creation:
						AddOmittedArgumentsSite(creation, creation.Arguments, sites);
						AddReturnedTargetTypedNewSite(creation, sites);
						break;
				}
			}
		}

		static Site Add(List<Site> sites)
		{
			var site = new Site();
			sites.Add(site);
			return site;
		}

		/// <summary>
		/// "\"Name\"" → "nameof(Name)" (a member of the current type, a parameter or a local) or "nameof(T.Name)"
		/// (a member of a type the statement uses).
		/// </summary>
		void AddNameofSite(PrimitiveExpression literal, string name, AstNode statement, List<Site> sites)
		{
			if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
				return;
			var site = new Site();
			var currentType = statement.Ancestors.OfType<TypeDeclaration>().FirstOrDefault()?.GetSymbol() as ITypeDefinition;
			var member = statement.Ancestors.OfType<EntityDeclaration>().FirstOrDefault(e => e is not TypeDeclaration);
			bool inScope = currentType?.GetMembers(m => m.Name == name).Any() == true
				|| member?.Descendants.OfType<ParameterDeclaration>().Any(p => p.Name == name) == true
				|| member?.Descendants.OfType<VariableInitializer>().Any(v => v.Name == name) == true;
			if (inScope)
				site.Alternatives.Add(() => ReplaceWithNameof(literal, new IdentifierExpression(name)));
			var types = statement.Descendants.Select(n => n switch {
				TypeOfExpression t => t.Type,
				TypeReferenceExpression t => t.Type,
				_ => null
			}).OfType<AstType>().Select(t => (Syntax: t, Type: t.GetResolveResult()?.Type)).Where(t => t.Type is { Kind: not TypeKind.Unknown })
				.GroupBy(t => t.Type).Select(g => g.First()).ToList();
			foreach (var (syntax, type) in types)
			{
				if (type!.GetMembers(m => m.Name == name).Any())
					site.Alternatives.Add(() => ReplaceWithNameof(literal, new MemberReferenceExpression(new TypeReferenceExpression(syntax.Clone()), name)));
			}
			if (site.Alternatives.Count > 0)
				sites.Add(site);
		}

		static Action ReplaceWithNameof(PrimitiveExpression literal, Expression argument)
		{
			var nameof = new InvocationExpression(new IdentifierExpression("nameof"), argument);
			nameof.CopyAnnotationsFrom(literal);
			literal.ReplaceWith(nameof);
			return () => nameof.ReplaceWith(literal);
		}

		/// <summary>"var (a, b) = e" ↔ "(int a, string b) = e" (and "_" stays a discard).</summary>
		void AddDeconstructionSite(AssignmentExpression assignment, List<Site> sites)
		{
			if (assignment.Left is DeclarationExpression { Designation: ParenthesizedVariableDesignation designations } declaration && declaration.Type.IsVar())
			{
				var elements = designations.VariableDesignations.ToList();
				if (elements.Any(e => e is not SingleVariableDesignation))
					return;
				var types = elements.Select(e => ((SingleVariableDesignation)e).Identifier == "_" ? null : VariableType(e)).ToList();
				if (types.Where((t, i) => t is null && ((SingleVariableDesignation)elements[i]).Identifier != "_").Any())
					return;
				Add(sites).Alternatives.Add(() => {
					var tuple = new TupleExpression();
					for (int i = 0; i < elements.Count; i++)
					{
						var designation = (SingleVariableDesignation)elements[i];
						if (types[i] is null)
							tuple.Elements.Add(new IdentifierExpression("_"));
						else
							tuple.Elements.Add(new DeclarationExpression { Type = context.TypeSystemAstBuilder.ConvertType(types[i]!), Designation = (VariableDesignation)designation.Clone() });
					}
					tuple.CopyAnnotationsFrom(declaration);
					declaration.ReplaceWith(tuple);
					return () => tuple.ReplaceWith(declaration);
				});
			}
			else if (assignment.Left is TupleExpression tuple && tuple.Elements.All(e => e is DeclarationExpression { Designation: SingleVariableDesignation } or IdentifierExpression { Identifier: "_" })
				&& tuple.Elements.Any(e => e is DeclarationExpression))
			{
				Add(sites).Alternatives.Add(() => {
					var designation = new ParenthesizedVariableDesignation();
					foreach (var element in tuple.Elements)
					{
						designation.VariableDesignations.Add(element is DeclarationExpression d
							? (VariableDesignation)d.Designation.Clone()
							: new SingleVariableDesignation { Identifier = "_" });
					}
					var declaration = new DeclarationExpression { Type = new SimpleType("var"), Designation = designation };
					declaration.CopyAnnotationsFrom(tuple);
					tuple.ReplaceWith(declaration);
					return () => declaration.ReplaceWith(tuple);
				});
			}
		}

		static IType? VariableType(VariableDesignation designation)
		{
			var type = designation.Annotation<ILVariableResolveResult>()?.Variable.Type ?? designation.GetResolveResult()?.Type;
			return type is null || type.Kind is TypeKind.Unknown or TypeKind.ByReference || type.ContainsAnonymousType() ? null : type;
		}

		/// <summary>"t.Item10" → "t.Rest.Item3" (the same field of a big tuple).</summary>
		static void AddTupleRestSite(MemberReferenceExpression member, List<Site> sites)
		{
			if (!member.MemberName.StartsWith("Item", StringComparison.Ordinal) || !int.TryParse(member.MemberName.Substring(4), out int n) || n < 8
				|| member.Target.GetResolveResult()?.Type is not TupleType)
				return;
			Add(sites).Alternatives.Add(() => {
				var oldName = member.MemberName;
				var oldTarget = member.Target;
				Expression target = oldTarget.Detach();
				int index = n;
				while (index > 7)
				{
					target = new MemberReferenceExpression(target, "Rest");
					index -= 7;
				}
				member.Target = target;
				member.MemberName = "Item" + index;
				return () => {
					member.MemberName = oldName;
					member.Target = oldTarget;
				};
			});
		}

		/// <summary>"ref c ? ref a : ref b" → "ref (c ? ref a : ref b)"</summary>
		static void AddParenthesesSite(DirectionExpression direction, List<Site> sites)
		{
			Add(sites).Alternatives.Add(() => {
				var inner = direction.Expression;
				var parenthesized = new ParenthesizedExpression { Expression = inner.Detach() };
				direction.Expression = parenthesized;
				return () => direction.Expression = ((ParenthesizedExpression)direction.Expression).Expression.Detach();
			});
		}

		/// <summary>"return new T(x);" / "=> new T(x)" → "new(x)" when T is the member's return type.</summary>
		void AddReturnedTargetTypedNewSite(ObjectCreateExpression creation, List<Site> sites)
		{
			if (context.Settings.GetMinimumRequiredVersion() < LanguageVersion.CSharp9_0 || creation.Annotation<TargetTypedNewAnnotation>() is not null
				|| creation.Initializer is not null && !creation.Arguments.Any())
				return;
			bool returned = creation.Parent is ReturnStatement
				|| creation.Parent is PropertyDeclaration or IndexerDeclaration && creation.Slot?.Name == "ExpressionBody";
			if (!returned || creation.GetResolveResult()?.Type is not { Kind: not TypeKind.Unknown } type)
				return;
			var member = creation.Ancestors.OfType<EntityDeclaration>().FirstOrDefault()?.GetSymbol() as IMember;
			if (member is null || creation.Ancestors.Any(a => a is LambdaExpression or AnonymousMethodExpression or LocalFunctionDeclarationStatement)
				|| !NormalizeTypeVisitor.IgnoreNullability.EquivalentTypes(member.ReturnType, type))
				return;
			Add(sites).Alternatives.Add(() => {
				creation.AddAnnotation(TargetTypedNewAnnotation.Instance);
				return () => creation.RemoveAnnotations<TargetTypedNewAnnotation>();
			});
		}

		/// <summary>"*p" → "p[0]" (the same pointer read or write).</summary>
		static void AddPointerIndexSite(UnaryOperatorExpression dereference, List<Site> sites)
		{
			if (dereference.Expression is not (IdentifierExpression or MemberReferenceExpression) || dereference.Expression.GetResolveResult()?.Type is not PointerType)
				return;
			Add(sites).Alternatives.Add(() => {
				var pointer = dereference.Expression.Detach();
				var index = new IndexerExpression(pointer, new PrimitiveExpression(0));
				index.CopyAnnotationsFrom(dereference);
				dereference.ReplaceWith(index);
				return () => {
					pointer.Remove();
					dereference.Expression = pointer;
					index.ReplaceWith(dereference);
				};
			});
		}

		/// <summary>"p->Z" → "(*p).Z"</summary>
		static void AddArrowSite(PointerReferenceExpression arrow, List<Site> sites)
		{
			if (arrow.TypeArguments.Count != 0)
				return;
			Add(sites).Alternatives.Add(() => {
				var pointer = arrow.Target.Detach();
				var member = new MemberReferenceExpression(new ParenthesizedExpression { Expression = new UnaryOperatorExpression(UnaryOperatorType.Dereference, pointer) }, arrow.MemberName);
				member.CopyAnnotationsFrom(arrow);
				arrow.ReplaceWith(member);
				return () => {
					pointer.Remove();
					arrow.Target = pointer;
					member.ReplaceWith(arrow);
				};
			});
		}

		/// <summary>"!M()" → "M() == false" (bool only: the same IL).</summary>
		static void AddEqualsFalseSite(UnaryOperatorExpression not, List<Site> sites)
		{
			if (not.Expression.GetResolveResult()?.Type.IsKnownType(KnownTypeCode.Boolean) != true)
				return;
			Add(sites).Alternatives.Add(() => {
				var comparison = new BinaryOperatorExpression(not.Expression.Detach(), BinaryOperatorType.Equality, new PrimitiveExpression(false));
				comparison.CopyAnnotationsFrom(not);
				not.ReplaceWith(comparison);
				return () => {
					not.Expression = comparison.Left!.Detach();
					comparison.ReplaceWith(not);
				};
			});
		}

		/// <summary>
		/// "(double)(a + b) + d" → "a + b + d": an implicit numeric conversion the compiler applies anyway, because the
		/// other operand already has the target type.
		/// </summary>
		static void AddImplicitCastSite(CastExpression cast, List<Site> sites)
		{
			if (cast.Parent is not BinaryOperatorExpression { Operator: BinaryOperatorType.Add or BinaryOperatorType.Subtract or BinaryOperatorType.Multiply or BinaryOperatorType.Divide } binary)
				return;
			var target = cast.GetResolveResult()?.Type;
			var source = cast.Expression.GetResolveResult()?.Type;
			var other = binary.Left == cast ? binary.Right : binary.Left;
			if (target is null || source is null || other?.GetResolveResult()?.Type is not { } otherType || !otherType.Equals(target))
				return;
			// int/long → double/float/decimal, int → long: widening conversions that keep the value
			bool widening = (source.IsKnownType(KnownTypeCode.Int32) || source.IsKnownType(KnownTypeCode.Int64) || source.IsKnownType(KnownTypeCode.Int16))
				&& (target.IsKnownType(KnownTypeCode.Double) || target.IsKnownType(KnownTypeCode.Decimal) || target.IsKnownType(KnownTypeCode.Int64) && !source.IsKnownType(KnownTypeCode.Int64));
			if (!widening)
				return;
			Add(sites).Alternatives.Add(() => {
				var inner = cast.Expression.Detach();
				cast.ReplaceWith(inner);
				return () => {
					inner.ReplaceWith(cast);
					cast.Expression = inner;
				};
			});
		}

		/// <summary>"M(a)" → "M(a)!" for a call whose result is annotated nullable (and isn't used with "!" already).</summary>
		static void AddNullForgivingResultSite(InvocationExpression invocation, List<Site> sites)
		{
			if (invocation.Parent is UnaryOperatorExpression { Operator: UnaryOperatorType.SuppressNullableWarning } or MemberReferenceExpression
				|| invocation.GetSymbol() is not IMethod { ReturnType: { Nullability: Nullability.Nullable, IsReferenceType: true } })
				return;
			Add(sites).Alternatives.Add(() => {
				var placeholder = new UnaryOperatorExpression();
				invocation.ReplaceWith(placeholder);
				placeholder.Operator = UnaryOperatorType.SuppressNullableWarning;
				placeholder.Expression = invocation;
				return () => {
					invocation.Remove();
					placeholder.ReplaceWith(invocation);
				};
			});
		}

		/// <summary>"M().P" → "M()!.P" (the source's null-forgiving operator; no IL).</summary>
		static void AddNullForgivingSite(MemberReferenceExpression member, List<Site> sites)
		{
			if (member.Target is not (InvocationExpression or MemberReferenceExpression) || member.Target.GetResolveResult()?.Type is not { IsReferenceType: true })
				return;
			Add(sites).Alternatives.Add(() => {
				var target = member.Target;
				var placeholder = new UnaryOperatorExpression(UnaryOperatorType.SuppressNullableWarning, target.Detach());
				placeholder.CopyAnnotationsFrom(target);
				member.Target = placeholder;
				return () => member.Target = ((UnaryOperatorExpression)member.Target).Expression.Detach();
			});
		}

		/// <summary>"out var x" / "out T x".</summary>
		void AddOutVarTypeSite(OutVarDeclarationExpression outVar, List<Site> sites)
		{
			var type = outVar.Variable.Annotation<ILVariableResolveResult>()?.Variable.Type;
			if (type is null || type.Kind is TypeKind.Unknown or TypeKind.ByReference || type.ContainsAnonymousType())
				return;
			AstType alternative = outVar.Type.IsVar() ? context.TypeSystemAstBuilder.ConvertType(type) : new SimpleType("var");
			Add(sites).Alternatives.Add(() => {
				var old = outVar.Type;
				alternative.CopyAnnotationsFrom(old);
				old.ReplaceWith(alternative);
				return () => alternative.ReplaceWith(old);
			});
		}

		/// <summary>"c ? a : (b ?? d)" → "c ? a : b ?? d", "x = (c ? a : b)" → "x = c ? a : b" (no readability parentheses).</summary>
		static void AddMinimalParenthesesSite(AstNode node, List<Site> sites)
		{
			if (node.Annotation<MinimalParenthesesAnnotation>() is not null)
				return;
			Add(sites).Alternatives.Add(() => {
				node.AddAnnotation(MinimalParenthesesAnnotation.Instance);
				return () => node.RemoveAnnotations<MinimalParenthesesAnnotation>();
			});
		}

		/// <summary>"x += y" → "x = x + y" (statement level, simple targets, types where both mean the same).</summary>
		static void AddCompoundAssignmentSite(AssignmentExpression assignment, List<Site> sites)
		{
			if (assignment.Parent is not ExpressionStatement || AssignmentExpression.GetCorrespondingBinaryOperator(assignment.Operator) is not { } op
				|| op == BinaryOperatorType.NullCoalescing)
				return;
			if (assignment.Left is not (IdentifierExpression or MemberReferenceExpression { Target: ThisReferenceExpression or IdentifierExpression or TypeReferenceExpression }))
				return;
			if (assignment.Left.GetSymbol() is IEvent)
				return;
			var type = assignment.Left.GetResolveResult().Type;
			bool sameType = type.Kind == TypeKind.Dynamic
				|| type.GetDefinition()?.KnownTypeCode is KnownTypeCode.Int32 or KnownTypeCode.UInt32 or KnownTypeCode.Int64 or KnownTypeCode.UInt64
					or KnownTypeCode.Single or KnownTypeCode.Double or KnownTypeCode.Decimal or KnownTypeCode.String or KnownTypeCode.Boolean;
			if (!sameType || (type.IsKnownType(KnownTypeCode.Boolean) && op is not (BinaryOperatorType.BitwiseAnd or BinaryOperatorType.BitwiseOr or BinaryOperatorType.ExclusiveOr)))
				return;
			Add(sites).Alternatives.Add(() => {
				var left = assignment.Left;
				var right = assignment.Right;
				var expanded = new AssignmentExpression(left.Clone(), new BinaryOperatorExpression(left.Clone(), op, right.Detach()));
				expanded.CopyAnnotationsFrom(assignment);
				assignment.ReplaceWith(expanded);
				return () => {
					var movedRight = ((BinaryOperatorExpression)expanded.Right).Right!;
					assignment.Right = movedRight.Detach();
					expanded.ReplaceWith(assignment);
				};
			});
		}

		/// <summary>"var x = new T()" / "T x = new T()" / "T x = new()" (and "var" for any initializer of exactly the declared type).</summary>
		void AddDeclarationSite(VariableDeclarationStatement declaration, List<Site> sites)
		{
			if (declaration.Variables.Count != 1 || declaration.Modifiers != Modifiers.None
				|| declaration.Variables.Single() is not { Initializer: { } initializer } variable)
				return;
			var variableType = variable.GetILVariable()?.Type ?? declaration.Type.GetResolveResult()?.Type;
			if (variableType is null || variableType.Kind is TypeKind.Unknown or TypeKind.ByReference or TypeKind.Dynamic || variableType.ContainsAnonymousType())
				return;
			var initializerType = initializer.GetResolveResult()?.Type;
			bool exactType = initializerType is not null && NormalizeTypeVisitor.IgnoreNullability.EquivalentTypes(initializerType, variableType)
				&& SameTupleNames(initializerType, variableType);
			bool varAllowed = exactType && initializer is not (NullReferenceExpression or DefaultValueExpression { Type: null } or LambdaExpression
				or AnonymousMethodExpression or StackAllocExpression or CollectionExpression or ArrayInitializerExpression)
				&& initializer.GetResolveResult() is not MethodGroupResolveResult;
			bool isVar = declaration.Type.IsVar();
			var site = Add(sites);
			if (isVar)
			{
				site.Alternatives.Add(() => SetDeclarationType(declaration, context.TypeSystemAstBuilder.ConvertType(variableType)));
			}
			else if (varAllowed)
			{
				site.Alternatives.Add(() => SetDeclarationType(declaration, new SimpleType("var")));
			}
			else if (initializer is TupleExpression tuple && variableType is TupleType named
				&& named.ElementNames.Length == tuple.Elements.Count && named.ElementNames.Any(n => n is not null)
				&& !tuple.Elements.Any(e => e is NamedArgumentExpression))
			{
				// "(int Min, string name) t = (s.Min, "n")" → "var t = (s.Min, name: "n")" (Min is inferred from s.Min)
				site.Alternatives.Add(() => {
					var undoType = SetDeclarationType(declaration, new SimpleType("var"));
					var elements = tuple.Elements.ToList();
					var undos = new List<Action>();
					for (int i = 0; i < elements.Count; i++)
					{
						string? name = named.ElementNames[i];
						if (name is null || InferredName(elements[i]) == name)
							continue;
						var element = elements[i];
						var namedElement = new NamedArgumentExpression { Name = name };
						element.ReplaceWith(namedElement);
						namedElement.Expression = element;
						undos.Add(() => {
							element.Remove();
							namedElement.ReplaceWith(element);
						});
					}
					return () => {
						for (int i = undos.Count - 1; i >= 0; i--)
							undos[i]();
						undoType();
					};
				});
			}
			// target-typed "new(...)"
			if (initializer is ObjectCreateExpression creation && exactType && context.Settings.GetMinimumRequiredVersion() >= LanguageVersion.CSharp9_0
				&& creation.Annotation<TargetTypedNewAnnotation>() is null)
			{
				site.Alternatives.Add(() => {
					var undoType = isVar ? SetDeclarationType(declaration, context.TypeSystemAstBuilder.ConvertType(variableType)) : null;
					creation.AddAnnotation(TargetTypedNewAnnotation.Instance);
					return () => {
						creation.RemoveAnnotations<TargetTypedNewAnnotation>();
						undoType?.Invoke();
					};
				});
			}
			if (site.Alternatives.Count == 0)
				sites.Remove(site);
		}

		/// <summary>The tuple element name C# infers from an element expression ("x" from x, "Min" from s.Min).</summary>
		static string? InferredName(Expression element) => element switch {
			IdentifierExpression id => id.Identifier,
			MemberReferenceExpression member => member.MemberName,
			_ => null
		};

		static Action SetDeclarationType(VariableDeclarationStatement declaration, AstType type)
		{
			var old = declaration.Type;
			type.CopyAnnotationsFrom(old);
			old.ReplaceWith(type);
			return () => type.ReplaceWith(old);
		}

		/// <summary>"1.0" → "1" for a double/float/decimal argument (the int converts implicitly).</summary>
		static void AddIntegralLiteralSite(PrimitiveExpression literal, List<Site> sites)
		{
			if (literal.Value is not (double or float or decimal))
				return;
			// the type the literal converts to: a parameter's, or the other operand's / the compound assignment's target
			IType? parameterType = ParameterOf(literal)?.Type;
			if (parameterType is null && literal.Parent is BinaryOperatorExpression { Operator: BinaryOperatorType.Add or BinaryOperatorType.Subtract
				or BinaryOperatorType.Multiply or BinaryOperatorType.Divide or BinaryOperatorType.LessThan or BinaryOperatorType.LessThanOrEqual
				or BinaryOperatorType.GreaterThan or BinaryOperatorType.GreaterThanOrEqual or BinaryOperatorType.Equality or BinaryOperatorType.InEquality } binary)
			{
				var other = binary.Left == literal ? binary.Right : binary.Left;
				if (other is not PrimitiveExpression)
					parameterType = other?.GetResolveResult()?.Type;
			}
			if (parameterType is null && literal.Parent is AssignmentExpression { Operator: not AssignmentOperatorType.Assign } compound && compound.Right == literal)
				parameterType = compound.Left.GetResolveResult()?.Type;
			if (parameterType is null || !parameterType.Equals(literal.GetResolveResult()?.Type))
				return;
			if (!(parameterType.IsKnownType(KnownTypeCode.Double) || parameterType.IsKnownType(KnownTypeCode.Single) || parameterType.IsKnownType(KnownTypeCode.Decimal)))
				return;
			decimal value;
			try
			{
				value = Convert.ToDecimal(literal.Value, CultureInfo.InvariantCulture);
			}
			catch (OverflowException)
			{
				return;
			}
			if (value != decimal.Truncate(value) || value < int.MinValue || value > int.MaxValue
				|| (literal.Value is double d && d == 0 && BitConverter.DoubleToInt64Bits(d) < 0))
				return;
			Add(sites).Alternatives.Add(() => {
				var integer = new PrimitiveExpression((int)value);
				integer.CopyAnnotationsFrom(literal);
				literal.ReplaceWith(integer);
				return () => integer.ReplaceWith(literal);
			});
		}

		/// <summary>"in x" → "ref x" (ref readonly parameter) or "x" (in / ref readonly parameter).</summary>
		static void AddDirectionSite(DirectionExpression direction, List<Site> sites)
		{
			if (direction.FieldDirection != FieldDirection.In || ParameterOf(direction) is not { } parameter)
				return;
			var site = Add(sites);
			if (parameter.ReferenceKind == ReferenceKind.RefReadOnly)
				site.Alternatives.Add(() => SetDirection(direction, FieldDirection.Ref));
			if (parameter.ReferenceKind is ReferenceKind.In or ReferenceKind.RefReadOnly)
			{
				site.Alternatives.Add(() => {
					var value = direction.Expression;
					var placeholder = value.Detach();
					direction.ReplaceWith(placeholder);
					return () => {
						placeholder.ReplaceWith(direction);
						direction.Expression = placeholder;
					};
				});
			}
		}

		static Action SetDirection(DirectionExpression direction, FieldDirection value)
		{
			var old = direction.FieldDirection;
			direction.FieldDirection = value;
			return () => direction.FieldDirection = old;
		}

		/// <summary>"M(a, b)" → "M&lt;T&gt;(a, b)" (the source may have spelled out inferable type arguments).</summary>
		void AddTypeArgumentsSite(InvocationExpression invocation, List<Site> sites)
		{
			if (invocation.GetResolveResult() is not CSharpInvocationResolveResult { Member: IMethod { TypeArguments.Count: > 0 } method }
				|| method.TypeArguments.Any(t => t.Kind is TypeKind.Unknown || t.ContainsAnonymousType()))
				return;
			AstNodeCollection<AstType> typeArguments;
			switch (invocation.Target)
			{
				case IdentifierExpression { TypeArguments.Count: 0 } identifier:
					typeArguments = identifier.TypeArguments;
					break;
				case MemberReferenceExpression { TypeArguments.Count: 0 } member:
					typeArguments = member.TypeArguments;
					break;
				default:
					return;
			}
			Add(sites).Alternatives.Add(() => {
				var added = method.TypeArguments.Select(t => context.TypeSystemAstBuilder.ConvertType(t)).ToList();
				typeArguments.AddRange(added);
				return () => {
					foreach (var a in added)
						a.Remove();
				};
			});
		}

		/// <summary>
		/// "M(a, 5, 'x')" with defaults "b = 5" → "M(a, c: 'x')": arguments equal to their parameter's default value
		/// are left out, the ones after them are named.
		/// </summary>
		static void AddOmittedArgumentsSite(Expression call, AstNodeCollection<Expression> arguments, List<Site> sites)
		{
			if (call.GetResolveResult() is not CSharpInvocationResolveResult { IsExpandedForm: false } resolved
				|| arguments.Any(a => a is NamedArgumentExpression))
				return;
			int offset = resolved.IsExtensionMethodInvocation ? 1 : 0;
			var parameters = resolved.Member.Parameters;
			var list = arguments.ToList();
			if (list.Count + offset != parameters.Count)
				return;
			var omittable = new List<int>();
			for (int i = 0; i < list.Count; i++)
			{
				var parameter = parameters[i + offset];
				if (parameter.IsOptional && !parameter.IsParams && IsDefaultArgument(list[i], parameter))
					omittable.Add(i);
			}
			// trailing ones are left out by the decompiler already; a subset that is a strict prefix-free choice
			if (omittable.Count == 0 || omittable.Count > 6)
				return;
			var site = Add(sites);
			for (int mask = 1; mask < (1 << omittable.Count); mask++)
			{
				var omitted = new HashSet<int>(omittable.Where((_, bit) => (mask & (1 << bit)) != 0));
				site.Alternatives.Add(() => {
					int first = omitted.Min();
					var rebuilt = new List<Expression>();
					foreach (var argument in arguments.ToList())
						argument.Remove();
					for (int i = 0; i < list.Count; i++)
					{
						if (omitted.Contains(i))
							continue;
						rebuilt.Add(i > first ? new NamedArgumentExpression { Name = parameters[i + offset].Name, Expression = list[i] } : list[i]);
					}
					arguments.AddRange(rebuilt);
					return () => {
						foreach (var argument in arguments.ToList())
							argument.Remove();
						foreach (var argument in list)
							argument.Remove();
						arguments.AddRange(list);
					};
				});
			}
		}

		/// <summary>
		/// The argument is what the compiler passes for the optional parameter when it is left out: its default value,
		/// the [DefaultParameterValue] value, or Type.Missing for an [Optional] object parameter without one.
		/// </summary>
		static bool IsDefaultArgument(Expression argument, IParameter parameter)
		{
			var resolved = argument.GetResolveResult();
			if (parameter.HasConstantValueInSignature)
				return resolved is { IsCompileTimeConstant: true } && ConstantsEqual(resolved.ConstantValue, parameter.GetConstantValue());
			var defaultValue = parameter.GetAttributes()
				.FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.InteropServices.DefaultParameterValueAttribute");
			if (defaultValue is not null)
			{
				return defaultValue.FixedArguments.Length == 1 && resolved is { IsCompileTimeConstant: true }
					&& ConstantsEqual(resolved.ConstantValue, defaultValue.FixedArguments[0].Value);
			}
			return parameter.Type.IsKnownType(KnownTypeCode.Object)
				&& argument.GetSymbol() is IField { Name: "Missing", DeclaringType.FullName: "System.Type" };
		}

		/// <summary>The parameter an argument (or a node right inside one) is passed to.</summary>
		static IParameter? ParameterOf(Expression argument)
		{
			if (argument.Parent is not (InvocationExpression or ObjectCreateExpression))
				return null;
			var call = (Expression)argument.Parent;
			if (call.GetResolveResult() is not CSharpInvocationResolveResult { IsExpandedForm: false } resolved)
				return null;
			var arguments = call is InvocationExpression invocation ? invocation.Arguments : ((ObjectCreateExpression)call).Arguments;
			if (!arguments.Contains(argument) || arguments.Any(a => a is NamedArgumentExpression))
				return null;
			int index = arguments.TakeWhile(a => a != argument).Count() + (resolved.IsExtensionMethodInvocation ? 1 : 0);
			return index < resolved.Member.Parameters.Count ? resolved.Member.Parameters[index] : null;
		}

		/// <summary>Same constant value (numbers of different types compared by value).</summary>
		static bool ConstantsEqual(object? a, object? b)
		{
			if (a is null || b is null)
				return a is null && b is null;
			if (a is string || b is string || a is bool || b is bool)
				return a.Equals(b);
			if (a is char ca)
				a = (int)ca;
			if (b is char cb)
				b = (int)cb;
			try
			{
				return Convert.ToDecimal(a, CultureInfo.InvariantCulture) == Convert.ToDecimal(b, CultureInfo.InvariantCulture);
			}
			catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException)
			{
				return false;
			}
		}

		static bool SameTupleNames(IType a, IType b)
		{
			switch (a)
			{
				case TupleType ta when b is TupleType tb:
					return ta.ElementNames.SequenceEqual(tb.ElementNames) && ta.ElementTypes.Zip(tb.ElementTypes, SameTupleNames).All(x => x);
				case ParameterizedType pa when b is ParameterizedType pb:
					return pa.TypeArguments.Zip(pb.TypeArguments, SameTupleNames).All(x => x);
				case ArrayType aa when b is ArrayType ab:
					return SameTupleNames(aa.ElementType, ab.ElementType);
				default:
					return true;
			}
		}

		#endregion
	}

	/// <summary>NetSpy: the annotated object creation is written "new(...)" (its type comes from the target).</summary>
	public sealed class TargetTypedNewAnnotation
	{
		public static readonly TargetTypedNewAnnotation Instance = new TargetTypedNewAnnotation();

		TargetTypedNewAnnotation()
		{
		}
	}
}
