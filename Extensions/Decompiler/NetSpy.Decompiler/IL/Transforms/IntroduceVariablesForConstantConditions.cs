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

using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.IL.Transforms
{
	/// <summary>
	/// NetSpy: in Release builds the C# compiler keeps a local on the IL stack when it's written once and
	/// read once, eg. <c>bool flag = true; var x = flag ? a : b;</c> becomes <c>ldc.i4.1; brtrue</c>.
	/// Decompiled as is, the condition is a constant (<c>if (1 == 0)</c>) and the C# compiler reports
	/// unreachable code (CS0162). The variable is restored instead:
	/// <c>bool flag = false; if (flag) ...</c>, which compiles without warnings like the original code.
	/// </summary>
	public class IntroduceVariablesForConstantConditions : IILTransform
	{
		public void Run(ILFunction function, ILTransformContext context)
		{
			foreach (var ifInst in function.Descendants.OfType<IfInstruction>().ToArray())
			{
				if (!TryEvaluate(ifInst.Condition, out bool value))
					continue;
				var statement = GetStatement(ifInst);
				if (statement?.Parent is not Block block)
					continue;
				context.Step("Introduce variable for constant condition", ifInst);
				var variable = new ILVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean), StackType.I4);
				function.Variables.Add(variable);
				block.Instructions.Insert(statement.ChildIndex, new StLoc(variable, new LdcI4(value ? 1 : 0)));
				ifInst.Condition = new LdLoc(variable);
			}
		}

		static ILInstruction? GetStatement(ILInstruction inst)
		{
			for (var node = inst; node != null; node = node.Parent)
			{
				if (node.Parent is Block { Kind: BlockKind.ControlFlow })
					return node;
			}
			return null;
		}

		static bool TryEvaluate(ILInstruction inst, out bool value)
		{
			switch (inst)
			{
				case LdcI4 ldc:
					value = ldc.Value != 0;
					return true;
				case Comp comp when comp.LiftingKind == ComparisonLiftingKind.None:
					if (comp.Left is LdcI4 l && comp.Right is LdcI4 r)
					{
						switch (comp.Kind)
						{
							case ComparisonKind.Equality: value = l.Value == r.Value; return true;
							case ComparisonKind.Inequality: value = l.Value != r.Value; return true;
						}
					}
					else if (comp.Left is LdNull && comp.Right is LdNull)
					{
						switch (comp.Kind)
						{
							case ComparisonKind.Equality: value = true; return true;
							case ComparisonKind.Inequality: value = false; return true;
						}
					}
					break;
			}
			value = false;
			return false;
		}
	}
}
