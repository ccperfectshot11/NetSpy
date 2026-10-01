using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace NetSpy.Designer.Model
{
	/// <summary>
	/// Transformă designul într-o temă NetSpy (Themes\netspy.dntheme). Pornește de la tema dnSpy "dark"
	/// (sau "light" pentru un design deschis): culorile paletei Visual Studio sunt înlocuite cu cele din
	/// design, iar elementele importante (cod, arbore, taburi, meniuri) primesc culori explicite.
	/// Lângă temă se scrie și designul (razele, fonturile etc. sunt citite de NetSpy din el).
	/// </summary>
	public static class ThemeExporter
	{
		public const string ThemeFileName = "netspy.dntheme";
		public const string DesignFileName = "netspy" + DesignDocument.FileExtension;
		// GUID fix: setarea „tema curentă” din NetSpy rămâne valabilă după fiecare export
		public const string ThemeGuid = "5E7A1C42-9B3D-4F61-8C2E-7D4B0A9F1E63";

		/// <summary>Folderul Themes al NetSpy, găsit urcând din folderul designerului până la dnSpy.sln.</summary>
		public static string? FindThemesDirectory()
		{
			for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
			{
				if (File.Exists(Path.Combine(dir.FullName, "dnSpy.sln")))
					return Path.Combine(dir.FullName, "dnSpy", "dnSpy", "Themes");
			}
			return null;
		}

		/// <returns>calea temei scrise</returns>
		public static string Export(DesignDocument doc, string themesDirectory)
		{
			bool dark = ColorUtil.Luminance(doc.Color("WindowBg")) < 0.35;
			var baseTheme = File.ReadAllText(Path.Combine(themesDirectory, dark ? "dark.dntheme" : "light.dntheme"));

			var palette = BuildPalette(doc, dark);
			var overrides = BuildOverrides(doc);

			// antetul: nume, GUID și poziția în meniul Themes
			var header = $"<theme guid=\"{ThemeGuid}\" name=\"netspy\" menu-name=\"_NetSpy (Fluent)\" order=\"50\" " +
				(dark ? "is-dark=\"true\"" : "is-light=\"true\"") + " is-high-contrast=\"false\">";
			var text = Regex.Replace(baseTheme, "^<theme [^>]*>", header);

			// fiecare <color name="x" fg=".." bg=".." color3=".." color4=".."/>
			text = Regex.Replace(text, "<color name=\"(?<name>[^\"]+)\"(?<attrs>[^>]*)/>", m => {
				var name = m.Groups["name"].Value;
				var attrs = Regex.Replace(m.Groups["attrs"].Value, "(?<attr>fg|bg|color3|color4)=\"(?<value>[^\"]*)\"", a => {
					var attr = a.Groups["attr"].Value;
					var value = a.Groups["value"].Value;
					if (overrides.TryGetValue(name, out var o) && o.TryGetValue(attr, out var forced))
						return $"{attr}=\"{ColorUtil.ToHex(forced)}\"";
					return $"{attr}=\"{MapPalette(value, palette)}\"";
				});
				// atribute cerute de design care lipsesc din tema de bază (ex. bg pentru linia curentă)
				if (overrides.TryGetValue(name, out var all))
				{
					foreach (var (attr, color) in all)
					{
						if (!Regex.IsMatch(attrs, $"\\b{attr}=\""))
							attrs += $" {attr}=\"{ColorUtil.ToHex(color)}\"";
					}
				}
				return $"<color name=\"{name}\"{attrs}/>";
			});

			var themePath = Path.Combine(themesDirectory, ThemeFileName);
			File.WriteAllText(themePath, "<!-- Generat de NetSpy Designer (Tools\\NetSpy.Designer). Nu edita de mână: reexportă din designer. -->\r\n" + text);
			doc.Save(Path.Combine(themesDirectory, DesignFileName));
			return themePath;
		}

		/// <summary>Culoarea de sus, cu transparența ei, peste o culoare opacă → o culoare opacă.</summary>
		static Color Over(Color top, Color bottom)
		{
			double a = top.A / 255.0;
			byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
			return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
		}

		static Color Darken(Color c, double amount) =>
			Color.FromRgb((byte)(c.R * (1 - amount)), (byte)(c.G * (1 - amount)), (byte)(c.B * (1 - amount)));

		static Dictionary<string, Color> BuildPalette(DesignDocument d, bool dark)
		{
			var panel = d.Color("PanelBg");
			var hover = Over(d.Color("Hover"), panel);
			var selection = Over(d.Color("SelectionBg"), panel);
			var accent = d.Color("Accent");
			var thumb = Over(Color.FromArgb(140, d.Color("ScrollThumb").R, d.Color("ScrollThumb").G, d.Color("ScrollThumb").B), panel);
			if (!dark)
			{
				// paleta temei "light" din dnSpy
				return new Dictionary<string, Color> {
					["F5F5F5"] = d.Color("WindowBg"),
					["EEEEF2"] = d.Color("WindowBg"),
					["CCCEDB"] = d.Color("Border"),
					["007ACC"] = accent,
					["3399FF"] = selection,
					["1E1E1E"] = d.Color("Text"),
				};
			}
			// paleta temei "dark" din dnSpy (Visual Studio 2013/2015 Dark) → culorile designului
			return new Dictionary<string, Color> {
				["F1F1F1"] = d.Color("Text"),
				["2D2D30"] = d.Color("WindowBg"),
				["252526"] = panel,
				["1E1E1E"] = d.Color("EditorBg"),
				["007ACC"] = accent,
				["1C97EA"] = accent,
				["52B0EF"] = accent,
				["0097FB"] = accent,
				["0E6198"] = Darken(accent, 0.3),
				["3399FF"] = selection,
				["3F3F46"] = d.Color("Border"),
				["3E3E42"] = d.Color("Border"),
				["434346"] = d.Color("Border"),
				["3E3E40"] = hover,
				["333334"] = hover,
				["1B1B1C"] = d.Color("PopupBg"),
				["333337"] = d.Color("InputBg"),
				["999999"] = d.Color("TextSecondary"),
				["686868"] = thumb,
			};
		}

		static string MapPalette(string value, Dictionary<string, Color> palette)
		{
			if (!ColorUtil.TryParse(value, out var color))
				return value; // nume de culoare ("White") sau altceva: rămâne cum e
			var key = $"{color.R:X2}{color.G:X2}{color.B:X2}";
			if (!palette.TryGetValue(key, out var mapped))
				return value;
			// păstrăm transparența originală (ex. #72555555)
			return ColorUtil.ToHex(Color.FromArgb(color.A, mapped.R, mapped.G, mapped.B));
		}

		static Dictionary<string, Dictionary<string, Color>> BuildOverrides(DesignDocument d)
		{
			var panel = d.Color("PanelBg");
			var hover = Over(d.Color("Hover"), panel);
			var popupHover = Over(d.Color("Hover"), d.Color("PopupBg"));
			var windowHover = Over(d.Color("Hover"), d.Color("WindowBg"));
			var tabHover = Over(d.Color("Hover"), d.Color("TabStripBg"));
			var selection = Over(d.Color("SelectionBg"), panel);
			var headerBg = d.Color("PanelHeaderBg");

			var o = new Dictionary<string, Dictionary<string, Color>>();
			void Set(string name, params (string Attr, Color Color)[] values)
			{
				if (!o.TryGetValue(name, out var dict))
					o[name] = dict = new Dictionary<string, Color>();
				foreach (var (attr, color) in values)
					dict[attr] = color;
			}
			void All(string name, Color c) => Set(name, ("fg", c), ("bg", c), ("color3", c), ("color4", c));

			// ---- editorul de cod ----
			Set("defaulttext", ("fg", d.Color("EditorText")), ("bg", d.Color("EditorBg")));
			Set("keyword", ("fg", d.Color("SyntaxKeyword")));
			Set("comment", ("fg", d.Color("SyntaxComment")));
			foreach (var n in new[] { "string", "verbatimstring", "char" })
				Set(n, ("fg", d.Color("SyntaxString")));
			Set("number", ("fg", d.Color("SyntaxNumber")));
			foreach (var n in new[] { "type", "sealedtype" })
				Set(n, ("fg", d.Color("SyntaxType")));
			foreach (var n in new[] { "instancemethod", "staticmethod", "extensionmethod" })
				Set(n, ("fg", d.Color("SyntaxMethod")));
			foreach (var n in new[] { "local", "parameter" })
				Set(n, ("fg", d.Color("SyntaxLocal")));
			Set("linenumber", ("fg", d.Color("LineNumber")));
			Set("glyphmargin", ("bg", d.Color("EditorBg")));
			Set("selectedtext", ("bg", d.Color("EditorSelection")));
			foreach (var n in new[] { "currentline", "currentlinenofocus" })
				Set(n, ("fg", d.Color("CurrentLine")), ("bg", d.Color("CurrentLine")));

			// ---- arborele ----
			Set("treeview", ("fg", d.Color("Text")), ("bg", panel));
			Set("treeviewitemselected", ("fg", d.Color("SelectionText")), ("bg", selection));
			Set("treeviewitemmouseover", ("fg", d.Color("Text")), ("bg", hover));
			Set("listboxitemselectedactivebackground", ("bg", selection));
			Set("listboxitemmouseoverbackground", ("bg", hover));

			// ---- taburile documentelor ----
			Set("environmentfiletabbackground", ("bg", d.Color("TabStripBg")));
			All("environmentfiletabselectedgradient", d.Color("TabActiveBg"));
			All("environmentfiletabinactivegradient", d.Color("TabActiveBg"));
			Set("environmentfiletabselectedborder", ("bg", d.Color("Border")));
			Set("environmentfiletabinactiveborder", ("bg", d.Color("Border")));
			Set("environmentfiletabselectedtext", ("bg", d.Color("TabActiveText")));
			Set("environmentfiletabinactivetext", ("bg", d.Color("TabActiveText")));
			Set("environmentfiletabtext", ("bg", d.Color("TabText")));
			All("environmentfiletabhotgradient", tabHover);
			Set("environmentfiletabhotborder", ("bg", tabHover));
			Set("environmentfiletabhottext", ("bg", d.Color("Text")));

			// ---- antetele panourilor (Assembly Explorer etc.): nu mai sunt albastre ----
			Set("environmenttitlebaractive", ("fg", d.Color("PanelHeaderText")), ("bg", headerBg));
			Set("environmenttitlebarinactive", ("fg", d.Color("PanelHeaderText")), ("bg", headerBg));
			All("environmenttitlebaractivegradient", headerBg);
			All("environmenttitlebarinactivegradient", headerBg);
			Set("environmenttitlebaractiveborder", ("bg", headerBg));
			Set("environmenttitlebarinactiveborder", ("bg", headerBg));
			Set("environmenttoolwindow", ("bg", panel));

			// ---- fereastra principală ----
			Set("environmentmainwindowactivecaption", ("fg", d.Color("TitleBarText")), ("bg", d.Color("TitleBarBg")));
			Set("environmentmainwindowinactivecaption", ("fg", d.Color("TextSecondary")), ("bg", d.Color("TitleBarBg")));
			Set("environmentmainwindowactivedefaultborder", ("bg", d.Color("Border")));
			Set("environmentmainwindowinactiveborder", ("bg", d.Color("Border")));

			// ---- meniuri, bara de unelte, popup-uri ----
			Set("contextmenubackground", ("bg", d.Color("PopupBg")));
			Set("contextmenuborderbrush", ("bg", d.Color("Border")));
			Set("toolbarsubmenubackground", ("bg", d.Color("PopupBg")));
			Set("environmentcommandbarmenuitemmouseover", ("fg", d.Color("Text")), ("bg", popupHover));
			Set("toplevelmenuheaderhover", ("bg", windowHover));
			Set("toolbarbuttonhover", ("bg", windowHover));
			Set("toolbarbuttonhoverborder", ("bg", windowHover));
			Set("tooltipbackground", ("fg", d.Color("PopupBg")), ("bg", d.Color("PopupBg")));
			Set("tooltipforeground", ("fg", d.Color("Text")));

			// ---- bare de derulare ----
			Set("environmentscrollbarbackground", ("bg", panel));
			return o;
		}
	}
}
