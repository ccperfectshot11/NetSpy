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
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NetSpy.Contracts.Decompiler;
using NetSpy.Decompiler.Core.Settings;
using NetSpy.Decompiler;
using NetSpy.Decompiler.CSharp;

namespace NetSpy.Decompiler.Engine.Core.Settings {
	/// <summary>
	/// Describes one boolean option of the modern NetSpy <see cref="DecompilerSettings"/> class.
	/// The list is built with reflection so every option NetSpy adds shows up automatically
	/// in the options dialog and in NetSpy.Console.
	/// </summary>
	sealed class CSharpOptionInfo {
		public PropertyInfo Property { get; }
		public Guid Guid { get; }
		public string Name { get; }
		public string Description { get; }
		/// <summary>Options page group, eg. "C# 9.0" or "Other"</summary>
		public string Group { get; }
		/// <summary>Sort key of <see cref="Group"/></summary>
		public int GroupOrder { get; }

		public CSharpOptionInfo(PropertyInfo property, Guid guid, string name, string description, string group, int groupOrder) {
			Property = property;
			Guid = guid;
			Name = name;
			Description = description;
			Group = group;
			GroupOrder = groupOrder;
		}
	}

	sealed class CSharpDecompilerSettings : DecompilerSettingsBase {
		/// <summary>
		/// The modern NetSpy settings. Every change raises <see cref="VersionChanged"/>.
		/// </summary>
		public DecompilerSettings Settings { get; }

		/// <summary>
		/// NetSpy-only option: show metadata tokens / RVAs in short names (tree view, tooltips).
		/// The modern engine never writes them into C# code.
		/// </summary>
		public bool ShowTokenAndRvaComments {
			get => showTokenAndRvaComments;
			set {
				if (showTokenAndRvaComments != value) {
					showTokenAndRvaComments = value;
					OnModified();
				}
			}
		}
		bool showTokenAndRvaComments;

		public override int Version => version;
		int version;
		public override event EventHandler? VersionChanged;

		public static IReadOnlyList<CSharpOptionInfo> AllOptions { get; } = CreateOptionInfos();

		public CSharpDecompilerSettings(DecompilerSettings? settings = null) {
			Settings = settings ?? CreateDefaultSettings();
			Settings.PropertyChanged += (s, e) => OnModified();
			options = CreateOptions().ToArray();
		}

		/// <summary>
		/// Defaults chosen so decompiled code is closest to the original source and builds without warnings
		/// </summary>
		public static DecompilerSettings CreateDefaultSettings() {
			var settings = new DecompilerSettings(LanguageVersion.Latest);
			ApplyDefaults(settings);
			return settings;
		}

		/// <summary>
		/// NetSpy's defaults (on top of NetSpy's defaults)
		/// </summary>
		public static void ApplyDefaults(DecompilerSettings settings) {
			// Show 'using' directives, like the original source
			settings.UsingDeclarations = true;
		}

		void OnModified() {
			version++;
			VersionChanged?.Invoke(this, EventArgs.Empty);
		}

		public override DecompilerSettingsBase Clone() =>
			new CSharpDecompilerSettings(Settings.Clone()) { showTokenAndRvaComments = showTokenAndRvaComments };

		public override IEnumerable<IDecompilerOption> Options => options;
		readonly IDecompilerOption[] options;

		IEnumerable<IDecompilerOption> CreateOptions() {
			foreach (var info in AllOptions) {
				var prop = info.Property;
				yield return new DecompilerOption<bool>(info.Guid, () => (bool)prop.GetValue(Settings)!, a => prop.SetValue(Settings, a)) {
					Name = info.Name,
					Description = info.Description,
				};
			}
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ShowTokenAndRvaComments_GUID,
						() => ShowTokenAndRvaComments, a => ShowTokenAndRvaComments = a) {
				Description = "Show tokens, RVAs and file offsets",
				Name = DecompilerOptionConstants.ShowTokenAndRvaComments_NAME,
			};
		}

		static IReadOnlyList<CSharpOptionInfo> CreateOptionInfos() {
			var list = new List<CSharpOptionInfo>();
			var constants = typeof(DecompilerOptionConstants);
			foreach (var prop in typeof(DecompilerSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
				if (prop.PropertyType != typeof(bool) || !prop.CanRead || !prop.CanWrite)
					continue;
				if (prop.GetSetMethod() is null || prop.GetIndexParameters().Length != 0)
					continue;
				// Only real options have a description (skips helpers without UI meaning)
				var descAttr = prop.GetCustomAttribute<DescriptionAttribute>();
				if (descAttr is null)
					continue;

				// Reuse NetSpy's GUID/name if NetSpy had an option with the same name, so
				// existing code (eg. the analyzer) and old command lines keep working
				var guid = constants.GetField(prop.Name + "_GUID", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Guid? ?? CreateGuid(prop.Name);
				var name = constants.GetField(prop.Name + "_NAME", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string ?? ToOptionName(prop.Name);

				var description = GetText(descAttr.Description) ?? SplitWords(prop.Name);
				GetGroup(prop, out var group, out int groupOrder);
				list.Add(new CSharpOptionInfo(prop, guid, name, description, group, groupOrder));
			}
			return list.OrderBy(a => a.GroupOrder).ThenBy(a => a.Description, StringComparer.OrdinalIgnoreCase).ToArray();
		}

		static void GetGroup(PropertyInfo prop, out string group, out int groupOrder) {
			var category = prop.GetCustomAttribute<CategoryAttribute>();
			if (category is not null) {
				group = GetText(category.Category) ?? category.Category;
				groupOrder = 1000;
				return;
			}
			// The DecompilerSetting(LanguageVersion) attribute is generated by a source generator
			// and embedded in the decompiler assembly, so read it by name.
			foreach (var ca in prop.CustomAttributes) {
				if (ca.AttributeType.Name != "DecompilerSettingAttribute")
					continue;
				if (ca.ConstructorArguments.Count == 1 && ca.ConstructorArguments[0].Value is int versionValue) {
					var version = (LanguageVersion)versionValue;
					group = "C# " + GetVersionString(version);
					groupOrder = versionValue;
					return;
				}
			}
			group = GetText("DecompilerSettings.Other") ?? "Other";
			groupOrder = 1000;
		}

		static string GetVersionString(LanguageVersion version) {
			var s = version.ToString();
			// "CSharp7_3" => "7.3", "CSharp9_0" => "9.0", "CSharp1" => "1"
			if (s.StartsWith("CSharp", StringComparison.Ordinal))
				s = s.Substring("CSharp".Length).Replace('_', '.');
			return s;
		}

		static string? GetText(string key) => EngineSettingsText.Texts.TryGetValue(key, out var text) ? text : null;

		static string SplitWords(string name) {
			var sb = new StringBuilder(name.Length + 8);
			for (int i = 0; i < name.Length; i++) {
				if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
					sb.Append(' ');
				sb.Append(i == 0 ? name[i] : char.ToLowerInvariant(name[i]));
			}
			return sb.ToString();
		}

		/// <summary>"NativeIntegers" => "native-integers" (used by NetSpy.Console: --native-integers / --no-native-integers)</summary>
		static string ToOptionName(string name) {
			var sb = new StringBuilder(name.Length + 8);
			for (int i = 0; i < name.Length; i++) {
				char c = name[i];
				if (char.IsUpper(c)) {
					if (i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
						sb.Append('-');
					sb.Append(char.ToLowerInvariant(c));
				}
				else
					sb.Append(c);
			}
			return sb.ToString();
		}

		/// <summary>Stable GUID derived from the property name, so saved settings survive restarts</summary>
		static Guid CreateGuid(string name) {
			using var md5 = MD5.Create();
			var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("NetSpy.CSharpDecompilerSettings." + name));
			return new Guid(hash);
		}

		public override bool Equals(object? obj) {
			if (obj is not CSharpDecompilerSettings other)
				return false;
			if (showTokenAndRvaComments != other.showTokenAndRvaComments)
				return false;
			foreach (var info in AllOptions) {
				if (!Equals(info.Property.GetValue(Settings), info.Property.GetValue(other.Settings)))
					return false;
			}
			return true;
		}

		public override int GetHashCode() {
			int h = showTokenAndRvaComments ? 1 : 0;
			foreach (var info in AllOptions)
				h = h * 31 + ((bool)info.Property.GetValue(Settings)! ? 1 : 0);
			return h;
		}
	}
}
