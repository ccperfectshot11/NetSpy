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

using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using NetSpy.Contracts.Decompiler;

namespace NetSpy.Decompiler.Core.MSBuild {
	sealed class XamlTypeProjectFile : TypeProjectFile {
		public XamlTypeProjectFile(TypeDef type, string filename, DecompilationContext decompilationContext, IDecompiler decompiler, Func<TextWriter, IDecompilerOutput> createDecompilerOutput)
			: base(type, filename, decompilationContext, decompiler, createDecompilerOutput) {
		}

		protected override void Decompile(DecompileContext ctx, IDecompilerOutput output) {
			var opts = new DecompilePartialType(output, decompilationContext, Type);
			foreach (var d in GetDefsToRemove())
				opts.Definitions.Add(d);
			opts.InterfacesToRemove.Add(new TypeRefUser(Type.Module, "System.Windows.Markup", "IComponentConnector", new AssemblyNameInfo("WindowsBase, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35").ToAssemblyRef()));
			opts.InterfacesToRemove.Add(new TypeRefUser(Type.Module, "System.Windows.Markup", "IComponentConnector", new AssemblyNameInfo("System.Xaml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089").ToAssemblyRef()));
			opts.InterfacesToRemove.Add(new TypeRefUser(Type.Module, "System.Windows.Markup", "IStyleConnector", new AssemblyNameInfo("PresentationFramework, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35").ToAssemblyRef()));
			opts.InterfacesToRemove.Add(new TypeRefUser(Type.Module, "System.Windows.Markup", "IStyleConnector", new AssemblyNameInfo("PresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35").ToAssemblyRef()));
			decompiler.Decompile(DecompilationType.PartialType, opts);
		}

		IEnumerable<IMemberDef> GetDefsToRemove() => XamlCodeBehind.GetGeneratedMembers(Type);
	}
}
