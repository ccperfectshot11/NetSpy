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

using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.MSBuild {
	/// <summary>
	/// The whole project (project file + sources + resources) written by a decompiler that
	/// supports <see cref="DecompilationType.WholeProject"/> (NetSpy's C# decompiler)
	/// </summary>
	sealed class WholeProjectFile : ProjectFile {
		readonly Project project;

		public WholeProjectFile(Project project) => this.project = project;

		public override string Description => "Decompiling project " + System.IO.Path.GetFileName(project.Filename);
		public override string Filename => project.Filename;
		public override BuildAction BuildAction => BuildAction.None;

		public override void Create(DecompileContext ctx) {
			var options = project.Options;
			var data = new DecompileWholeProject(options.Module, project.Filename, options.ProjectGuid, options.DecompilationContext) {
				SdkStyleProject = project.SdkStyle,
				UnpackResources = options.UnpackResources,
				CreateResX = options.CreateResX,
				DecompileBaml = options.DecompileXaml ? options.DecompileBaml : null,
				DecompileBamlTypeName = options.DecompileXaml ? options.DecompileBamlTypeName : null,
				Error = ctx.Logger.Error,
			};
			options.Decompiler.Decompile(DecompilationType.WholeProject, data);
		}
	}
}
