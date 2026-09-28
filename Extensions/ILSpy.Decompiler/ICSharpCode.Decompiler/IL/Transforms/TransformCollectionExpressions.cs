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

using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.IL.Transforms
{
	/// <summary>
	/// NetSpy: reconstructs C# 12 collection expressions from the code the compiler generates for them.
	/// Only patterns that cannot come from ordinary C# code are transformed; the result is a
	/// <see cref="BlockKind.CollectionExpression"/> block.
	/// </summary>
	public class TransformCollectionExpressions : IStatementTransform
	{
		void IStatementTransform.Run(Block block, int pos, StatementTransformContext context)
		{
			if (!context.Settings.CollectionExpressions)
				return;
			if (TransformKnownLengthList(block, pos, context) || TransformInlineArraySpan(block, pos, context))
			{
				ILInlining.InlineOneIfPossible(block, pos, InliningOptions.Aggressive, context);
				context.RequestRerun();
			}
		}

		/// <summary>
		/// List&lt;T&gt; list = [a, b];
		/// <code>
		/// [stloc num(ldc.i4 N)]
		/// stloc list(newobj List..ctor(ldloc num | ldc.i4 N))
		/// call CollectionsMarshal.SetCount(ldloc list, ldloc num | ldc.i4 N)
		/// [stloc span(call CollectionsMarshal.AsSpan(ldloc list))]
		/// stobj T(call get_Item(ldloca span | addressof Span(call AsSpan(ldloc list)), ldc.i4 k), value_k)   (k = 0..N-1)
		/// </code>
		/// </summary>
		static bool TransformKnownLengthList(Block block, int pos, StatementTransformContext context)
		{
			var instructions = block.Instructions;
			int i = pos;
			int count = -1;
			ILVariable? countVar = null;
			if (instructions[i].MatchStLoc(out var v, out var value) && value.MatchLdcI4(out count)
				&& v.IsSingleDefinition && v.LoadCount == 2 && v.AddressCount == 0)
			{
				countVar = v;
				i++;
			}
			if (i >= instructions.Count || !instructions[i].MatchStLoc(out var listVar, out value))
				return false;
			if (value is not NewObj { Arguments.Count: 1 } newObj || !IsList(newObj.Method.DeclaringType)
				|| !newObj.Method.Parameters[0].Type.IsKnownType(KnownTypeCode.Int32))
				return false;
			if (!MatchCount(newObj.Arguments[0], countVar, ref count) || count <= 0)
				return false;
			if (!listVar.IsSingleDefinition || listVar.Kind is not (VariableKind.Local or VariableKind.StackSlot))
				return false;
			i++;
			if (i >= instructions.Count || instructions[i] is not Call { Arguments.Count: 2 } setCount
				|| !IsCollectionsMarshal(setCount.Method, "SetCount")
				|| !setCount.Arguments[0].MatchLdLoc(listVar) || !MatchCount(setCount.Arguments[1], countVar, ref count))
				return false;
			i++;
			ILVariable? spanVar = null;
			if (i < instructions.Count && instructions[i].MatchStLoc(out v, out value) && IsAsSpan(value, listVar))
			{
				if (v.StoreCount != 1 || v.LoadCount != 0 || v.AddressCount != count)
					return false;
				spanVar = v;
				i++;
			}
			if (i + count > instructions.Count)
				return false;
			var elementStores = new List<StObj>();
			for (int k = 0; k < count; k++)
			{
				if (instructions[i + k] is not StObj stobj || stobj.Target is not Call { Arguments.Count: 2 } getItem
					|| getItem.Method.Name != "get_Item" || !getItem.Method.DeclaringType.IsKnownType(KnownTypeCode.SpanOfT)
					|| !getItem.Arguments[1].MatchLdcI4(k))
					return false;
				var spanAddress = getItem.Arguments[0];
				if (spanVar != null ? !spanAddress.MatchLdLoca(spanVar) : !(spanAddress is AddressOf addr && IsAsSpan(addr.Value, listVar)))
					return false;
				if (UsesVariable(stobj.Value, listVar) || (spanVar != null && UsesVariable(stobj.Value, spanVar)))
					return false;
				elementStores.Add(stobj);
			}
			int end = i + count;

			context.Step("Collection expression (List<T>)", instructions[pos]);
			var listType = newObj.Method.DeclaringType;
			var target = context.Function.RegisterVariable(VariableKind.InitializerTarget, listType);
			instructions.RemoveRange(pos, end - pos);
			var asSpan = setCount.Method.DeclaringTypeDefinition?.GetMethods(m => m.Name == "AsSpan" && m.TypeParameters.Count == 1).FirstOrDefault()
				?.Specialize(new TypeParameterSubstitution(null, new[] { listType.TypeArguments[0] }));
			if (asSpan == null)
				throw new InvalidOperationException("CollectionsMarshal.AsSpan not found");
			var spanType = asSpan.ReturnType;

			var result = new Block(BlockKind.CollectionExpression);
			newObj.Arguments[0] = new LdcI4(count);
			result.Instructions.Add(new StLoc(target, newObj));
			setCount.Arguments[0] = new LdLoc(target);
			setCount.Arguments[1] = new LdcI4(count);
			result.Instructions.Add(setCount);
			foreach (var stobj in elementStores)
			{
				var getItem = (Call)stobj.Target;
				getItem.Arguments[0] = new AddressOf(new Call(asSpan) { Arguments = { new LdLoc(target) } }, spanType);
				result.Instructions.Add(stobj);
			}
			result.FinalInstruction = new LdLoc(target);
			instructions.Insert(pos, new StLoc(listVar, result));
			return true;
		}

		/// <summary>
		/// Span&lt;T&gt; span = [a, b]; Bag bag = [a, b]; (CollectionBuilder)
		/// <code>
		/// stloc buffer(default.value InlineArrayN&lt;T&gt;)
		/// stobj T(ldelema.inlinearray(ldloca buffer, ldc.i4 k), value_k)   (k = 0..N-1)
		/// ... call InlineArrayAsSpan|InlineArrayAsReadOnlySpan(ldloca buffer, ldc.i4 N) ...
		/// </code>
		/// </summary>
		static bool TransformInlineArraySpan(Block block, int pos, StatementTransformContext context)
		{
			var instructions = block.Instructions;
			if (!instructions[pos].MatchStLoc(out var bufferVar, out var value) || value is not DefaultValue defaultValue)
				return false;
			if (!IsCompilerInlineArray(defaultValue.Type) || bufferVar.StoreCount != 1 || bufferVar.LoadCount != 0)
				return false;
			int count = bufferVar.AddressCount - 1;
			if (count <= 0 || pos + count + 1 >= instructions.Count)
				return false;
			var elementStores = new List<StObj>();
			for (int k = 0; k < count; k++)
			{
				if (instructions[pos + 1 + k] is not StObj stobj || stobj.Target is not LdElemaInlineArray { Indices.Count: 1 } ldelema
					|| !ldelema.Array.MatchLdLoca(bufferVar) || !ldelema.Indices[0].MatchLdcI4(k))
					return false;
				elementStores.Add(stobj);
			}
			var next = instructions[pos + count + 1];
			var asSpan = next.Descendants.OfType<Call>().FirstOrDefault(c => c.Arguments.Count == 2
				&& c.Method.Name is "InlineArrayAsSpan" or "InlineArrayAsReadOnlySpan"
				&& c.Arguments[0].MatchLdLoca(bufferVar));
			if (asSpan == null || !asSpan.Arguments[1].MatchLdcI4(count))
				return false;
			ILInstruction resultInst = asSpan;
			if (asSpan.Parent is Call { Arguments.Count: 1 } builder && IsCollectionBuilderMethod(builder.Method))
				resultInst = builder;

			context.Step("Collection expression (inline array)", instructions[pos]);
			var target = context.Function.RegisterVariable(VariableKind.InitializerTarget, defaultValue.Type);
			var resultType = resultInst is Call call ? call.Method.ReturnType : asSpan.Method.ReturnType;
			var slot = context.Function.RegisterVariable(VariableKind.StackSlot, resultType);
			resultInst.ReplaceWith(new LdLoc(slot));
			instructions.RemoveRange(pos, count + 1);

			var result = new Block(BlockKind.CollectionExpression);
			result.Instructions.Add(new StLoc(target, defaultValue));
			foreach (var stobj in elementStores)
			{
				((LdElemaInlineArray)stobj.Target).Array = new LdLoca(target);
				result.Instructions.Add(stobj);
			}
			asSpan.Arguments[0] = new LdLoca(target);
			result.FinalInstruction = resultInst;
			instructions.Insert(pos, new StLoc(slot, result));
			return true;
		}

		static bool MatchCount(ILInstruction inst, ILVariable? countVar, ref int count)
		{
			if (countVar != null)
				return inst.MatchLdLoc(countVar);
			if (count < 0)
				return inst.MatchLdcI4(out count);
			return inst.MatchLdcI4(count);
		}

		static bool IsList(IType type)
		{
			return type is ParameterizedType { TypeArguments.Count: 1 } && type.Namespace == "System.Collections.Generic" && type.Name == "List";
		}

		static bool IsCollectionsMarshal(IMethod method, string name)
		{
			return method.Name == name && method.DeclaringType.FullName == "System.Runtime.InteropServices.CollectionsMarshal";
		}

		static bool IsAsSpan(ILInstruction inst, ILVariable listVar)
		{
			return inst is Call { Arguments.Count: 1 } call && IsCollectionsMarshal(call.Method, "AsSpan")
				&& call.Arguments[0].MatchLdLoc(listVar);
		}

		static bool UsesVariable(ILInstruction inst, ILVariable v)
		{
			return inst.Descendants.OfType<IInstructionWithVariableOperand>().Any(i => i.Variable == v);
		}

		/// <summary>
		/// The buffer types the compiler uses for collection expressions: its own &lt;&gt;y__InlineArrayN&lt;T&gt;
		/// or the System.Runtime.CompilerServices.InlineArrayN&lt;T&gt; of the runtime (.NET 10+).
		/// </summary>
		static bool IsCompilerInlineArray(IType type)
		{
			if (type.Kind != TypeKind.Struct || type.TypeArguments.Count != 1)
				return false;
			if (type.Name.StartsWith("<>y__InlineArray", StringComparison.Ordinal))
				return true;
			return type.Namespace == "System.Runtime.CompilerServices" && type.Name.StartsWith("InlineArray", StringComparison.Ordinal);
		}

		/// <summary>
		/// Is this the static create method of a [CollectionBuilder(typeof(Builder), "Create")] collection type?
		/// </summary>
		internal static bool IsCollectionBuilderMethod(IMethod method)
		{
			if (!method.IsStatic || method.Parameters.Count != 1 || !method.Parameters[0].Type.IsKnownType(KnownTypeCode.ReadOnlySpanOfT))
				return false;
			var collectionType = method.ReturnType.GetDefinition();
			if (collectionType == null)
				return false;
			foreach (var attr in collectionType.GetAttributes())
			{
				if (attr.AttributeType.FullName != "System.Runtime.CompilerServices.CollectionBuilderAttribute" || attr.FixedArguments.Length != 2)
					continue;
				if (attr.FixedArguments[0].Value is IType builderType && attr.FixedArguments[1].Value is string name)
					return name == method.Name && builderType.GetDefinition()?.Equals(method.DeclaringTypeDefinition) == true;
			}
			return false;
		}
	}
}
