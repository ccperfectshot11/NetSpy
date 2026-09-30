using System.Collections.Generic;

namespace NetSpy.Designer.Model
{
	/// <summary>O temă gata făcută: doar valorile care diferă de cele implicite (Fluent luminos).</summary>
	public sealed class Preset
	{
		public Preset(string name, Dictionary<string, string> values)
		{
			Name = name;
			Values = values;
		}

		public string Name { get; }
		public Dictionary<string, string> Values { get; }

		public override string ToString() => Name;

		// Aspectul clasic: drept, lipit, dens (ca dnSpy / Visual Studio).
		static readonly Dictionary<string, string> Classic = new() {
			["RoundedCorners"] = "false",
			["PanelGap"] = "0",
			["TabStyle"] = "Classic",
			["TitleBarStyle"] = "Classic",
			["SelectionIndicator"] = "false",
			["Shadows"] = "false",
			["RowHeight"] = "20",
			["UiFont"] = "Segoe UI",
			["UiFontSize"] = "12",
			["CodeFont"] = "Consolas",
			["ToolBarIcons"] = "Classic",
		};

		static readonly Dictionary<string, string> DarkEditor = new() {
			["EditorText"] = "#DCDCDC",
			["LineNumber"] = "#858585",
			["EditorSelection"] = "#264F78",
			["SyntaxKeyword"] = "#569CD6",
			["SyntaxControl"] = "#D8A0DF",
			["SyntaxType"] = "#4EC9B0",
			["SyntaxMethod"] = "#DCDCAA",
			["SyntaxLocal"] = "#9CDCFE",
			["SyntaxString"] = "#D69D85",
			["SyntaxNumber"] = "#B5CEA8",
			["SyntaxComment"] = "#57A64A",
		};

		static Dictionary<string, string> Merge(params Dictionary<string, string>[] parts)
		{
			var result = new Dictionary<string, string>();
			foreach (var part in parts)
			{
				foreach (var (k, v) in part)
					result[k] = v;
			}
			return result;
		}

		public static IReadOnlyList<Preset> All { get; } = new[] {
			new Preset("Fluent luminos (Windows 11)", new()),

			new Preset("Fluent întunecat (Windows 11)", Merge(DarkEditor, new() {
				["Accent"] = "#4CC2FF",
				["AccentText"] = "#000000",
				["WindowBg"] = "#202020",
				["TitleBarBg"] = "#202020",
				["TitleBarText"] = "#FFFFFF",
				["ToolBarBg"] = "#202020",
				["PanelBg"] = "#272727",
				["PanelHeaderBg"] = "#272727",
				["PanelHeaderText"] = "#C5C5C5",
				["Border"] = "#333333",
				["Text"] = "#FFFFFF",
				["TextSecondary"] = "#C5C5C5",
				["Hover"] = "#0FFFFFFF",
				["SelectionBg"] = "#264CC2FF",
				["SelectionText"] = "#FFFFFF",
				["TabStripBg"] = "#272727",
				["TabActiveBg"] = "#1F1F1F",
				["TabActiveText"] = "#FFFFFF",
				["TabText"] = "#C5C5C5",
				["InputBg"] = "#1F1F1F",
				["PopupBg"] = "#2C2C2C",
				["ScrollThumb"] = "#9F9F9F",
				["StatusBarBg"] = "#202020",
				["StatusBarText"] = "#C5C5C5",
				["EditorBg"] = "#1F1F1F",
				["CurrentLine"] = "#2A2A2A",
			})),

			new Preset("Miezul nopții (violet)", Merge(DarkEditor, new() {
				["Accent"] = "#A371F7",
				["AccentText"] = "#FFFFFF",
				["WindowBg"] = "#0D1117",
				["TitleBarBg"] = "#0D1117",
				["TitleBarText"] = "#E6EDF3",
				["ToolBarBg"] = "#0D1117",
				["PanelBg"] = "#161B22",
				["PanelHeaderBg"] = "#161B22",
				["PanelHeaderText"] = "#8B949E",
				["Border"] = "#30363D",
				["Text"] = "#E6EDF3",
				["TextSecondary"] = "#8B949E",
				["Hover"] = "#14FFFFFF",
				["SelectionBg"] = "#33A371F7",
				["SelectionText"] = "#FFFFFF",
				["TabStripBg"] = "#161B22",
				["TabActiveBg"] = "#0D1117",
				["TabActiveText"] = "#E6EDF3",
				["TabText"] = "#8B949E",
				["InputBg"] = "#0D1117",
				["PopupBg"] = "#1C2128",
				["ScrollThumb"] = "#484F58",
				["StatusBarBg"] = "#0D1117",
				["StatusBarText"] = "#8B949E",
				["EditorBg"] = "#0D1117",
				["EditorText"] = "#E6EDF3",
				["LineNumber"] = "#6E7681",
				["CurrentLine"] = "#161B22",
				["EditorSelection"] = "#3B2E58",
				["SyntaxKeyword"] = "#FF7B72",
				["SyntaxControl"] = "#FF7B72",
				["SyntaxType"] = "#FFA657",
				["SyntaxMethod"] = "#D2A8FF",
				["SyntaxLocal"] = "#E6EDF3",
				["SyntaxString"] = "#A5D6FF",
				["SyntaxNumber"] = "#79C0FF",
				["SyntaxComment"] = "#8B949E",
			})),

			new Preset("Visual Studio albastru (dnSpy)", Merge(Classic, new() {
				["Accent"] = "#3399FF",
				["WindowBg"] = "#293955",
				["TitleBarBg"] = "#D6DBE9",
				["TitleBarText"] = "#1E1E1E",
				["ToolBarBg"] = "#D6DBE9",
				["PanelBg"] = "#FFFFFF",
				["PanelHeaderBg"] = "#4D6082",
				["PanelHeaderText"] = "#FFFFFF",
				["Border"] = "#8E9BBC",
				["Text"] = "#1E1E1E",
				["TextSecondary"] = "#444444",
				["Hover"] = "#FFFCF4",
				["SelectionBg"] = "#3399FF",
				["SelectionText"] = "#FFFFFF",
				["TabStripBg"] = "#293955",
				["TabActiveBg"] = "#F5CC84",
				["TabActiveText"] = "#1E1E1E",
				["TabText"] = "#FFFFFF",
				["PopupBg"] = "#E9ECEE",
				["ScrollThumb"] = "#C2C3C9",
				["StatusBarBg"] = "#007ACC",
				["StatusBarText"] = "#FFFFFF",
				["CurrentLine"] = "#FFFFFF",
				["LineNumber"] = "#2B91AF",
			})),

			new Preset("Visual Studio întunecat (dnSpy)", Merge(Classic, DarkEditor, new() {
				["Accent"] = "#007ACC",
				["WindowBg"] = "#2D2D30",
				["TitleBarBg"] = "#2D2D30",
				["TitleBarText"] = "#F1F1F1",
				["ToolBarBg"] = "#2D2D30",
				["PanelBg"] = "#252526",
				["PanelHeaderBg"] = "#2D2D30",
				["PanelHeaderText"] = "#F1F1F1",
				["Border"] = "#3F3F46",
				["Text"] = "#F1F1F1",
				["TextSecondary"] = "#999999",
				["Hover"] = "#3E3E40",
				["SelectionBg"] = "#3399FF",
				["SelectionText"] = "#FFFFFF",
				["TabStripBg"] = "#2D2D30",
				["TabActiveBg"] = "#007ACC",
				["TabActiveText"] = "#FFFFFF",
				["TabText"] = "#F1F1F1",
				["InputBg"] = "#333337",
				["PopupBg"] = "#1B1B1C",
				["ScrollThumb"] = "#686868",
				["StatusBarBg"] = "#007ACC",
				["StatusBarText"] = "#FFFFFF",
				["EditorBg"] = "#1E1E1E",
				["CurrentLine"] = "#282828",
				["LineNumber"] = "#2B91AF",
			})),
		};
	}
}
