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
using System.IO;
using System.Threading;
using dnlib.DotNet;

namespace NetSpy.Contracts.Decompiler {
	/// <summary>
	/// Data of <see cref="DecompilationType.WholeProject"/>: the decompiler writes the project file
	/// and every source/resource file of <see cref="Module"/> itself
	/// </summary>
	public sealed class DecompileWholeProject {
		/// <summary>
		/// Module to decompile
		/// </summary>
		public ModuleDef Module { get; }

		/// <summary>
		/// Full path of the project file to create (eg. C:\out\MyLib\MyLib.csproj). The other files are
		/// written to its directory.
		/// </summary>
		public string ProjectFilename { get; }

		/// <summary>
		/// Project GUID (the solution file references it)
		/// </summary>
		public Guid ProjectGuid { get; }

		/// <summary>
		/// Decompilation context
		/// </summary>
		public DecompilationContext Context { get; }

		/// <summary>
		/// true to create an SDK style project file
		/// </summary>
		public bool SdkStyleProject { get; set; }

		/// <summary>
		/// true to unpack the resources
		/// </summary>
		public bool UnpackResources { get; set; } = true;

		/// <summary>
		/// true to create .resx files from .resources files
		/// </summary>
		public bool CreateResX { get; set; } = true;

		/// <summary>
		/// Decompiles BAML to XAML or null (then BAML is saved as is). Writes the XAML to the stream and returns the referenced assemblies.
		/// </summary>
		public Func<ModuleDef, byte[], CancellationToken, Stream, IList<string>>? DecompileBaml { get; set; }

		/// <summary>
		/// Gets the x:Class type name of BAML or null
		/// </summary>
		public Func<ModuleDef, byte[], CancellationToken, string?>? DecompileBamlTypeName { get; set; }

		/// <summary>
		/// Called for every error (the export continues)
		/// </summary>
		public Action<string>? Error { get; set; }

		/// <summary>
		/// Called after each written file with the number of written files and the total number of files
		/// </summary>
		public Action<int, int>? Progress { get; set; }

		/// <summary>
		/// Cancellation token
		/// </summary>
		public CancellationToken CancellationToken => Context.CancellationToken;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="module">Module</param>
		/// <param name="projectFilename">Full path of the project file</param>
		/// <param name="projectGuid">Project GUID</param>
		/// <param name="context">Decompilation context</param>
		public DecompileWholeProject(ModuleDef module, string projectFilename, Guid projectGuid, DecompilationContext context) {
			Module = module ?? throw new ArgumentNullException(nameof(module));
			ProjectFilename = projectFilename ?? throw new ArgumentNullException(nameof(projectFilename));
			ProjectGuid = projectGuid;
			Context = context ?? throw new ArgumentNullException(nameof(context));
		}
	}
}
