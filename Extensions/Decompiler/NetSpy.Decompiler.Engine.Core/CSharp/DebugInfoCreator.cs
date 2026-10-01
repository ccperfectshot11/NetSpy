/*
    Copyright (C) 2026 NetSpy contributors

    This file is part of NetSpy (a dnSpy fork)

    NetSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    NetSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with NetSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NetSpy.Contracts.Decompiler;
using NetSpy.Decompiler.CSharp;
using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.IL;
using NetSpy.Decompiler.TypeSystem;
using IMethod = NetSpy.Decompiler.TypeSystem.IMethod;
using IType = NetSpy.Decompiler.TypeSystem.IType;
using IField = NetSpy.Decompiler.TypeSystem.IField;
using ILVariable = NetSpy.Decompiler.IL.ILVariable;
using VariableKind = NetSpy.Decompiler.IL.VariableKind;

namespace NetSpy.Decompiler.Engine.Core.CSharp {
	/// <summary>
	/// Maps NetSpy variables to NetSpy's <see cref="SourceLocal"/> / <see cref="SourceParameter"/>.
	/// The same object is used as the text reference (highlighting, tooltips) and in the
	/// debug info (so the debugger can show the value of the variable under the mouse).
	/// </summary>
	sealed class VariableMapper {
		readonly SymbolConverter converter;
		readonly Dictionary<ILVariable, ISourceVariable> map = new Dictionary<ILVariable, ISourceVariable>();

		public VariableMapper(SymbolConverter converter) => this.converter = converter;

		public IEnumerable<KeyValuePair<ILVariable, ISourceVariable>> All => map;

		public ISourceVariable Get(ILVariable variable) {
			if (!map.TryGetValue(variable, out var sv)) {
				sv = Create(variable);
				map.Add(variable, sv);
			}
			return sv;
		}

		MethodDef? GetMethodDef(IMethod? method) => converter.GetReference(method) as MethodDef;

		ISourceVariable Create(ILVariable v) {
			string name = v.Name ?? "V_" + v.IndexInFunction.ToString();
			var flags = v.IsRefReadOnly ? SourceVariableFlags.ReadOnlyReference : SourceVariableFlags.None;
			var hoistedField = v.StateMachineField is null ? null : converter.GetReference(v.StateMachineField) as FieldDef;
			var function = v.Function;

			if (v.Kind == VariableKind.Parameter && v.Index is int paramIndex) {
				var md = GetMethodDef(function?.Method);
				var param = md is null ? null : GetParameter(md, paramIndex);
				if (param is not null) {
					if (hoistedField is not null)
						return new SourceParameter(param, name, hoistedField, flags);
					return new SourceParameter(param, name, param.Type ?? converter.ToTypeSig(v.Type), flags);
				}
			}

			if (hoistedField is not null)
				return new SourceLocal(null, name, hoistedField, flags);

			if (IsRealLocal(v) && v.Index is int localIndex) {
				var md = GetMethodDef(function?.MoveNextMethod ?? function?.Method);
				var locals = md?.Body?.Variables;
				if (locals is not null && (uint)localIndex < (uint)locals.Count) {
					var local = locals[localIndex];
					return new SourceLocal(local, name, local.Type ?? converter.ToTypeSig(v.Type), flags);
				}
			}

			// Decompiler generated variable (eg. a stack slot turned into a local)
			return new SourceLocal(null, name, converter.ToTypeSig(v.Type), flags);
		}

		static Parameter? GetParameter(MethodDef md, int index) {
			// NetSpy: 'this' has index -1, the declared parameters start at 0
			if (index < 0)
				return md.Parameters.FirstOrDefault(a => a.IsHiddenThisParameter);
			return md.Parameters.FirstOrDefault(a => a.MethodSigIndex == index);
		}

		public static bool IsRealLocal(ILVariable v) {
			switch (v.Kind) {
			case VariableKind.Local:
			case VariableKind.PinnedLocal:
			case VariableKind.PinnedRegionLocal:
			case VariableKind.UsingLocal:
			case VariableKind.ForeachLocal:
			case VariableKind.PatternLocal:
			case VariableKind.DisplayClassLocal:
				return v.Index is not null;
			default:
				return false;
			}
		}
	}

	/// <summary>
	/// Creates NetSpy's <see cref="MethodDebugInfo"/> (IL offsets &lt;-&gt; text spans, locals, async info)
	/// from the sequence points NetSpy computes for the written syntax tree. Needed by the debugger
	/// (breakpoints, stepping, current statement, locals) and by "IL with C#"-like features.
	/// </summary>
	static class DebugInfoCreator {
		public static void AddDebugInfo(IDecompilerOutput output, NetSpy.Decompiler.CSharp.CSharpDecompiler decompiler, SyntaxTree syntaxTree,
			SymbolConverter converter, VariableMapper variables, int settingsVersion, IReadOnlyList<string> usingNamespaces) {
			Dictionary<ILFunction, List<NetSpy.Decompiler.DebugInfo.SequencePoint>> sequencePoints;
			try {
				sequencePoints = decompiler.CreateSequencePoints(syntaxTree);
			}
			catch (Exception) {
				// Debug info is optional; never fail the decompilation because of it
				return;
			}

			var functionSpans = GetFunctionSpans(syntaxTree);
			foreach (var kv in sequencePoints) {
				var function = kv.Key;
				if (function.Method is null || function.Method.MetadataToken.IsNil)
					continue;
				var kickoff = converter.GetReference(function.Method) as MethodDef;
				var moveNext = function.MoveNextMethod is null ? null : converter.GetReference(function.MoveNextMethod) as MethodDef;
				var method = moveNext ?? kickoff;
				if (method?.Body is null)
					continue;
				if (method == kickoff)
					kickoff = null;

				var stateMachineKind = StateMachineKind.None;
				if (moveNext is not null)
					stateMachineKind = function.IsAsync ? StateMachineKind.AsyncMethod : StateMachineKind.IteratorMethod;

				var builder = new MethodDebugInfoBuilder(settingsVersion, stateMachineKind, method, kickoff);
				builder.CompilerName = function.StateMachineCompiledWithMono ? PredefinedCompilerNames.MonoCSharp : PredefinedCompilerNames.MicrosoftCSharp;

				foreach (var sp in kv.Value) {
					if (sp.IsHidden || sp.EndOffset <= sp.Offset)
						continue;
					// Our token writer encodes absolute text positions as (line 1, column = position + 1)
					int start = sp.StartColumn - 1;
					int end = sp.EndColumn - 1;
					if (sp.StartLine != 1 || sp.EndLine != 1 || start < 0 || end < start)
						continue;
					builder.Add(new SourceStatement(ILSpan.FromBounds((uint)sp.Offset, (uint)sp.EndOffset), TextSpan.FromBounds(start, end)));
				}

				if (functionSpans.TryGetValue(function, out var span)) {
					builder.StartPosition = span.start;
					builder.EndPosition = span.end;
				}

				AddVariables(builder, function, variables);
				for (int i = 0; i < usingNamespaces.Count; i++)
					builder.Scope.Imports.Add(ImportInfo.CreateNamespace(usingNamespaces[i]));

				if (stateMachineKind == StateMachineKind.AsyncMethod && moveNext is not null)
					builder.AsyncInfo = CreateAsyncInfo(function, moveNext);

				output.AddDebugInfo(builder.Create());
			}
		}

		static void AddVariables(MethodDebugInfoBuilder builder, ILFunction function, VariableMapper variables) {
			var parameters = new List<SourceParameter>();
			foreach (var v in function.Variables) {
				var sv = variables.Get(v);
				if (sv is SourceParameter sp)
					parameters.Add(sp);
				else if (sv is SourceLocal sl && (sl.Local is not null || sl.HoistedField is not null))
					builder.Scope.Locals.Add(sl);
			}
			if (parameters.Count != 0)
				builder.Parameters = parameters.ToArray();
		}

		static AsyncMethodDebugInfo? CreateAsyncInfo(ILFunction function, MethodDef moveNext) {
			var info = function.AsyncDebugInfo;
			if (info.Awaits.IsDefault)
				return null;
			var stepInfos = info.Awaits.Select(a => new AsyncStepInfo((uint)a.YieldOffset, moveNext, (uint)a.ResumeOffset)).ToArray();
			FieldDef? builderField = null;
			foreach (var f in moveNext.DeclaringType.Fields) {
				if (f.Name.EndsWith("__builder", StringComparison.Ordinal)) {
					builderField = f;
					break;
				}
			}
			uint setResultOffset = uint.MaxValue;
			foreach (var instr in moveNext.Body.Instructions) {
				if ((instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt) && instr.Operand is IMethodDefOrRef m && m.Name == "SetResult") {
					setResultOffset = instr.Offset;
					break;
				}
			}
			uint catchHandlerOffset = info.CatchHandlerOffset < 0 ? uint.MaxValue : (uint)info.CatchHandlerOffset;
			return new AsyncMethodDebugInfo(stepInfos, builderField, catchHandlerOffset, setResultOffset);
		}

		/// <summary>
		/// Text span of the declaration of each function (method, accessor, lambda, local function)
		/// </summary>
		static Dictionary<ILFunction, (int start, int end)> GetFunctionSpans(SyntaxTree syntaxTree) {
			var dict = new Dictionary<ILFunction, (int start, int end)>();
			foreach (var node in syntaxTree.Descendants) {
				var function = node.Annotation<ILFunction>();
				if (function is null || dict.ContainsKey(function))
					continue;
				var start = node.StartLocation;
				var end = node.EndLocation;
				if (start.IsEmpty || end.IsEmpty || start.Line != 1 || end.Line != 1)
					continue;
				dict[function] = (start.Column - 1, end.Column - 1);
			}
			return dict;
		}
	}
}
