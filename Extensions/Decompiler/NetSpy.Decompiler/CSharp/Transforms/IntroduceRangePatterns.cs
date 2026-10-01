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

using NetSpy.Decompiler.CSharp.Syntax;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: "c is >= 'a' and &lt;= 'z' or >= 'A' and &lt;= 'Z'" compiles to range checks that are decompiled
	/// as a switch with one case label per value:
	/// <code>
	/// switch (c) { case 'A': ... case 'z': return true; default: return false; }
	/// </code>
	/// Such a switch becomes "return c is >= 'A' and &lt;= 'Z' or >= 'a' and &lt;= 'z';" again. Only switches with at
	/// least one run of 3+ consecutive values are changed (a hand written switch rarely lists a whole range).
	/// </summary>
	public sealed class IntroduceRangePatterns : DepthFirstAstVisitor, IAstTransform
	{
		const int MinRangeLength = 3;

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.RelationalPatterns || !context.Settings.PatternCombinators)
				return;
			rootNode.AcceptVisitor(this);
		}

		public override void VisitSwitchStatement(SwitchStatement switchStatement)
		{
			base.VisitSwitchStatement(switchStatement);
			if (TryBuild(switchStatement) is { } replacement)
				switchStatement.ReplaceWith(replacement);
		}

		static ReturnStatement? TryBuild(SwitchStatement switchStatement)
		{
			if (switchStatement.SwitchSections.Count != 2)
				return null;
			SwitchSection? cases = null, @default = null;
			foreach (var section in switchStatement.SwitchSections)
			{
				bool hasDefault = section.CaseLabels.Any(l => l.Expression is null);
				if (hasDefault)
				{
					if (section.CaseLabels.Count != 1)
						return null;
					@default = section;
				}
				else
				{
					cases = section;
				}
			}
			if (cases is null || @default is null)
				return null;
			if (ReturnedBool(cases) is not bool casesResult || ReturnedBool(@default) is not bool defaultResult || casesResult == defaultResult)
				return null;

			// the label values (char or integer constants)
			var values = new SortedDictionary<long, PrimitiveExpression>();
			foreach (var label in cases.CaseLabels)
			{
				if (label.Expression is not PrimitiveExpression { Value: { } value } primitive || ToInt64(value) is not long key)
					return null;
				values[key] = primitive;
			}
			var runs = Runs(values.Keys.ToList());
			if (!runs.Any(r => r.Last - r.First + 1 >= MinRangeLength))
				return null;

			Expression? pattern = null;
			foreach (var (first, last) in runs)
			{
				Expression part;
				if (first == last)
				{
					part = values[first].Clone();
				}
				else if (last == first + 1)
				{
					part = new BinaryOperatorExpression(values[first].Clone(), BinaryOperatorType.PatternOr, values[last].Clone());
				}
				else
				{
					part = new BinaryOperatorExpression(
						new UnaryOperatorExpression(UnaryOperatorType.PatternRelationalGreaterThanOrEqual, values[first].Clone()),
						BinaryOperatorType.PatternAnd,
						new UnaryOperatorExpression(UnaryOperatorType.PatternRelationalLessThanOrEqual, values[last].Clone()));
				}
				pattern = pattern is null ? part : new BinaryOperatorExpression(pattern, BinaryOperatorType.PatternOr, part);
			}
			if (pattern is null)
				return null;
			// "return true for these values" → "x is pattern", "return false for these values" → "x is not (pattern)"
			if (!casesResult)
				pattern = new UnaryOperatorExpression(UnaryOperatorType.PatternNot, new ParenthesizedExpression(pattern));
			var subject = switchStatement.Expression.Detach();
			return new ReturnStatement(new BinaryOperatorExpression(subject, BinaryOperatorType.IsPattern, pattern));
		}

		/// <summary>The constant of a section that is just "return true;" / "return false;".</summary>
		static bool? ReturnedBool(SwitchSection section)
		{
			if (section.Statements.Count != 1)
				return null;
			var statement = section.Statements.Single();
			if (statement is BlockStatement { Statements.Count: 1 } block)
				statement = block.Statements.Single();
			return statement is ReturnStatement { Expression: PrimitiveExpression { Value: bool b } } ? b : null;
		}

		static long? ToInt64(object value) => value switch {
			char c => c,
			byte b => b,
			sbyte sb => sb,
			short s => s,
			ushort us => us,
			int i => i,
			uint ui => ui,
			long l => l,
			_ => null,
		};

		static List<(long First, long Last)> Runs(List<long> sorted)
		{
			var runs = new List<(long, long)>();
			for (int i = 0; i < sorted.Count;)
			{
				int j = i;
				while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
					j++;
				runs.Add((sorted[i], sorted[j]));
				i = j + 1;
			}
			return runs;
		}
	}
}
