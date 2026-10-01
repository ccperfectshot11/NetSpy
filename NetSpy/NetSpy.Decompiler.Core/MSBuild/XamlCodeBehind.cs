/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Collections.Generic;
using dnlib.DotNet;

namespace NetSpy.Decompiler.Core.MSBuild {
	/// <summary>
	/// Finds the members of a XAML code-behind class that the XAML compiler generates
	/// (they must not be in the decompiled .cs file since the build generates them again)
	/// </summary>
	public static class XamlCodeBehind {
		/// <summary>
		/// Gets InitializeComponent(), Connect(), _CreateDelegate(), the fields they use and Main() if it's in <paramref name="type"/>
		/// </summary>
		public static IEnumerable<IMemberDef> GetGeneratedMembers(TypeDef type) {
			var ep = type.Module.EntryPoint;
			if (ep is not null && ep.DeclaringType == type)
				yield return ep;
			var d = FindInitializeComponent(type);
			if (d is not null) {
				yield return d;
				foreach (var f in DotNetUtils.GetFields(d)) {
					if (f.FieldType.RemovePinnedAndModifiers().GetElementType() == ElementType.Boolean)
						yield return f;
				}
			}
			var connMeth = FindConnectMethod(type);
			if (connMeth is not null) {
				yield return connMeth;
				foreach (var f in DotNetUtils.GetFields(connMeth))
					yield return f;
			}
			var delMeth = FindCreateDelegateMethod(type);
			if (delMeth is not null)
				yield return delMeth;
		}

		static MethodDef? FindInitializeComponent(TypeDef type) {
			foreach (var md in type.FindMethods("InitializeComponent")) {
				if (md.IsStatic || md.Parameters.Count != 1)
					continue;
				if (md.ReturnType.RemovePinnedAndModifiers().GetElementType() != ElementType.Void)
					continue;
				return md;
			}
			return null;
		}

		static MethodDef? FindConnectMethod(TypeDef type) {
			foreach (var md in type.Methods) {
				if (IsConnect(md))
					return md;
			}
			return null;
		}

		static bool IsConnect(MethodDef md) {
			if (md is null || md.IsStatic || md.Parameters.Count != 3)
				return false;
			if (md.ReturnType.RemovePinnedAndModifiers().GetElementType() != ElementType.Void)
				return false;

			var sig = md.MethodSig;
			if (sig is null || sig.Params.Count != 2)
				return false;
			if (sig.Params[0].RemovePinnedAndModifiers().GetElementType() != ElementType.I4)
				return false;
			if (sig.Params[1].RemovePinnedAndModifiers().GetElementType() != ElementType.Object)
				return false;

			foreach (var o in md.Overrides) {
				if (o.MethodDeclaration is null || o.MethodDeclaration.DeclaringType is null)
					continue;
				if (o.MethodDeclaration.DeclaringType.FullName != "System.Windows.Markup.IComponentConnector")
					continue;
				if (o.MethodDeclaration.Name != "Connect")
					continue;
				return true;
			}

			return md.Name == "Connect";
		}

		// Finds 'internal Delegate _CreateDelegate(Type delegateType, string handler)' which is
		// called by XamlGeneratedNamespace.GeneratedInternalTypeHelper.CreateDelegate()
		static MethodDef? FindCreateDelegateMethod(TypeDef type) {
			foreach (var m in type.Methods) {
				if (m.Name != "_CreateDelegate")
					continue;
				if (m.IsStatic || !m.IsAssembly)
					continue;
				var sig = m.MethodSig;
				if (sig.GetParamCount() != 2)
					continue;
				if (sig.RetType.RemovePinnedAndModifiers() is null || sig.RetType.RemovePinnedAndModifiers().ToString() != "System.Delegate")
					continue;
				if (sig.Params[0].RemovePinnedAndModifiers() is null || sig.Params[0].RemovePinnedAndModifiers().ToString() != "System.Type")
					continue;
				if (sig.Params[1].RemovePinnedAndModifiers() is null || sig.Params[1].RemovePinnedAndModifiers().GetElementType() != ElementType.String)
					continue;

				return m;
			}
			return null;
		}
	}
}
