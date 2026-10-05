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
using System.Reflection.Metadata.Ecma335;

using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.Disassembler;
using NetSpy.Decompiler.Metadata;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: puts the members of each type in their source order. The source line of a member comes from the PDB:
	/// methods and accessors from their sequence points, constructors from the sequence point of their base/this call
	/// (the ones before it are field initializers), fields with an initializer from the constructor's sequence point that
	/// stores them, nested types from their members. Members without a line keep their neighbours: a field goes right
	/// before the next member with a line, anything else right after the previous one.
	/// </summary>
	public sealed class OrderMembersBySource : IAstTransform
	{
		TransformContext context = null!;
		DebugInfo.IDebugInfoProvider provider = null!;
		readonly Dictionary<IField, (string Document, int Line)> fieldLines = new Dictionary<IField, (string, int)>();

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.SourceLayoutFromPdb || !context.Settings.SourceMemberOrder || context.DecompileRun.DebugInfoProvider is not { } provider)
				return;
			this.context = context;
			this.provider = provider;
			// innermost types first: a nested type's line comes from its (already ordered) members
			foreach (var type in rootNode.Descendants.OfType<TypeDeclaration>().Reverse().ToList())
				Order(type);
		}

		void Order(TypeDeclaration type)
		{
			if (type.GetSymbol() is not ITypeDefinition definition || definition.Kind is TypeKind.Enum || definition.IsRecord)
				return;
			CollectFieldLines(definition);
			var members = type.Members.ToList();
			if (members.Count < 2)
				return;
			var lines = members.Select(m => SourceLine(m)).ToList();
			var documents = lines.Where(l => l is not null).Select(l => l!.Value.Document).Distinct().ToList();
			if (documents.Count != 1 || lines.Count(l => l is not null) < 1)
				return; // partial types spread over files keep the decompiler's order
			// stable sort of the known members by line, the others anchored to their neighbours
			var known = members.Select((m, i) => (Member: m, Index: i, Line: lines[i]?.Line ?? -1)).Where(x => x.Line >= 0)
				.OrderBy(x => x.Line).ThenBy(x => x.Index).Select(x => x.Member).ToList();
			var result = new List<EntityDeclaration>(known);
			for (int i = 0; i < members.Count; i++)
			{
				if (lines[i] is not null)
					continue;
				var member = members[i];
				if (member is FieldDeclaration or EventDeclaration or PropertyDeclaration)
				{
					// before the next member (in the decompiler's order) that has a line
					var next = members.Skip(i + 1).Where((m, k) => lines[i + 1 + k] is not null).FirstOrDefault();
					// (earlier unknown fields were inserted at the same place, so they stay in front of this one)
					int at = next is null ? result.Count : result.IndexOf(next);
					result.Insert(at, member);
				}
				else if (member is TypeDeclaration)
				{
					// a nested type without code (constants only): where nested types usually are, at the end
					result.Add(member);
				}
				else
				{
					// after the previous member (in the decompiler's order), or first
					var previous = i > 0 ? members[i - 1] : null;
					int at = previous is null ? 0 : result.IndexOf(previous) + 1;
					result.Insert(at, member);
				}
			}
			if (result.SequenceEqual(members))
				return;
			context.Step("Order members by source", type);
			foreach (var member in members)
				member.Remove();
			type.Members.AddRange(result);
		}

		(string Document, int Line)? SourceLine(EntityDeclaration member)
		{
			switch (member)
			{
				case TypeDeclaration nested:
					return nested.Members.Select(SourceLine).Where(l => l is not null).OrderBy(l => l!.Value.Line).FirstOrDefault();
				case FieldDeclaration field:
					return field.Variables.Select(v => v.GetSymbol() as IField).Append(field.GetSymbol() as IField)
						.Where(f => f is not null && fieldLines.ContainsKey(f))
						.Select(f => ((string, int)?)fieldLines[f!]).OrderBy(l => l!.Value.Item2).FirstOrDefault();
				case PropertyDeclaration or IndexerDeclaration or EventDeclaration or CustomEventDeclaration:
				{
					var symbol = member.GetSymbol();
					var accessors = symbol switch {
						IProperty p => new[] { p.Getter, p.Setter },
						IEvent e => new[] { e.AddAccessor, e.RemoveAccessor },
						_ => Array.Empty<IMethod?>()
					};
					var line = accessors.OfType<IMethod>().Select(MethodLine).Where(l => l is not null).OrderBy(l => l!.Value.Line).FirstOrDefault();
					// an auto-property with an initializer: the line its backing field is stored on
					if (line is null && symbol is IProperty property)
					{
						var backing = property.DeclaringTypeDefinition?.Fields.FirstOrDefault(f => f.Name == "<" + property.Name + ">k__BackingField");
						if (backing is not null && fieldLines.TryGetValue(backing, out var stored))
							line = stored;
					}
					return line;
				}
				case ConstructorDeclaration:
					return member.GetSymbol() is IMethod constructor ? ConstructorLine(constructor) : null;
				default:
					return member.GetSymbol() is IMethod method ? MethodLine(method) : null;
			}
		}

		List<DebugInfo.SequencePoint>? Points(IMethod method)
		{
			if (method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition)
				return null;
			try
			{
				return provider.GetSequencePoints((MethodDefinitionHandle)method.MetadataToken).Where(sp => !sp.IsHidden).ToList();
			}
			catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
			{
				return null;
			}
		}

		(string Document, int Line)? MethodLine(IMethod method)
		{
			var points = Points(method);
			if (points is null || points.Count == 0)
				return null;
			var first = points.OrderBy(sp => sp.StartLine).First();
			return (first.DocumentUrl, first.StartLine);
		}

		/// <summary>The line of the base/this constructor call (the constructor's declaration); null if it has none.</summary>
		(string Document, int Line)? ConstructorLine(IMethod constructor)
		{
			var points = Points(constructor);
			int callOffset = BaseCallOffset(constructor);
			if (points is null || callOffset < 0)
				return MethodLine(constructor);
			var point = points.Where(sp => sp.Offset <= callOffset).OrderBy(sp => sp.Offset).LastOrDefault();
			return point is null ? null : (point.DocumentUrl, point.StartLine);
		}

		/// <summary>Fields stored before the base constructor call (field initializers) and the lines they are on.</summary>
		void CollectFieldLines(ITypeDefinition type)
		{
			// a struct constructor has no base call: its field initializers can't be told from its body's assignments
			if (type.IsReferenceType != true)
				return;
			foreach (var constructor in type.Methods.Where(m => m.IsConstructor))
			{
				var points = Points(constructor);
				if (points is null || points.Count == 0)
					continue;
				int callOffset = BaseCallOffset(constructor);
				foreach (var (offset, token) in FieldStores(constructor))
				{
					if (callOffset >= 0 && offset > callOffset && !constructor.IsStatic)
						continue;
					var point = points.Where(sp => sp.Offset <= offset).OrderBy(sp => sp.Offset).LastOrDefault();
					if (point is null || context.TypeSystem.MainModule.ResolveEntity(token) is not IField field || field.DeclaringTypeDefinition?.Equals(type) != true)
						continue;
					if (!fieldLines.ContainsKey(field))
						fieldLines[field] = (point.DocumentUrl, point.StartLine);
				}
			}
		}

		/// <summary>The IL offset of the call to the base (or this) constructor; -1 if none.</summary>
		int BaseCallOffset(IMethod constructor)
		{
			if (constructor.IsStatic)
				return -1;
			foreach (var (offset, opCode, token) in Instructions(constructor))
			{
				if (opCode == ILOpCode.Call && token.Kind is HandleKind.MethodDefinition or HandleKind.MemberReference
					&& context.TypeSystem.MainModule.ResolveEntity(token) is IMethod { IsConstructor: true } called
					&& (called.DeclaringTypeDefinition?.Equals(constructor.DeclaringTypeDefinition) == true
						|| constructor.DeclaringTypeDefinition?.DirectBaseTypes.Any(b => b.GetDefinition()?.Equals(called.DeclaringTypeDefinition) == true) == true))
					return offset;
			}
			return -1;
		}

		IEnumerable<(int Offset, EntityHandle Token)> FieldStores(IMethod method)
		{
			foreach (var (offset, opCode, token) in Instructions(method))
			{
				if (opCode is ILOpCode.Stfld or ILOpCode.Stsfld && token.Kind == HandleKind.FieldDefinition)
					yield return (offset, token);
			}
		}

		IEnumerable<(int Offset, ILOpCode OpCode, EntityHandle Token)> Instructions(IMethod method)
		{
			if (method.ParentModule is not MetadataModule module || method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition)
				yield break;
			var definition = module.MetadataFile.Metadata.GetMethodDefinition((MethodDefinitionHandle)method.MetadataToken);
			if (definition.RelativeVirtualAddress == 0)
				yield break;
			var reader = module.MetadataFile.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
			while (reader.RemainingBytes > 0)
			{
				int offset = reader.Offset;
				var opCode = reader.DecodeOpCode();
				EntityHandle token = default;
				if (opCode is ILOpCode.Call or ILOpCode.Stfld or ILOpCode.Stsfld)
					token = MetadataTokens.EntityHandle(reader.ReadInt32());
				else
					reader.SkipOperand(opCode);
				yield return (offset, opCode, token);
			}
		}
	}
}
