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
using System.Collections.Generic;
using System.Linq;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings.Dialog;
using dnSpy.Decompiler.ILSpy.Core.Settings;
using ICSharpCode.Decompiler;

namespace dnSpy.Decompiler.ILSpy.Settings {
	/// <summary>
	/// Options dialog page with every option of the modern ILSpy engine, grouped by the
	/// C# version that introduced the feature (built from <see cref="CSharpDecompilerSettings.AllOptions"/>)
	/// </summary>
	sealed class CSharpDecompilerSettingsPage : AppSettingsPage, IAppSettingsPage2 {
		readonly DecompilerSettings globalSettings;

		public override double Order => AppSettingsConstants.ORDER_DECOMPILER_SETTINGS_ILSPY_CSHARP;
		public override Guid ParentGuid => new Guid(AppSettingsConstants.GUID_DECOMPILER);
		public override Guid Guid => new Guid("8929CE8E-7E2C-4701-A8BA-42F70363872C");
		public override string Title => "C# (ILSpy)";
		public override object? UIObject => this;

		public IReadOnlyList<OptionGroupVM> Groups { get; }

		public CSharpDecompilerSettingsPage(DecompilerSettings decompilerSettings) {
			globalSettings = decompilerSettings;
			Groups = CSharpDecompilerSettings.AllOptions
				.GroupBy(a => (a.GroupOrder, a.Group))
				.OrderBy(a => a.Key.GroupOrder)
				.Select(g => new OptionGroupVM(g.Key.Group, g.Select(a => new OptionVM(a, (bool)a.Property.GetValue(decompilerSettings)!)).ToArray()))
				.ToArray();
		}

		public override void OnApply() => throw new InvalidOperationException();

		public void OnApply(IAppRefreshSettings appRefreshSettings) {
			bool modified = false;
			foreach (var option in Groups.SelectMany(a => a.Options)) {
				var prop = option.Info.Property;
				if ((bool)prop.GetValue(globalSettings)! != option.IsChecked) {
					prop.SetValue(globalSettings, option.IsChecked);
					modified = true;
				}
			}
			if (modified) {
				appRefreshSettings.Add(AppSettingsConstants.REFRESH_LANGUAGE_SHOWMEMBER);
				appRefreshSettings.Add(SettingsConstants.REDECOMPILE_CSHARP_ILSPY_CODE);
			}
		}

		public override string[]? GetSearchStrings() => Groups.SelectMany(a => a.Options).Select(a => a.Text).ToArray();
	}

	sealed class OptionGroupVM {
		public string Title { get; }
		public IReadOnlyList<OptionVM> Options { get; }

		public OptionGroupVM(string title, IReadOnlyList<OptionVM> options) {
			Title = title;
			Options = options;
		}
	}

	sealed class OptionVM : ViewModelBase {
		public CSharpOptionInfo Info { get; }
		public string Text => Info.Description;
		public string ToolTip => Info.Property.Name + " (--" + Info.Name + ")";

		public bool IsChecked {
			get => isChecked;
			set {
				if (isChecked != value) {
					isChecked = value;
					OnPropertyChanged(nameof(IsChecked));
				}
			}
		}
		bool isChecked;

		public OptionVM(CSharpOptionInfo info, bool isChecked) {
			Info = info;
			this.isChecked = isChecked;
		}
	}
}
