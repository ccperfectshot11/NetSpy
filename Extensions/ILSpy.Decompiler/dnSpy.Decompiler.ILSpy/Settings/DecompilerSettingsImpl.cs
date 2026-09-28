/*
    Copyright (C) 2014-2019 de4dot@gmail.com
    Copyright (C) 2026 NetSpy contributors

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
using System.ComponentModel.Composition;
using dnSpy.Contracts.Settings;
using dnSpy.Decompiler.ILSpy.Core.Settings;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;

namespace dnSpy.Decompiler.ILSpy.Settings {
	/// <summary>
	/// The global C# decompiler settings (modern ILSpy engine), saved in NetSpy's settings file.
	/// Every boolean option is saved by name, so options added by newer ILSpy versions are saved too.
	/// </summary>
	[Export]
	sealed class DecompilerSettingsImpl : DecompilerSettings {
		// New GUID: the settings of the old ILSpy 2.x engine aren't compatible
		static readonly Guid SETTINGS_GUID = new Guid("0F0C24D9-3A73-4B0B-8C7E-3F6B0E4C7E21");

		readonly ISettingsService settingsService;
		readonly DecompilerSettingsImpl original;
		bool disableSave;

		[ImportingConstructor]
		DecompilerSettingsImpl(ISettingsService settingsService)
			: base(LanguageVersion.Latest) {
			this.settingsService = settingsService;
			original = this;

			disableSave = true;
			CSharpDecompilerSettings.ApplyDefaults(this);
			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			foreach (var info in CSharpDecompilerSettings.AllOptions) {
				var value = sect.Attribute<bool?>(info.Property.Name);
				if (value is not null)
					info.Property.SetValue(this, value.Value);
			}
			disableSave = false;
		}

		protected override void OnPropertyChanged(string? propertyName = null) {
			base.OnPropertyChanged(propertyName);
			// Clone() copies this object, so only the global instance saves the settings
			if (disableSave || !ReferenceEquals(original, this))
				return;
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			foreach (var info in CSharpDecompilerSettings.AllOptions)
				sect.Attribute(info.Property.Name, (bool)info.Property.GetValue(this)!);
		}
	}
}
