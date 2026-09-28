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

using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;
using IMethod = ICSharpCode.Decompiler.TypeSystem.IMethod;
using IType = ICSharpCode.Decompiler.TypeSystem.IType;
using IField = ICSharpCode.Decompiler.TypeSystem.IField;

namespace dnSpy.Decompiler.ILSpy.Core.CSharp {
	/// <summary>
	/// Converts modern ILSpy symbols to the dnlib objects dnSpy uses for references
	/// (go to definition, tooltips, highlighting) and to dnlib type signatures.
	/// </summary>
	sealed class SymbolConverter {
		readonly ModuleDef contextModule;

		public SymbolConverter(ModuleDef contextModule) => this.contextModule = contextModule;

		public ModuleDef ContextModule => contextModule;

		/// <summary>
		/// Returns the dnlib object (TypeDef, MethodDef, FieldDef, PropertyDef, EventDef, GenericParam,
		/// NamespaceReference) for an ILSpy symbol, or null if it can't be found
		/// </summary>
		public object? GetReference(object? symbol) {
			switch (symbol) {
			case null:
				return null;
			case ITypeParameter tp:
				return GetGenericParam(tp);
			case IEntity entity:
				return GetDefinition(entity);
			case INamespace ns:
				return GetNamespaceReference(ns);
			case IType type:
				return GetTypeReference(type);
			}
			return null;
		}

		object? GetDefinition(IEntity entity) {
			// Specialized members (eg. List<int>.Add) point to the generic definition
			var def = entity is IMember member ? member.MemberDefinition : entity;
			if (def.MetadataToken.IsNil)
				return null;
			var module = DnlibMetadata.TryGetModule(def.ParentModule?.MetadataFile);
			if (module is null)
				return null;
			return DnlibMetadata.ResolveToken(module, def.MetadataToken);
		}

		object? GetTypeReference(IType type) {
			switch (type) {
			case ParameterizedType pt:
				return GetReference(pt.GenericType);
			case TupleType tt:
				return GetReference(tt.UnderlyingType);
			case TypeWithElementType te:
				return GetReference(te.ElementType);
			}
			var def = type.GetDefinition();
			if (def is not null)
				return GetDefinition(def);
			return null;
		}

		GenericParam? GetGenericParam(ITypeParameter tp) {
			var owner = tp.Owner is null ? null : GetDefinition(tp.Owner);
			switch (owner) {
			case TypeDef td:
				return td.GenericParameters.FirstOrDefault(a => a.Number == tp.Index);
			case MethodDef md:
				return md.GenericParameters.FirstOrDefault(a => a.Number == tp.Index);
			}
			return null;
		}

		NamespaceReference GetNamespaceReference(INamespace ns) {
			IAssembly? asm = null;
			foreach (var m in ns.ContributingModules) {
				var module = DnlibMetadata.TryGetModule(m.MetadataFile);
				if (module?.Assembly is not null) {
					asm = module.Assembly;
					break;
				}
			}
			return new NamespaceReference(asm ?? contextModule.Assembly, ns.FullName);
		}

		/// <summary>
		/// Converts an ILSpy type to a dnlib type signature (used for locals / parameters in debug info)
		/// </summary>
		public TypeSig ToTypeSig(IType? type) {
			var corlib = contextModule.CorLibTypes;
			switch (type) {
			case null:
				return corlib.Object;
			case TupleType tt:
				return ToTypeSig(tt.UnderlyingType);
			case ParameterizedType pt: {
				var genericType = ToTypeSig(pt.GenericType) as ClassOrValueTypeSig;
				if (genericType is null)
					return corlib.Object;
				return new GenericInstSig(genericType, pt.TypeArguments.Select(ToTypeSig).ToList());
			}
			case ArrayType at:
				if (at.Dimensions == 1)
					return new SZArraySig(ToTypeSig(at.ElementType));
				return new ArraySig(ToTypeSig(at.ElementType), (uint)at.Dimensions);
			case PointerType ptr:
				return new PtrSig(ToTypeSig(ptr.ElementType));
			case ByReferenceType br:
				return new ByRefSig(ToTypeSig(br.ElementType));
			case ITypeParameter tp:
				if (tp.OwnerType == SymbolKind.Method)
					return new GenericMVar((uint)tp.Index);
				return new GenericVar((uint)tp.Index);
			}

			var def = type.GetDefinition();
			if (def is null)
				return type.Kind == TypeKind.Pointer || type.Kind == TypeKind.FunctionPointer ? corlib.IntPtr : corlib.Object;

			switch (def.KnownTypeCode) {
			case KnownTypeCode.Void: return corlib.Void;
			case KnownTypeCode.Boolean: return corlib.Boolean;
			case KnownTypeCode.Char: return corlib.Char;
			case KnownTypeCode.SByte: return corlib.SByte;
			case KnownTypeCode.Byte: return corlib.Byte;
			case KnownTypeCode.Int16: return corlib.Int16;
			case KnownTypeCode.UInt16: return corlib.UInt16;
			case KnownTypeCode.Int32: return corlib.Int32;
			case KnownTypeCode.UInt32: return corlib.UInt32;
			case KnownTypeCode.Int64: return corlib.Int64;
			case KnownTypeCode.UInt64: return corlib.UInt64;
			case KnownTypeCode.Single: return corlib.Single;
			case KnownTypeCode.Double: return corlib.Double;
			case KnownTypeCode.String: return corlib.String;
			case KnownTypeCode.Object: return corlib.Object;
			case KnownTypeCode.IntPtr: return corlib.IntPtr;
			case KnownTypeCode.UIntPtr: return corlib.UIntPtr;
			case KnownTypeCode.TypedReference: return corlib.TypedReference;
			}

			if (GetDefinition(def) is TypeDef td) {
				ITypeDefOrRef tdr = td.Module == contextModule ? td : new Importer(contextModule).Import(td);
				return tdr.ToTypeSig();
			}
			// Unresolved type (missing reference): create a reference by name
			var typeRef = new TypeRefUser(contextModule, def.Namespace, def.MetadataName);
			return def.IsReferenceType == false ? new ValueTypeSig(typeRef) : new ClassSig(typeRef);
		}
	}
}
