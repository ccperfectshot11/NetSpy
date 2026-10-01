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

using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using NetSpy.Decompiler.CSharp;
using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.CSharp.Transforms;
using NetSpy.Decompiler.TypeSystem;
using IMethod = NetSpy.Decompiler.TypeSystem.IMethod;
using IType = NetSpy.Decompiler.TypeSystem.IType;
using IField = NetSpy.Decompiler.TypeSystem.IField;

namespace NetSpy.Decompiler.Engine.Core.CSharp {
	/// <summary>
	/// Base class of NetSpy's AST transforms: gives access to the dnlib definition of a declaration
	/// </summary>
	abstract class DnlibAstTransform : IAstTransform {
		protected readonly SymbolConverter Converter;

		protected DnlibAstTransform(ModuleDef module) => Converter = new SymbolConverter(module);

		protected object? GetDef(AstNode node) => Converter.GetReference(node.GetSymbol());

		public abstract void Run(AstNode rootNode, TransformContext context);
	}

	/// <summary>
	/// Decompiling a constructor: show it together with the field initializers (like NetSpy)
	/// </summary>
	sealed class SelectCtorTransform : DnlibAstTransform {
		readonly MethodDef ctor;

		public SelectCtorTransform(MethodDef ctor) : base(ctor.Module) => this.ctor = ctor;

		public override void Run(AstNode rootNode, TransformContext context) {
			ConstructorDeclaration? ctorDecl = null;
			foreach (var node in rootNode.Children.ToArray()) {
				switch (node) {
				case ConstructorDeclaration cd:
					if (GetDef(cd) == ctor)
						ctorDecl = cd;
					else
						cd.Remove();
					break;
				case FieldDeclaration fd:
					if (fd.Variables.All(v => v.Initializer is null))
						fd.Remove();
					break;
				case EventDeclaration ed:
					if (ed.Variables.All(v => v.Initializer is null))
						ed.Remove();
					break;
				case PropertyDeclaration pd:
					if (pd.Initializer is null)
						pd.Remove();
					break;
				case CustomEventDeclaration:
				case IndexerDeclaration:
					node.Remove();
					break;
				}
			}
			// 'this(...)' ctors don't run the field initializers
			if (ctorDecl?.Initializer?.ConstructorInitializerType == ConstructorInitializerType.This) {
				foreach (var node in rootNode.Children.ToArray()) {
					if (node is EntityDeclaration && node is not ConstructorDeclaration)
						node.Remove();
				}
			}
		}
	}

	/// <summary>
	/// Decompiling a field: only keep the field (the ctors were decompiled to find its initializer)
	/// </summary>
	sealed class SelectFieldTransform : DnlibAstTransform {
		readonly FieldDef field;

		public SelectFieldTransform(FieldDef field) : base(field.Module) => this.field = field;

		public override void Run(AstNode rootNode, TransformContext context) {
			foreach (var node in rootNode.Children.ToArray()) {
				if (node is EntityDeclaration && GetDef(node) != field)
					node.Remove();
			}
		}
	}

	/// <summary>
	/// Edit Class: the members that aren't edited are shown in a separate partial type
	/// </summary>
	sealed class DecompilePartialTransform : DnlibAstTransform {
		readonly TypeDef type;
		readonly HashSet<IMemberDef> definitions;
		readonly bool showDefinitions;
		readonly bool addPartialKeyword;
		readonly HashSet<ITypeDefOrRef> ifacesToRemove;

		public DecompilePartialTransform(TypeDef type, HashSet<IMemberDef> definitions, bool showDefinitions, bool addPartialKeyword, IEnumerable<ITypeDefOrRef> ifacesToRemove)
			: base(type.Module) {
			this.type = type;
			this.definitions = definitions;
			this.showDefinitions = showDefinitions;
			this.addPartialKeyword = addPartialKeyword;
			this.ifacesToRemove = new HashSet<ITypeDefOrRef>(ifacesToRemove, TypeEqualityComparer.Instance);
		}

		public override void Run(AstNode rootNode, TransformContext context) {
			foreach (var en in rootNode.Descendants.OfType<EntityDeclaration>().ToArray()) {
				if (GetDef(en) is not IMemberDef def)
					continue;
				if (def == type) {
					if (en is TypeDeclaration tdecl) {
						if (addPartialKeyword && tdecl.ClassType != ClassType.Enum)
							tdecl.Modifiers |= Modifiers.Partial;
						foreach (var baseType in tdecl.BaseTypes.ToArray()) {
							if (Converter.GetReference(baseType.GetSymbol()) is ITypeDefOrRef tdr && ifacesToRemove.Contains(tdr))
								baseType.Remove();
						}
					}
				}
				else if (showDefinitions != definitions.Contains(def))
					en.Remove();
			}
		}
	}

	/// <summary>
	/// Edit Method: only the edited methods are shown with bodies; the other members are either
	/// removed or (the 'hidden' part) get bodies that compile but aren't used.
	/// </summary>
	sealed class DecompileTypeMethodsTransform : DnlibAstTransform {
		readonly HashSet<IMemberDef> defsToShow = new HashSet<IMemberDef>();
		readonly HashSet<TypeDef> partialTypes = new HashSet<TypeDef>();
		readonly bool showDefinitions;
		readonly bool showAll;

		public DecompileTypeMethodsTransform(HashSet<TypeDef> types, HashSet<MethodDef> methods, bool showDefinitions, bool showAll)
			: base((types.FirstOrDefault() ?? methods.First().DeclaringType).Module) {
			this.showDefinitions = showDefinitions;
			this.showAll = showAll;
			foreach (var method in methods) {
				// If it's part of a property or event, include the property or event since there are no partial props/events
				var prop = method.DeclaringType.Properties.FirstOrDefault(a => a.GetMethods.Contains(method) || a.SetMethods.Contains(method));
				if (prop is not null) {
					defsToShow.Add(prop);
					foreach (var m in prop.GetMethods.Concat(prop.SetMethods).Concat(prop.OtherMethods))
						defsToShow.Add(m);
				}
				else {
					var evt = method.DeclaringType.Events.FirstOrDefault(a => a.AddMethod == method || a.RemoveMethod == method);
					if (evt is not null) {
						defsToShow.Add(evt);
						foreach (var m in new[] { evt.AddMethod, evt.RemoveMethod, evt.InvokeMethod }.Concat(evt.OtherMethods)) {
							if (m is not null)
								defsToShow.Add(m);
						}
					}
					else
						defsToShow.Add(method);
				}
			}
			foreach (var type in types) {
				if (!type.IsEnum) {
					defsToShow.Add(type);
					partialTypes.Add(type);
				}
			}
			foreach (var def in defsToShow.ToArray()) {
				for (var declType = def.DeclaringType; declType is not null; declType = declType.DeclaringType)
					partialTypes.Add(declType);
			}
			foreach (var type in types) {
				if (type.IsEnum) {
					defsToShow.Add(type);
					foreach (var f in type.Fields)
						defsToShow.Add(f);
				}
			}
		}

		public override void Run(AstNode rootNode, TransformContext context) {
			foreach (var en in rootNode.Descendants.OfType<EntityDeclaration>().ToArray()) {
				if (GetDef(en) is not IMemberDef def)
					continue;
				if (def is TypeDef td && partialTypes.Contains(td)) {
					if (en is TypeDeclaration tdecl) {
						if (tdecl.ClassType != ClassType.Enum)
							tdecl.Modifiers |= Modifiers.Partial;
						if (!showDefinitions) {
							tdecl.BaseTypes.Clear();
							tdecl.Attributes.Clear();
						}
					}
				}
				else if (showDefinitions) {
					if (!showAll && !defsToShow.Contains(def))
						en.Remove();
				}
				else {
					if (showAll || defsToShow.Contains(def))
						en.Remove();
					else
						ReplaceBodies(en);
				}
			}
		}

		/// <summary>
		/// Hidden members only need to compile: replace their code with 'throw null;'
		/// </summary>
		static void ReplaceBodies(EntityDeclaration en) {
			switch (en) {
			case MethodDeclaration md when md.Body is not null:
				md.Body = CreateThrowBody();
				break;
			case OperatorDeclaration od when od.Body is not null:
				od.Body = CreateThrowBody();
				break;
			case DestructorDeclaration dd when dd.Body is not null:
				dd.Body = new BlockStatement();
				break;
			case ConstructorDeclaration cd when cd.Body is not null:
				cd.Body = new BlockStatement();
				break;
			case PropertyDeclaration pd:
				if (pd.ExpressionBody is not null) {
					pd.ExpressionBody = null;
					pd.Getter = new Accessor { Body = CreateThrowBody() };
				}
				ReplaceAccessorBody(pd.Getter);
				ReplaceAccessorBody(pd.Setter);
				break;
			case IndexerDeclaration idx:
				if (idx.ExpressionBody is not null) {
					idx.ExpressionBody = null;
					idx.Getter = new Accessor { Body = CreateThrowBody() };
				}
				ReplaceAccessorBody(idx.Getter);
				ReplaceAccessorBody(idx.Setter);
				break;
			case CustomEventDeclaration ced:
				if (ced.AddAccessor?.Body is not null)
					ced.AddAccessor.Body = new BlockStatement();
				if (ced.RemoveAccessor?.Body is not null)
					ced.RemoveAccessor.Body = new BlockStatement();
				break;
			}
		}

		static void ReplaceAccessorBody(Accessor? accessor) {
			if (accessor?.Body is not null)
				accessor.Body = CreateThrowBody();
		}

		static BlockStatement CreateThrowBody() {
			var block = new BlockStatement();
			block.Statements.Add(new ThrowStatement { Expression = new NullReferenceExpression() });
			return block;
		}
	}

	/// <summary>
	/// Edit Assembly Attributes: remove the attributes the compiler adds itself
	/// </summary>
	sealed class AssemblyInfoTransform : IAstTransform {
		static readonly HashSet<string> compilerAttributes = new HashSet<string> {
			"System.Runtime.Versioning.TargetFrameworkAttribute",
			"System.Security.UnverifiableCodeAttribute",
			"System.Runtime.CompilerServices.CompilationRelaxationsAttribute",
			"System.Runtime.CompilerServices.RuntimeCompatibilityAttribute",
			"System.Diagnostics.DebuggableAttribute",
			"System.Security.Permissions.SecurityPermissionAttribute",
		};

		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var section in rootNode.Descendants.OfType<AttributeSection>().ToArray()) {
				var attr = section.Attributes.FirstOrDefault();
				if (attr?.GetSymbol() is IMethod ctor && compilerAttributes.Contains(ctor.DeclaringType.FullName))
					section.Remove();
			}
		}
	}
}
