using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace NetSpy.Designer.Controls
{
	/// <summary>
	/// Încarcă iconițele SVG ale NetSpy (Icons\*.svg). Sunt iconițe din pachetul Visual Studio,
	/// desenate cu o paletă fixă; pentru fundal închis înlocuim culorile paletei (ca Visual Studio).
	/// </summary>
	public static class IconCache
	{
		static readonly Dictionary<(string, bool), ImageSource?> cache = new();
		static bool dark;

		// culoare pe fundal deschis → culoare pe fundal închis
		static readonly Dictionary<string, string> DarkPalette = new(StringComparer.OrdinalIgnoreCase) {
			["#f6f6f6"] = "#252526", // conturul (se confundă cu fundalul)
			["#424242"] = "#C5C5C5", // desenul principal
			["#f0eff1"] = "#2B282E", // interiorul
			["#efeef0"] = "#2B282E",
			["#090909"] = "#F0F0F0",
			// culorile „de accent” ale iconițelor sunt prea închise pe fundal închis: le luminăm
			["#00539c"] = "#4DAAFF", // albastru (Back, Save, Reload, metode)
			["#0000ff"] = "#4DAAFF",
			["#652d90"] = "#B180D7", // violet (metode, delegate)
			["#a1260d"] = "#F48771", // roșu închis
			["#e51400"] = "#F14C4C", // roșu (Delete, Warning)
			["#388a34"] = "#89D185", // verde
			["#218022"] = "#89D185",
			["#c27d1a"] = "#E8A33D", // portocaliu (clase)
		};

		static readonly Regex HexColor = new("#[0-9a-fA-F]{6}\\b", RegexOptions.Compiled);

		public static bool Dark
		{
			get => dark;
			set
			{
				if (dark == value)
					return;
				dark = value;
				ThemeChanged?.Invoke(null, EventArgs.Empty);
			}
		}

		public static event EventHandler? ThemeChanged;

		public static ImageSource? Get(string name)
		{
			var key = (name, dark);
			if (!cache.TryGetValue(key, out var image))
			{
				image = Load(name, dark);
				cache[key] = image;
			}
			return image;
		}

		static ImageSource? Load(string name, bool dark)
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Icons", name + ".svg");
			if (!File.Exists(path))
				return null;
			var svg = File.ReadAllText(path);
			if (dark)
				svg = HexColor.Replace(svg, m => DarkPalette.TryGetValue(m.Value, out var replacement) ? replacement : m.Value);
			// fill:#fff scurt apare și el în câteva iconițe (textul "GAC" etc.) – îl lăsăm alb
			var settings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true };
			var reader = new FileSvgReader(settings);
			using var text = new StringReader(svg);
			var drawing = reader.Read(text);
			if (drawing is null)
				return null;
			var image = new DrawingImage(drawing);
			image.Freeze();
			return image;
		}
	}

	/// <summary>&lt;c:SvgIcon Icon="Class"/&gt; – o iconiță NetSpy care se recolorează singură la schimbarea temei.</summary>
	public sealed class SvgIcon : Image
	{
		public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
			nameof(Icon), typeof(string), typeof(SvgIcon), new PropertyMetadata(null, (d, _) => ((SvgIcon)d).Refresh()));

		public SvgIcon()
		{
			Stretch = Stretch.Uniform;
			SnapsToDevicePixels = true;
			RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
			Loaded += (_, _) => { IconCache.ThemeChanged += OnThemeChanged; Refresh(); };
			Unloaded += (_, _) => IconCache.ThemeChanged -= OnThemeChanged;
		}

		public string? Icon
		{
			get => (string?)GetValue(IconProperty);
			set => SetValue(IconProperty, value);
		}

		void OnThemeChanged(object? sender, EventArgs e) => Refresh();

		void Refresh() => Source = Icon is { } name ? IconCache.Get(name) : null;
	}
}
