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

using System.ComponentModel.Composition;
using NetSpy.Contracts.Text;
using NetSpy.Decompiler.Engine.Core.Text;
using Microsoft.VisualStudio.Utilities;

namespace NetSpy.Decompiler.Engine {
	static class ContentTypeDefinitions {
#pragma warning disable CS0169
		[Export]
		[Name(ContentTypesInternal.DecompilerEngine)]
		[BaseDefinition(ContentTypes.DecompiledCode)]
		static readonly ContentTypeDefinition? DecompilerEngineContentTypeDefinition;

		[Export]
		[Name(ContentTypesInternal.CSharpEngine)]
		[BaseDefinition(ContentTypesInternal.DecompilerEngine)]
		[BaseDefinition(ContentTypes.CSharp)]
		static readonly ContentTypeDefinition? CSharpEngineContentTypeDefinition;

		[Export]
		[Name(ContentTypesInternal.VisualBasicEngine)]
		[BaseDefinition(ContentTypesInternal.DecompilerEngine)]
		[BaseDefinition(ContentTypes.VisualBasic)]
		static readonly ContentTypeDefinition? VisualBasicEngineContentTypeDefinition;

		[Export]
		[Name(ContentTypesInternal.ILEngine)]
		[BaseDefinition(ContentTypesInternal.DecompilerEngine)]
		[BaseDefinition(ContentTypes.IL)]
		static readonly ContentTypeDefinition? ILEngineContentTypeDefinition;

		[Export]
		[Name(ContentTypesInternal.ILAstEngine)]
		[BaseDefinition(ContentTypesInternal.DecompilerEngine)]
		static readonly ContentTypeDefinition? ILAstEngineContentTypeDefinition;

		[Export]
		[Name(ContentTypesInternal.ILWithCSharpEngine)]
		[BaseDefinition(ContentTypesInternal.ILEngine)]
		static readonly ContentTypeDefinition? ILWithCSharpEngineContentTypeDefinition;
#pragma warning restore CS0169
	}
}
