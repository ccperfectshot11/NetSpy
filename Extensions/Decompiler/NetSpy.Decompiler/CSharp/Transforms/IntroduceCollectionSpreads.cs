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

using System.Collections.Generic;
using System.Linq;

using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.IL;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: C# 12 collection expressions with spread elements. The compiler builds them by hand:
	/// <code>
	/// int num = 0; int[] array2 = array; Span&lt;int&gt; span2 = span;   // the elements, evaluated first
	/// int num2 = 0;
	/// var array3 = new int[2 + (array2.Length + span2.Length)];
	/// array3[num2] = num; num2++;
	/// var ros = new ReadOnlySpan&lt;int&gt;(array2); ros.CopyTo(new Span&lt;int&gt;(array3).Slice(num2, ros.Length)); num2 += ros.Length;
	/// span2.CopyTo(new Span&lt;int&gt;(array3).Slice(num2, span2.Length)); num2 += span2.Length;
	/// array3[num2] = 100;
	/// int[] spread = array3;                                    → int[] spread = [0, .. array, .. span, 100];
	///
	/// var list = new List&lt;int&gt;(); list.AddRange(a); list.Add(b);
	/// List&lt;int&gt; result = list;                                → List&lt;int&gt; result = [.. a, b];
	/// </code>
	/// Every helper variable must be a compiler temporary that is used nowhere else.
	/// </summary>
	public sealed class IntroduceCollectionSpreads : DepthFirstAstVisitor, IAstTransform
	{
		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.CollectionExpressions)
				return;
			rootNode.AcceptVisitor(this);
		}

		public override void VisitBlockStatement(BlockStatement block)
		{
			base.VisitBlockStatement(block);
			bool changed;
			do
			{
				changed = false;
				var statements = block.Statements.ToList();
				for (int i = 0; i < statements.Count && !changed; i++)
					changed = TryArray(block, statements, i) || TryList(block, statements, i);
			} while (changed);
		}

		#region Arrays

		/// <summary>statements[start] is "int idx = 0;" of an array spread.</summary>
		static bool TryArray(BlockStatement block, List<Statement> statements, int start)
		{
			// int idx = 0;
			if (Declaration(statements[start]) is not (var idxVar, PrimitiveExpression { Value: 0 }) || !idxVar.HasGeneratedName)
				return false;
			int i = start + 1;
			// var arr = new T[K + (a.Length + b.Length)];
			if (i >= statements.Count || Declaration(statements[i]) is not (var arrVar, ArrayCreateExpression { Arguments.Count: 1, Initializer: null } create)
				|| !arrVar.HasGeneratedName || create.Arguments.Single() is not { } size)
				return false;
			if (!ParseSize(size, out int singleCount, out var spreadLengths))
				return false;
			i++;
			var elements = new List<(bool Spread, Expression Value)>();
			int seenSingles = 0, seenSpreads = 0;
			bool lastWasFinal = false;
			while (i < statements.Count && !lastWasFinal)
			{
				// arr[idx] = value; [idx++;]
				if (statements[i] is ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign, Left: IndexerExpression { Target: IdentifierExpression arrRef, Arguments.Count: 1 } indexer, Right: { } value } }
					&& Is(arrRef, arrVar) && indexer.Arguments.Single() is IdentifierExpression idxRef && Is(idxRef, idxVar))
				{
					elements.Add((false, value));
					seenSingles++;
					i++;
					if (i < statements.Count && IsIncrement(statements[i], idxVar))
						i++;
					else
						lastWasFinal = true;
					continue;
				}
				// [var ros = new ReadOnlySpan<T>(src);] X.CopyTo(new Span<T>(arr).Slice(idx, X.Length)); [idx += X.Length;]
				ILVariable? rosVar = null;
				Expression? rosSource = null;
				int j = i;
				if (Declaration(statements[j]) is (var r, ObjectCreateExpression { Arguments.Count: 1 } rosCreate)
					&& r.HasGeneratedName && IsSpanType(rosCreate.GetResolveResult().Type, KnownTypeCode.ReadOnlySpanOfT))
				{
					rosVar = r;
					rosSource = rosCreate.Arguments.Single();
					j++;
				}
				if (j < statements.Count && statements[j] is ExpressionStatement { Expression: InvocationExpression { Target: MemberReferenceExpression { MemberName: "CopyTo", Target: IdentifierExpression copied }, Arguments.Count: 1 } copyTo }
					&& IsSliceOfArray(copyTo.Arguments.Single(), arrVar, idxVar, copied.GetILVariable()))
				{
					var copiedVar = copied.GetILVariable();
					if (copiedVar is null || (rosVar is not null && copiedVar != rosVar))
						return false;
					if (seenSpreads >= spreadLengths.Count || spreadLengths[seenSpreads] != (rosVar is not null ? GetVariable(rosSource!) : copiedVar))
						return false;
					elements.Add((true, rosVar is not null ? rosSource! : copied));
					seenSpreads++;
					i = j + 1;
					if (i < statements.Count && IsAddLength(statements[i], idxVar, copiedVar))
						i++;
					else
						lastWasFinal = true;
					continue;
				}
				break;
			}
			if (seenSingles != singleCount || seenSpreads != spreadLengths.Count || elements.Count == 0)
				return false;
			// T[] result = arr;
			if (i >= statements.Count || statements[i] is not VariableDeclarationStatement { Variables.Count: 1 } resultDecl
				|| resultDecl.Variables.Single().Initializer is not IdentifierExpression resultValue || !Is(resultValue, arrVar))
				return false;
			int end = i;

			// the element temporaries right before "int idx = 0;" (in element order)
			var range = statements.Skip(start).Take(end - start + 1).ToList();
			var temps = new Dictionary<ILVariable, (Statement Statement, Expression Value)>();
			for (int k = start - 1; k >= 0; k--)
			{
				if (Declaration(statements[k]) is not (var temp, { } tempValue) || !temp.HasGeneratedName)
					break;
				temps[temp] = (statements[k], tempValue);
			}
			var collection = new CollectionExpression();
			var usedTemps = new List<ILVariable>();
			foreach (var (spread, value) in elements)
			{
				var element = value;
				if (GetVariable(value) is { } v && temps.TryGetValue(v, out var temp))
				{
					element = temp.Value;
					usedTemps.Add(v);
				}
				collection.Elements.Add(spread ? new SpreadElement { Expression = element.Clone() } : element.Clone());
			}
			// the helpers and the used temporaries must not be used anywhere else
			var scope = block.Ancestors.OfType<EntityDeclaration>().FirstOrDefault() as AstNode ?? block;
			var inRange = new HashSet<AstNode>(range.SelectMany(s => s.DescendantsAndSelf));
			foreach (var temp in usedTemps)
				inRange.UnionWith(temps[temp].Statement.DescendantsAndSelf);
			foreach (var helper in new[] { idxVar, arrVar }.Concat(usedTemps))
			{
				if (scope.Descendants.OfType<IdentifierExpression>().Any(id => id.GetILVariable() == helper && !inRange.Contains(id)))
					return false;
			}
			// temporaries must be consecutive and directly before the index declaration
			var tempStatements = usedTemps.Select(t => temps[t].Statement).ToList();
			if (tempStatements.Count != temps.Count(t => usedTemps.Contains(t.Key)))
				return false;

			resultDecl.Variables.Single().Initializer = collection;
			foreach (var s in range.Take(range.Count - 1).Concat(tempStatements))
				s.Remove();
			return true;
		}

		/// <summary>"K + (a.Length + b.Length)", "a.Length + b.Length", "K + a.Length", "a.Length".</summary>
		static bool ParseSize(Expression size, out int singleCount, out List<ILVariable> spreadLengths)
		{
			singleCount = 0;
			spreadLengths = new List<ILVariable>();
			if (size is BinaryOperatorExpression { Operator: BinaryOperatorType.Add, Left: PrimitiveExpression { Value: int k }, Right: { } rest })
			{
				singleCount = k;
				size = rest;
			}
			while (size is ParenthesizedExpression { Expression: { } inner })
				size = inner;
			return CollectLengths(size, spreadLengths);
		}

		static bool CollectLengths(Expression expression, List<ILVariable> lengths)
		{
			while (expression is ParenthesizedExpression { Expression: { } inner })
				expression = inner;
			switch (expression)
			{
				case BinaryOperatorExpression { Operator: BinaryOperatorType.Add, Left: { } left, Right: { } right }:
					return CollectLengths(left, lengths) && CollectLengths(right, lengths);
				case MemberReferenceExpression { MemberName: "Length", Target: IdentifierExpression id } when id.GetILVariable() is { } v:
					lengths.Add(v);
					return true;
				default:
					return false;
			}
		}

		/// <summary>"new Span&lt;T&gt;(arr).Slice(idx, X.Length)"</summary>
		static bool IsSliceOfArray(Expression expression, ILVariable arrVar, ILVariable idxVar, ILVariable? lengthOf)
		{
			return expression is InvocationExpression { Target: MemberReferenceExpression { MemberName: "Slice", Target: ObjectCreateExpression { Arguments.Count: 1 } spanCreate }, Arguments.Count: 2 } slice
				&& IsSpanType(spanCreate.GetResolveResult().Type, KnownTypeCode.SpanOfT)
				&& spanCreate.Arguments.Single() is IdentifierExpression arr && Is(arr, arrVar)
				&& slice.Arguments.First() is IdentifierExpression idx && Is(idx, idxVar)
				&& slice.Arguments.Last() is MemberReferenceExpression { MemberName: "Length", Target: IdentifierExpression lengthTarget }
				&& lengthTarget.GetILVariable() == lengthOf;
		}

		static bool IsIncrement(Statement statement, ILVariable variable) =>
			statement is ExpressionStatement { Expression: UnaryOperatorExpression { Operator: UnaryOperatorType.PostIncrement or UnaryOperatorType.Increment, Expression: IdentifierExpression id } }
			&& Is(id, variable);

		static bool IsAddLength(Statement statement, ILVariable idxVar, ILVariable lengthOf) =>
			statement is ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Add, Left: IdentifierExpression idx, Right: MemberReferenceExpression { MemberName: "Length", Target: IdentifierExpression target } } }
			&& Is(idx, idxVar) && target.GetILVariable() == lengthOf;

		static bool IsSpanType(IType type, KnownTypeCode code) => type.GetDefinition()?.KnownTypeCode == code;

		#endregion

		#region Lists

		/// <summary>"var list = new List&lt;T&gt;(); list.Add(a); list.AddRange(b); List&lt;T&gt; result = list;"</summary>
		static bool TryList(BlockStatement block, List<Statement> statements, int start)
		{
			if (Declaration(statements[start]) is not (var listVar, ObjectCreateExpression { Arguments.Count: 0, Initializer: null } create)
				|| !listVar.HasGeneratedName || create.GetResolveResult().Type is not { } listType
				|| listType.GetDefinition() is not { FullName: "System.Collections.Generic.List", TypeParameterCount: 1 })
				return false;
			var collection = new CollectionExpression();
			int i = start + 1;
			while (i < statements.Count && statements[i] is ExpressionStatement { Expression: InvocationExpression { Target: MemberReferenceExpression { MemberName: "Add" or "AddRange" } member, Arguments.Count: 1 } call }
				&& member.Target is IdentifierExpression target && Is(target, listVar))
			{
				var argument = call.Arguments.Single().Clone();
				collection.Elements.Add(member.MemberName == "AddRange" ? new SpreadElement { Expression = argument } : argument);
				i++;
			}
			// only spreads: plain Add calls are a collection initializer, already handled
			if (!collection.Elements.Any(e => e is SpreadElement))
				return false;
			if (i >= statements.Count || statements[i] is not VariableDeclarationStatement { Variables.Count: 1 } resultDecl
				|| resultDecl.Variables.Single().Initializer is not IdentifierExpression resultValue || !Is(resultValue, listVar)
				|| resultDecl.Type.GetResolveResult()?.Type is not { } declaredType || !declaredType.Equals(listType))
				return false;
			var scope = block.Ancestors.OfType<EntityDeclaration>().FirstOrDefault() as AstNode ?? block;
			var inRange = new HashSet<AstNode>(statements.Skip(start).Take(i - start + 1).SelectMany(s => s.DescendantsAndSelf));
			if (scope.Descendants.OfType<IdentifierExpression>().Any(id => id.GetILVariable() == listVar && !inRange.Contains(id)))
				return false;
			resultDecl.Variables.Single().Initializer = collection;
			for (int k = start; k < i; k++)
				statements[k].Remove();
			return true;
		}

		#endregion

		static (ILVariable Variable, Expression? Value)? Declaration(Statement statement)
		{
			if (statement is VariableDeclarationStatement { Variables.Count: 1, Modifiers: Modifiers.None } decl
				&& decl.Variables.Single() is { } variable && variable.GetILVariable() is { } v)
				return (v, variable.Initializer);
			return null;
		}

		static bool Is(IdentifierExpression id, ILVariable variable) => id.GetILVariable() == variable;

		static ILVariable? GetVariable(Expression expression) => (expression as IdentifierExpression)?.GetILVariable();
	}
}
