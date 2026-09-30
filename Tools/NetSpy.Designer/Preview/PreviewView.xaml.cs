using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

using NetSpy.Designer.Controls;
using NetSpy.Designer.Model;

namespace NetSpy.Designer.Preview
{
	/// <summary>Previzualizarea ferestrei NetSpy; toate culorile/razele vin din resursele "NS.*" scrise de <see cref="DesignDocument"/>.</summary>
	public partial class PreviewView : UserControl
	{
		// cuvântul selectat (și linia curentă) din fiecare exemplu de cod
		static readonly string[] Highlights = { "SerializeObject", "IsPrimitiveToken", "SerializeObject" };

		readonly List<(TreeViewItem Item, int Depth)> treeItems = new();
		readonly List<Button> menuButtons = new();
		readonly StackPanel menuPanel = new() { Orientation = Orientation.Horizontal };
		DesignDocument? document;
		int currentLineIndex;

		public PreviewView()
		{
			InitializeComponent();
			BuildMenu();
			BuildToolBar();
			BuildTree();
			BuildAnalyzer();
			BuildTabs();
			BuildMenuPopup();
			SizeChanged += (_, _) => PositionMenu();
		}

		public void Attach(DesignDocument doc)
		{
			document = doc;
			doc.Changed += (_, _) => Apply();
			Apply();
		}

		/// <summary>Aplică designul: resurse + lucrurile care schimbă structura (poziții, stiluri).</summary>
		void Apply()
		{
			if (document is not { } doc)
				return;
			doc.ApplyTo(Resources);
			IconCache.Dark = doc.UseDarkIcons;

			// bara de titlu: meniul stă în ea (integrată) sau pe un rând separat (clasică)
			bool integrated = doc["TitleBarStyle"] == "Integrated";
			TitleMenuHost.Content = null;
			ClassicMenuHost.Content = null;
			if (integrated)
				TitleMenuHost.Content = menuPanel;
			else
				ClassicMenuHost.Content = menuPanel;
			ClassicMenuRow.Visibility = integrated ? Visibility.Collapsed : Visibility.Visible;
			TitleBar.Height = integrated ? 40 : 32;
			foreach (var button in menuButtons)
				button.SetResourceReference(ForegroundProperty, integrated ? "NS.TitleBarText" : "NS.Text");

			// taburi
			string tabStyle = doc["TabStyle"];
			Resources["NS.TabItemStyle"] = FindResource("Tab" + tabStyle);
			if (tabStyle == "Classic")
			{
				// Visual Studio: o linie groasă în culoarea tabului activ sub toate taburile
				TabStrip.BorderThickness = new Thickness(0, 0, 0, 2);
				TabStrip.SetResourceReference(Border.BorderBrushProperty, "NS.TabActiveBg");
			}
			else
			{
				TabStrip.BorderThickness = new Thickness(0, 0, 0, 1);
				TabStrip.SetResourceReference(Border.BorderBrushProperty, "NS.Border");
			}

			// așezarea panourilor
			double gap = doc.Number("PanelGap");
			double treeWidth = doc.Number("TreeWidth");
			bool treeLeft = doc["TreePosition"] == "Left";
			Col0.Width = treeLeft ? new GridLength(treeWidth) : new GridLength(1, GridUnitType.Star);
			Col1.Width = new GridLength(gap);
			Col2.Width = treeLeft ? new GridLength(1, GridUnitType.Star) : new GridLength(treeWidth);
			int editorColumn = treeLeft ? 2 : 0;
			Grid.SetColumn(TreeCard, treeLeft ? 0 : 2);
			Grid.SetColumn(EditorCard, editorColumn);
			Grid.SetColumn(BottomCard, editorColumn);
			bool bottom = doc.Bool("ShowBottomPanel");
			BottomGapRow.Height = new GridLength(bottom ? gap : 0);
			BottomRow.Height = new GridLength(bottom ? 170 : 0);
			Body.Margin = new Thickness(gap, gap > 0 ? gap / 2 : 0, gap, gap);

			// indentarea arborelui depinde de mărimea iconițelor
			double indent = (double)Resources["NS.TreeIndent"];
			foreach (var (item, depth) in treeItems)
				item.Padding = new Thickness(depth * indent, 0, 0, 0);

			UpdateCurrentLine();
			Dispatcher.BeginInvoke(PositionMenu, DispatcherPriority.Loaded);
		}

		#region Meniu

		void BuildMenu()
		{
			foreach (var name in new[] { "File", "Edit", "View", "Debug", "Window", "Help" })
			{
				var button = new Button { Content = name, Style = (Style)FindResource("PvMenuButton") };
				menuButtons.Add(button);
				menuPanel.Children.Add(button);
			}
		}

		void BuildMenuPopup()
		{
			var rows = new (string? Icon, string Text, string? Shortcut, bool Submenu)[] {
				("Open|", "Open…", "Ctrl+O", false),
				("AssemblyListGAC|", "Open from GAC…", null, false),
				("AssemblyList|", "Open List…", null, false),
				("Refresh|", "Reload All Assemblies", null, false),
				(null, "-", null, false),
				("Save|", "Save Code…", "Ctrl+S", false),
				(null, "Export to Project…", null, false),
				(null, "-", null, false),
				(null, "Recent Files", null, true),
				(null, "-", null, false),
				(null, "Exit", "Alt+F4", false),
			};
			foreach (var (icon, text, shortcut, submenu) in rows)
			{
				if (text == "-")
				{
					var separator = new Border { Height = 1, Margin = new Thickness(12, 4, 12, 4) };
					separator.SetResourceReference(Border.BackgroundProperty, "NS.Border");
					MenuRows.Children.Add(separator);
					continue;
				}
				var grid = new Grid();
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
				grid.ColumnDefinitions.Add(new ColumnDefinition());
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				if (icon?.Split('|') is [var svg, var glyph])
				{
					var buttonIcon = (FrameworkElement)ButtonIcon(svg, glyph, 16);
					buttonIcon.HorizontalAlignment = HorizontalAlignment.Left;
					grid.Children.Add(buttonIcon);
				}
				var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
				Grid.SetColumn(label, 1);
				grid.Children.Add(label);
				if (shortcut is not null || submenu)
				{
					var right = new TextBlock {
						Text = submenu ? "" : shortcut,
						FontSize = submenu ? 10 : 12,
						VerticalAlignment = VerticalAlignment.Center,
						Margin = new Thickness(24, 0, 0, 0),
					};
					if (submenu)
						right.FontFamily = (FontFamily)FindResource("IconFont");
					right.SetResourceReference(TextBlock.ForegroundProperty, "NS.TextSecondary");
					Grid.SetColumn(right, 2);
					grid.Children.Add(right);
				}
				MenuRows.Children.Add(new Border { Style = (Style)FindResource("PvMenuRow"), Child = grid });
			}
		}

		void PositionMenu()
		{
			var file = menuButtons[0];
			bool open = document?.Bool("ShowMenuOpen") == true;
			if (open)
				file.SetResourceReference(BackgroundProperty, "NS.Hover");
			else
				file.ClearValue(BackgroundProperty);
			if (!open || !file.IsVisible)
				return;
			var p = file.TranslatePoint(new Point(0, file.ActualHeight + 4), FrameContent);
			Canvas.SetLeft(MenuPopup, p.X);
			Canvas.SetTop(MenuPopup, p.Y);
		}

		#endregion

		#region Bara de unelte

		void BuildToolBar()
		{
			AddButton("Back", "", "Back (Alt+Left)");
			AddButton("Forward", "", "Forward (Alt+Right)");
			AddSeparator();
			AddButton("Open", "", "Open… (Ctrl+O)");
			AddButton("Refresh", "", "Reload (F5)");
			AddButton("Save", "", "Save Code (Ctrl+S)");
			AddSeparator();
			AddToggle("ShowPublicOnly", "", "Show public members only", false);
			AddToggle("ShowPrivateInternal", "", "Show internal and private members", true);
			AddToggle("ShowAll", "", "Show all members (incl. compiler-generated)", false);
			AddSeparator();
			AddButton("Sort", "", "Sort assembly list");
			AddButton("CollapseAll", "", "Collapse all tree nodes");
			AddSeparator();

			// lista de limbaje (desenată ca un ComboBox WinUI)
			var combo = new Border {
				Height = 30,
				Padding = new Thickness(10, 0, 10, 0),
				Margin = new Thickness(4, 0, 0, 0),
				BorderThickness = new Thickness(1),
			};
			combo.SetResourceReference(Border.CornerRadiusProperty, "NS.ControlCorner");
			combo.SetResourceReference(Border.BackgroundProperty, "NS.InputBg");
			combo.SetResourceReference(Border.BorderBrushProperty, "NS.Border");
			var content = new StackPanel { Orientation = Orientation.Horizontal };
			content.Children.Add(new TextBlock { Text = "C#", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
			var version = new TextBlock { Text = "  14.0 / VS 2026", VerticalAlignment = VerticalAlignment.Center };
			version.SetResourceReference(TextBlock.ForegroundProperty, "NS.TextSecondary");
			content.Children.Add(version);
			var chevron = new TextBlock {
				Text = "",
				FontFamily = (FontFamily)FindResource("IconFont"),
				FontSize = 10,
				Margin = new Thickness(14, 0, 0, 0),
				VerticalAlignment = VerticalAlignment.Center,
			};
			content.Children.Add(chevron);
			combo.Child = content;
			ToolBarButtons.Children.Add(combo);
		}

		/// <summary>Iconița unui buton în ambele stiluri: SVG colorat (clasic) și glifă Segoe Fluent Icons (Windows 11).
		/// Resursele NS.ClassicIcons / NS.FluentIcons.Visibility o arată doar pe cea aleasă.</summary>
		UIElement ButtonIcon(string icon, string glyph, double size)
		{
			var classic = new SvgIcon { Icon = icon, Width = size, Height = size };
			classic.SetResourceReference(VisibilityProperty, "NS.ClassicIcons.Visibility");
			var fluent = new TextBlock {
				Text = glyph,
				FontFamily = (FontFamily)FindResource("IconFont"),
				FontSize = size,
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Center,
			};
			fluent.SetResourceReference(VisibilityProperty, "NS.FluentIcons.Visibility");
			var grid = new Grid { Width = size + 2, Height = size + 2 };
			grid.Children.Add(classic);
			grid.Children.Add(fluent);
			return grid;
		}

		void AddButton(string icon, string glyph, string tooltip) =>
			ToolBarButtons.Children.Add(new Button {
				Style = (Style)FindResource("PvButton"),
				Content = ButtonIcon(icon, glyph, 16),
				ToolTip = tooltip,
			});

		void AddToggle(string icon, string glyph, string tooltip, bool isChecked) =>
			ToolBarButtons.Children.Add(new ToggleButton {
				Style = (Style)FindResource("PvToggle"),
				Content = ButtonIcon(icon, glyph, 16),
				ToolTip = tooltip,
				IsChecked = isChecked,
			});

		void AddSeparator()
		{
			var separator = new Border { Width = 1, Height = 18, Margin = new Thickness(6, 0, 6, 0) };
			separator.SetResourceReference(Border.BackgroundProperty, "NS.Border");
			ToolBarButtons.Children.Add(separator);
		}

		#endregion

		#region Arbore

		TreeViewItem Node(string icon, string text, string? detail, int depth, bool expanded = false, params TreeViewItem[] children)
		{
			// DockPanel (nu StackPanel): textul primește doar lățimea rămasă, deci poate fi scurtat cu „…”
			var header = new DockPanel();
			var image = new SvgIcon { Icon = icon, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
			image.SetResourceReference(WidthProperty, "NS.IconSizeValue");
			image.SetResourceReference(HeightProperty, "NS.IconSizeValue");
			DockPanel.SetDock(image, Dock.Left);
			header.Children.Add(image);
			var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
			label.Inlines.Add(new Run(text));
			if (detail is not null)
			{
				var detailRun = new Run(" " + detail);
				detailRun.SetResourceReference(TextElement.ForegroundProperty, "NS.TextSecondary");
				label.Inlines.Add(detailRun);
			}
			header.Children.Add(label);

			var item = new TreeViewItem { Header = header, Style = (Style)FindResource("PvTreeItem"), IsExpanded = expanded };
			foreach (var child in children)
				item.Items.Add(child);
			treeItems.Add((item, depth));
			return item;
		}

		void BuildTree()
		{
			var serialize = Node("Method", "SerializeObject(object, Formatting)", ": string", 3);
			Tree.Items.Add(Node("Assembly", "Newtonsoft.Json", "(13.0.0.0, .NETStandard 2.0)", 0, true,
				Node("ReferenceFolder", "References", null, 1, false,
					Node("Assembly", "netstandard", "(2.0.0.0)", 2)),
				Node("Namespace", "Newtonsoft.Json", null, 1, true,
					Node("Enum", "Formatting", null, 2, false,
						Node("EnumValue", "None", ": 0", 3),
						Node("EnumValue", "Indented", ": 1", 3)),
					Node("Class", "JsonConvert", null, 2, true,
						Node("SuperTypes", "Base Types", null, 3, false,
							Node("Class", "object", null, 4)),
						Node("FieldReadOnly", "True", ": string", 3),
						Node("Property", "DefaultSettings", ": Func<JsonSerializerSettings>", 3),
						serialize,
						Node("Method", "DeserializeObject<T>(string)", ": T", 3),
						Node("Method", "ToString(DateTime)", ": string", 3)),
					Node("Class", "JsonSerializer", null, 2, false,
						Node("Constructor", "JsonSerializer()", null, 3),
						Node("Event", "Error", ": EventHandler<ErrorEventArgs>", 3)),
					Node("Interface", "IArrayPool<T>", null, 2, false,
						Node("Method", "Rent(int)", ": T[]", 3)),
					Node("Struct", "JsonPosition", null, 2, false,
						Node("Field", "Position", ": int", 3)),
					Node("Delegate", "SerializationCallback", null, 2, false,
						Node("Method", "Invoke(object, StreamingContext)", ": void", 3)),
					Node("Enum", "JsonToken", null, 2, false,
						Node("EnumValue", "None", ": 0", 3))),
				Node("Namespace", "Newtonsoft.Json.Linq", null, 1, false,
					Node("Class", "JObject", null, 2)),
				Node("Namespace", "Newtonsoft.Json.Serialization", null, 1, false,
					Node("Class", "DefaultContractResolver", null, 2))));
			Tree.Items.Add(Node("Assembly", "System.Private.CoreLib", "(10.0.0.0)", 0, false,
				Node("Namespace", "System", null, 1)));
			Tree.Items.Add(Node("Assembly", "AllFeatures", "(1.0.0.0, .NET 10.0)", 0, false,
				Node("Namespace", "AllFeatures", null, 1)));
			serialize.IsSelected = true;
		}

		void BuildAnalyzer()
		{
			AnalyzerTree.Items.Add(Node("Method", "SerializeObject(object, Formatting)", ": string", 0, true,
				Node("Search", "Used By", null, 1, true,
					Node("Method", "JsonConvert.SerializeObject(object)", ": string", 2),
					Node("Method", "Program.<Main>$(string[])", ": void", 2)),
				Node("Search", "Uses", null, 1, false,
					Node("Method", "JsonSerializer.Create(JsonSerializerSettings)", null, 2))));
		}

		#endregion

		#region Taburi și cod

		void BuildTabs()
		{
			var samples = CodeSamples.All;
			for (int i = 0; i < samples.Count; i++)
			{
				int index = i;
				var header = new StackPanel { Orientation = Orientation.Horizontal };
				header.Children.Add(new SvgIcon { Icon = samples[i].Icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 7, 0) });
				header.Children.Add(new TextBlock { Text = samples[i].Title, VerticalAlignment = VerticalAlignment.Center });
				header.Children.Add(new TextBlock {
					Text = "",
					FontFamily = (FontFamily)FindResource("IconFont"),
					FontSize = 9,
					Opacity = 0.7,
					Margin = new Thickness(12, 0, 0, 0),
					VerticalAlignment = VerticalAlignment.Center,
				});
				var tab = new RadioButton { GroupName = "PreviewTabs", Content = header };
				tab.SetResourceReference(StyleProperty, "NS.TabItemStyle");
				tab.Checked += (_, _) => ShowSample(index);
				Tabs.Children.Add(tab);
			}
			((RadioButton)Tabs.Children[0]).IsChecked = true;
		}

		void ShowSample(int index)
		{
			var lines = CodeSamples.Colorize(CodeSamples.All[index].Code);
			Code.Inlines.Clear();
			bool highlighted = false;
			currentLineIndex = 0;
			for (int i = 0; i < lines.Count; i++)
			{
				foreach (var token in lines[i])
				{
					var run = new Run(token.Text);
					if (token.Color is not null)
						run.SetResourceReference(TextElement.ForegroundProperty, "NS." + token.Color);
					if (!highlighted && token.Text == Highlights[index])
					{
						highlighted = true;
						currentLineIndex = i;
						run.SetResourceReference(TextElement.BackgroundProperty, "NS.EditorSelection");
					}
					Code.Inlines.Add(run);
				}
				if (i < lines.Count - 1)
					Code.Inlines.Add(new LineBreak());
			}
			LineNumbers.Text = string.Join("\n", Enumerable.Range(1, lines.Count));
			UpdateCurrentLine();
		}

		void UpdateCurrentLine()
		{
			if (Resources["NS.CodeLineHeight"] is double lineHeight)
				CurrentLine.Margin = new Thickness(0, currentLineIndex * lineHeight, 0, 0);
		}

		#endregion
	}
}
