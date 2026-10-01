/*
    Copyright (C) 2026 NetSpy contributors

    This file is part of NetSpy (a fork of dnSpy)

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
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using NetSpy.Contracts.App;
using WpfColor = System.Windows.Media.Color;

namespace NetSpy.Themes {
	/// <summary>
	/// The NetSpy design values stored next to the theme (Themes\netspy.netspy-design.json):
	/// corner radii, fonts, toolbar icon style... The file is a flat "key": "value" object, read with a
	/// regex so it works on .NET Framework too (no System.Text.Json there).
	/// </summary>
	static class NetSpyDesign {
		const string FileName = "netspy.netspy-design.json";
		static readonly Dictionary<string, string> values = Load();

		static Dictionary<string, string> Load() {
			var dict = new Dictionary<string, string>(StringComparer.Ordinal);
			try {
				foreach (var dir in AppDirectories.GetDirectories("Themes")) {
					var path = Path.Combine(dir, FileName);
					if (!File.Exists(path))
						continue;
					foreach (Match m in Regex.Matches(File.ReadAllText(path), "\"(?<key>\\w+)\"\\s*:\\s*\"(?<value>[^\"]*)\""))
						dict[m.Groups["key"].Value] = m.Groups["value"].Value;
					break;
				}
			}
			catch (IOException) {
			}
			catch (UnauthorizedAccessException) {
			}
			return dict;
		}

		public static string Get(string key, string defaultValue) => values.TryGetValue(key, out var value) ? value : defaultValue;

		public static double GetNumber(string key, double defaultValue) =>
			double.TryParse(Get(key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : defaultValue;

		public static bool GetBool(string key, bool defaultValue) => bool.TryParse(Get(key, string.Empty), out var b) ? b : defaultValue;

		/// <summary>Windows 11 (Segoe Fluent Icons) glyphs on the toolbar instead of the colored images</summary>
		public static bool FluentToolBarIcons => Get("ToolBarIcons", "Fluent") == "Fluent";

		/// <summary>Radius used when "RoundedCorners" is on, else 0</summary>
		public static double Radius(string key, double defaultValue) => GetBool("RoundedCorners", true) ? GetNumber(key, defaultValue) : 0;

		static WpfColor GetColor(string key, WpfColor defaultValue) {
			var hex = Get(key, string.Empty).TrimStart('#');
			if (hex.Length == 6)
				hex = "FF" + hex;
			if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
				return WpfColor.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
			return defaultValue;
		}

		static SolidColorBrush Brush(string key, WpfColor defaultValue) {
			var brush = new SolidColorBrush(GetColor(key, defaultValue));
			brush.Freeze();
			return brush;
		}

		/// <summary>
		/// Adds the design values as "NetSpy.*" resources; the templates in wpf.styles.templates.xaml
		/// read them with DynamicResource (keys "NetSpy.<setting>")
		/// </summary>
		public static void ApplyTo(ResourceDictionary resources) {
			double control = Radius("ControlRadius", 4), panel = Radius("PanelRadius", 8), popup = Radius("PopupRadius", 8);
			resources["NetSpy.IconFont"] = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
			resources["NetSpy.ControlCorner"] = new CornerRadius(control);
			resources["NetSpy.TabCorner"] = new CornerRadius(control, control, 0, 0);
			resources["NetSpy.PanelCorner"] = new CornerRadius(panel);
			resources["NetSpy.PanelInnerRadius"] = Math.Max(0, panel - 1);
			resources["NetSpy.PopupCorner"] = new CornerRadius(popup);
			double gap = GetNumber("PanelGap", 6);
			resources["NetSpy.PanelGap"] = gap;
			resources["NetSpy.PanelMargin"] = new Thickness(gap / 2);
			resources["NetSpy.RowHeight"] = GetNumber("RowHeight", 26);
			resources["NetSpy.IconSize"] = GetNumber("IconSize", 16);
			resources["NetSpy.UiFont"] = new FontFamily(Get("UiFont", "Segoe UI Variable Text") + ", Segoe UI");
			resources["NetSpy.UiFontSize"] = GetNumber("UiFontSize", 13);
			resources["NetSpy.Accent"] = Brush("Accent", WpfColor.FromRgb(0x4C, 0xC2, 0xFF));
			resources["NetSpy.TextSecondary"] = Brush("TextSecondary", WpfColor.FromRgb(0xC5, 0xC5, 0xC5));
			resources["NetSpy.Border"] = Brush("Border", WpfColor.FromRgb(0x33, 0x33, 0x33));
			resources["NetSpy.PanelBg"] = Brush("PanelBg", WpfColor.FromRgb(0x27, 0x27, 0x27));
			resources["NetSpy.PanelHeaderBg"] = Brush("PanelHeaderBg", WpfColor.FromRgb(0x27, 0x27, 0x27));
			resources["NetSpy.PanelHeaderText"] = Brush("PanelHeaderText", WpfColor.FromRgb(0xC5, 0xC5, 0xC5));
			resources["NetSpy.Hover"] = Brush("Hover", WpfColor.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
			resources["NetSpy.SelectionBg"] = Brush("SelectionBg", WpfColor.FromArgb(0x26, 0x4C, 0xC2, 0xFF));
			resources["NetSpy.TabActiveBg"] = Brush("TabActiveBg", WpfColor.FromRgb(0x1F, 0x1F, 0x1F));
			resources["NetSpy.TabStripBg"] = Brush("TabStripBg", WpfColor.FromRgb(0x27, 0x27, 0x27));
			resources["NetSpy.PopupBg"] = Brush("PopupBg", WpfColor.FromRgb(0x2C, 0x2C, 0x2C));
			resources["NetSpy.InputBg"] = Brush("InputBg", WpfColor.FromRgb(0x1F, 0x1F, 0x1F));
			resources["NetSpy.ScrollThumb"] = Brush("ScrollThumb", WpfColor.FromRgb(0x9F, 0x9F, 0x9F));
			resources["NetSpy.SelectionIndicatorVisibility"] = GetBool("SelectionIndicator", true) ? Visibility.Visible : Visibility.Collapsed;
			resources["NetSpy.ShadowOpacity"] = GetBool("Shadows", true) ? 0.28 : 0.0;
		}
	}
}
