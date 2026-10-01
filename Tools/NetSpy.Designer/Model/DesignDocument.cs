using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace NetSpy.Designer.Model
{
	/// <summary>
	/// Tot designul NetSpy: lista de setări, temele predefinite, salvarea în JSON și
	/// transformarea setărilor în resurse WPF ("NS.Cheie") folosite de previzualizare.
	/// </summary>
	public sealed class DesignDocument
	{
		public const string FileExtension = ".netspy-design.json";

		const string GCorners = "Colțuri rotunjite (WinUI)";
		const string GLayout = "Aspect";
		const string GFonts = "Fonturi";
		const string GApp = "Culori aplicație";
		const string GEditor = "Culori editor de cod";

		readonly Dictionary<string, Setting> byKey;

		public DesignDocument()
		{
			Settings = CreateSettings();
			byKey = Settings.ToDictionary(s => s.Key);
			Groups = Settings.GroupBy(s => s.Group)
				.Select((g, i) => new SettingGroup(g.Key, g.ToList(), isExpanded: i < 2))
				.ToList();
			foreach (var setting in Settings)
				setting.PropertyChanged += OnSettingChanged;
		}

		public IReadOnlyList<Setting> Settings { get; }
		public IReadOnlyList<SettingGroup> Groups { get; }

		/// <summary>Se declanșează după orice modificare (o singură dată și pentru o temă aplicată întreagă).</summary>
		public event EventHandler? Changed;

		int suspendCount;

		void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(Setting.Value) && suspendCount == 0)
				Changed?.Invoke(this, EventArgs.Empty);
		}

		public string this[string key] => byKey[key].Value;
		public Setting Get(string key) => byKey[key];
		public bool Bool(string key) => byKey[key].Value == "true";
		public double Number(string key) => ((NumberSetting)byKey[key]).Number;
		public Color Color(string key) => ColorUtil.Parse(byKey[key].Value, Colors.Magenta);

		/// <summary>Schimbă mai multe valori deodată și anunță o singură dată.</summary>
		public void SetValues(IEnumerable<KeyValuePair<string, string>> values, bool resetOthers)
		{
			suspendCount++;
			try
			{
				if (resetOthers)
				{
					foreach (var setting in Settings)
						setting.Value = setting.DefaultValue;
				}
				foreach (var (key, value) in values)
				{
					if (byKey.TryGetValue(key, out var setting))
						setting.Value = value;
				}
			}
			finally
			{
				suspendCount--;
			}
			Changed?.Invoke(this, EventArgs.Empty);
		}

		public void ApplyPreset(Preset preset) => SetValues(preset.Values, resetOthers: true);

		#region JSON

		public string ToJson()
		{
			var values = new JsonObject();
			foreach (var setting in Settings)
				values[setting.Key] = setting.Value;
			var root = new JsonObject {
				["format"] = "netspy-design",
				["version"] = 1,
				["values"] = values,
			};
			return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
		}

		public void LoadJson(string json)
		{
			var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Fișierul nu conține un obiect JSON.");
			if ((string?)root["format"] != "netspy-design")
				throw new InvalidDataException("Nu este un fișier de design NetSpy.");
			var values = root["values"] as JsonObject ?? throw new InvalidDataException("Lipsește secțiunea \"values\".");
			SetValues(values.Select(p => new KeyValuePair<string, string>(p.Key, p.Value?.ToString() ?? "")), resetOthers: true);
		}

		public void Save(string path) => File.WriteAllText(path, ToJson());
		public void Load(string path) => LoadJson(File.ReadAllText(path));

		#endregion

		#region Resurse WPF

		/// <summary>Scrie toate setările ca resurse "NS.Cheie" (pinsuri, numere, fonturi) plus câteva derivate.</summary>
		public void ApplyTo(ResourceDictionary resources)
		{
			foreach (var setting in Settings)
			{
				string key = "NS." + setting.Key;
				switch (setting)
				{
					case ColorSetting:
						var color = ColorUtil.Parse(setting.Value, Colors.Magenta);
						var brush = new SolidColorBrush(color);
						brush.Freeze();
						resources[key] = brush;
						resources[key + ".Color"] = color;
						break;
					case NumberSetting number:
						resources[key] = number.Number;
						break;
					case BoolSetting b:
						resources[key + ".Visibility"] = b.IsChecked ? Visibility.Visible : Visibility.Collapsed;
						break;
					case FontSetting:
						string fallback = setting.Key == "CodeFont" ? ", Cascadia Mono, Consolas" : ", Segoe UI";
						resources[key] = new FontFamily(setting.Value + fallback);
						break;
				}
			}

			// Colțurile: dacă "Colțuri rotunjite" e oprit, toate razele devin 0 (aspect clasic, drept).
			bool rounded = Bool("RoundedCorners");
			double R(string k) => rounded ? Number(k) : 0;
			resources["NS.ControlCorner"] = new CornerRadius(R("ControlRadius"));
			resources["NS.PanelCorner"] = new CornerRadius(R("PanelRadius"));
			resources["NS.PopupCorner"] = new CornerRadius(R("PopupRadius"));
			resources["NS.WindowCorner"] = new CornerRadius(R("WindowRadius"));
			resources["NS.WindowRadiusValue"] = R("WindowRadius");
			resources["NS.PanelRadiusValue"] = R("PanelRadius");
			// conținutul stă în interiorul chenarului de 1px, deci masca lui e cu 1px mai mică
			resources["NS.WindowInnerRadiusValue"] = Math.Max(0, R("WindowRadius") - 1);
			resources["NS.PanelInnerRadiusValue"] = Math.Max(0, R("PanelRadius") - 1);
			// taburile au colțuri doar sus
			resources["NS.TabCorner"] = new CornerRadius(R("ControlRadius"), R("ControlRadius"), 0, 0);
			resources["NS.PillCorner"] = new CornerRadius(rounded ? Math.Max(2, R("ControlRadius")) : 0);

			double codeSize = Number("CodeFontSize");
			resources["NS.CodeLineHeight"] = Math.Round(codeSize * 1.45);
			resources["NS.IconSizeValue"] = Number("IconSize");
			resources["NS.TreeIndent"] = Math.Max(12, Number("IconSize") + 4);
			resources["NS.ShadowOpacity"] = Bool("Shadows") ? 0.28 : 0.0;
			bool fluentIcons = this["ToolBarIcons"] == "Fluent";
			resources["NS.FluentIcons.Visibility"] = fluentIcons ? Visibility.Visible : Visibility.Collapsed;
			resources["NS.ClassicIcons.Visibility"] = fluentIcons ? Visibility.Collapsed : Visibility.Visible;
		}

		/// <summary>Iconițele trebuie recolorate pentru fundal închis?</summary>
		public bool UseDarkIcons => this["IconTheme"] switch {
			"Light" => false,
			"Dark" => true,
			_ => ColorUtil.Luminance(Color("PanelBg")) < 0.35,
		};

		#endregion

		#region Definiții

		static List<Setting> CreateSettings() => new() {
			// ---- Colțuri rotunjite ----
			new BoolSetting("RoundedCorners", "Colțuri rotunjite", GCorners, true,
				"Oprit = colțuri drepte peste tot (aspectul clasic NetSpy / Visual Studio)."),
			new NumberSetting("ControlRadius", "Rază controale", GCorners, 4, 0, 12,
				hint: "Butoane, taburi, selecția din arbore, câmpuri. WinUI folosește 4."),
			new NumberSetting("PanelRadius", "Rază panouri", GCorners, 8, 0, 20,
				hint: "Cardurile arborelui și editorului. WinUI folosește 8."),
			new NumberSetting("PopupRadius", "Rază meniuri / popup", GCorners, 8, 0, 16),
			new NumberSetting("WindowRadius", "Rază fereastră", GCorners, 8, 0, 16,
				hint: "Windows 11 rotunjește ferestrele cu 8."),
			new NumberSetting("PanelGap", "Spațiu între panouri", GCorners, 6, 0, 16,
				hint: "0 = panouri lipite (Visual Studio), 6–8 = carduri separate (WinUI)."),

			// ---- Aspect ----
			new ChoiceSetting("TitleBarStyle", "Bara de titlu", GLayout, new Choice[] {
				new("Integrated", "Integrată (meniul în bara de titlu)"),
				new("Classic", "Clasică (meniul pe rând separat)"),
			}, "Integrated"),
			new ChoiceSetting("TabStyle", "Stil taburi", GLayout, new Choice[] {
				new("Rounded", "Rotunjite (WinUI)"),
				new("Pill", "Pastile"),
				new("Underline", "Subliniate"),
				new("Classic", "Clasice (Visual Studio)"),
			}, "Rounded"),
			new ChoiceSetting("TreePosition", "Poziție arbore", GLayout, new Choice[] {
				new("Left", "Stânga"),
				new("Right", "Dreapta"),
			}, "Left"),
			new ChoiceSetting("ToolBarIcons", "Stil iconițe butoane", GLayout, new Choice[] {
				new("Fluent", "Fluent (Windows 11, monocrome)"),
				new("Classic", "Clasice (NetSpy / Visual Studio, colorate)"),
			}, "Fluent", "Bara de unelte și meniurile. Arborele păstrează iconițele colorate (culoarea arată tipul)."),
			new ChoiceSetting("IconTheme", "Culori iconițe", GLayout, new Choice[] {
				new("Auto", "Automat (după fundal)"),
				new("Light", "Pentru fundal deschis"),
				new("Dark", "Pentru fundal închis"),
			}, "Auto"),
			new NumberSetting("TreeWidth", "Lățime arbore", GLayout, 300, 180, 520),
			new NumberSetting("RowHeight", "Înălțime rând (arbore)", GLayout, 26, 18, 40,
				hint: "Densitatea: ~20 compact (NetSpy), ~26 normal, ~32 aerisit (WinUI)."),
			new NumberSetting("IconSize", "Mărime iconițe", GLayout, 16, 12, 24),
			new BoolSetting("SelectionIndicator", "Indicator selecție (bara accent WinUI)", GLayout, true),
			new BoolSetting("Shadows", "Umbre (fereastră, meniuri)", GLayout, true),
			new BoolSetting("ShowToolBar", "Bara de unelte", GLayout, true),
			new BoolSetting("ShowStatusBar", "Bara de stare", GLayout, true),
			new BoolSetting("ShowLineNumbers", "Numere de linie", GLayout, true),
			new BoolSetting("ShowBottomPanel", "Panoul de jos (Analizor)", GLayout, false),
			new BoolSetting("ShowMenuOpen", "Arată meniul „File” deschis", GLayout, false,
				"Doar pentru previzualizare: ca să vezi cum arată meniurile."),

			// ---- Fonturi ----
			new FontSetting("UiFont", "Font interfață", GFonts, "Segoe UI Variable Text"),
			new NumberSetting("UiFontSize", "Mărime font interfață", GFonts, 13, 10, 18),
			new FontSetting("CodeFont", "Font cod", GFonts, "Cascadia Mono"),
			new NumberSetting("CodeFontSize", "Mărime font cod", GFonts, 13, 9, 24),

			// ---- Culori aplicație (implicit: Fluent luminos) ----
			new ColorSetting("Accent", "Accent", GApp, "#0067C0", "Culoarea principală: butoane active, indicatorul de selecție, sublinieri."),
			new ColorSetting("AccentText", "Text pe accent", GApp, "#FFFFFF"),
			new ColorSetting("WindowBg", "Fundal fereastră", GApp, "#F3F3F3"),
			new ColorSetting("TitleBarBg", "Bara de titlu", GApp, "#F3F3F3"),
			new ColorSetting("TitleBarText", "Text bara de titlu", GApp, "#1B1B1B"),
			new ColorSetting("ToolBarBg", "Bara de unelte / meniu", GApp, "#F3F3F3"),
			new ColorSetting("PanelBg", "Fundal panouri", GApp, "#FBFBFB"),
			new ColorSetting("PanelHeaderBg", "Antet panou", GApp, "#FBFBFB"),
			new ColorSetting("PanelHeaderText", "Text antet panou", GApp, "#5F5F5F"),
			new ColorSetting("Border", "Contururi", GApp, "#E5E5E5"),
			new ColorSetting("Text", "Text", GApp, "#1B1B1B"),
			new ColorSetting("TextSecondary", "Text secundar", GApp, "#5F5F5F"),
			new ColorSetting("Hover", "Sub mouse (hover)", GApp, "#0A000000", "Poate fi semitransparent: #AARRGGBB."),
			new ColorSetting("SelectionBg", "Selecție arbore", GApp, "#170067C0"),
			new ColorSetting("SelectionText", "Text selecție", GApp, "#1B1B1B"),
			new ColorSetting("TabStripBg", "Fundal bară taburi", GApp, "#FBFBFB"),
			new ColorSetting("TabActiveBg", "Tab activ", GApp, "#FFFFFF"),
			new ColorSetting("TabActiveText", "Text tab activ", GApp, "#1B1B1B"),
			new ColorSetting("TabText", "Text tab inactiv", GApp, "#5F5F5F"),
			new ColorSetting("InputBg", "Câmpuri (căutare, liste)", GApp, "#FFFFFF"),
			new ColorSetting("PopupBg", "Meniuri / popup", GApp, "#F9F9F9"),
			new ColorSetting("ScrollThumb", "Bară de derulare", GApp, "#8A8A8A"),
			new ColorSetting("StatusBarBg", "Bara de stare", GApp, "#F3F3F3"),
			new ColorSetting("StatusBarText", "Text bara de stare", GApp, "#5F5F5F"),

			// ---- Culori editor ----
			new ColorSetting("EditorBg", "Fundal editor", GEditor, "#FFFFFF"),
			new ColorSetting("EditorText", "Text", GEditor, "#1B1B1B"),
			new ColorSetting("LineNumber", "Numere de linie", GEditor, "#8A8A8A"),
			new ColorSetting("CurrentLine", "Linia curentă", GEditor, "#F3F3F3"),
			new ColorSetting("EditorSelection", "Selecție", GEditor, "#ADD6FF"),
			new ColorSetting("SyntaxKeyword", "Cuvinte cheie (public, class)", GEditor, "#0000FF"),
			new ColorSetting("SyntaxControl", "Control (if, return)", GEditor, "#8F08C4"),
			new ColorSetting("SyntaxType", "Tipuri", GEditor, "#2B91AF"),
			new ColorSetting("SyntaxMethod", "Metode", GEditor, "#74531F"),
			new ColorSetting("SyntaxLocal", "Parametri / variabile", GEditor, "#1F377F"),
			new ColorSetting("SyntaxString", "Șiruri", GEditor, "#A31515"),
			new ColorSetting("SyntaxNumber", "Numere", GEditor, "#098658"),
			new ColorSetting("SyntaxComment", "Comentarii", GEditor, "#008000"),
		};

		#endregion
	}
}
