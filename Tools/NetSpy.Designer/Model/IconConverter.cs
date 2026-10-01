using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace NetSpy.Designer.Model
{
	/// <summary>
	/// Pune iconițele NetSpy (Icons\*.svg) în NetSpy: fiecare iconiță NetSpy din arbore (ex. ClassPrivate)
	/// devine un fișier vectorial NetSpy\NetSpy\Images\NetSpy\ClassPrivate.xaml = iconița NetSpy de bază (Class.svg)
	/// + suprapunerea de acces (OverlayPrivate.svg), exact cum le compune NetSpy. Culorile rămân cele pentru
	/// fundal deschis: NetSpy le recolorează singur pe fundal închis (ThemedImageCreator, ca Visual Studio).
	/// </summary>
	public static class IconConverter
	{
		// tipurile care au variante de acces în NetSpy: nume NetSpy -> iconiță NetSpy
		static readonly Dictionary<string, string> Kinds = new() {
			["Class"] = "Class",
			["Interface"] = "Interface",
			["Structure"] = "Struct",
			["Enumeration"] = "Enum",
			["EnumerationItem"] = "EnumValue",
			["Delegate"] = "Delegate",
			["Method"] = "Method",
			["Field"] = "Field",
			["Constant"] = "Literal",
			["Event"] = "Event",
			["Property"] = "Property",
			["Operator"] = "Operator",
		};

		// sufixul NetSpy -> suprapunerea NetSpy (null = fără)
		static readonly Dictionary<string, string?> Access = new() {
			["Public"] = null,
			["Internal"] = "OverlayInternal",
			["Private"] = "OverlayPrivate",
			["Protected"] = "OverlayProtected",
			["Sealed"] = null,
			["Shortcut"] = "ReferenceOverlay",
		};

		// iconițe fără variante
		static readonly Dictionary<string, string[]> Singles = new() {
			["Assembly"] = new[] { "Assembly" },
			["AssemblyExe"] = new[] { "Assembly" },
			["AssemblyError"] = new[] { "AssemblyWarning" },
			["Namespace"] = new[] { "Namespace" },
			["ExtensionMethod"] = new[] { "ExtensionMethod" },
			["FolderClosed"] = new[] { "FolderClosed" },
			["FolderOpened"] = new[] { "FolderOpen" },
			["Library"] = new[] { "Library" },
			["Reference"] = new[] { "Assembly" },
			["ModulePublic"] = new[] { "Library" },
			["ModuleInternal"] = new[] { "Library", "OverlayInternal" },
			["ModulePrivate"] = new[] { "Library", "OverlayPrivate" },
			["ModuleProtected"] = new[] { "Library", "OverlayProtected" },
			["Property"] = new[] { "Property" },
		};

		public static IEnumerable<(string Target, string[] Layers)> Mapping()
		{
			foreach (var (kind, icon) in Kinds)
			{
				foreach (var (access, overlay) in Access)
				{
					var layers = overlay is null ? new[] { icon } : new[] { icon, overlay };
					yield return (kind + access, layers);
				}
			}
			foreach (var (target, layers) in Singles)
				yield return (target, layers);
		}

		/// <summary>
		/// Folderul NetSpy\NetSpy\Images\NetSpy al NetSpy. (NetSpy.Images e un submodul al NetSpyEx: nu-l modificăm;
		/// NetSpy are un furnizor de imagini care le dă pe acestea înaintea celor din NetSpy.Images.)
		/// </summary>
		public static string? FindImagesDirectory()
		{
			var themes = ThemeExporter.FindThemesDirectory();
			return themes is null ? null : Path.GetFullPath(Path.Combine(themes, "..", "Images", "NetSpy"));
		}

		/// <returns>câte iconițe au fost scrise</returns>
		public static int Convert(string iconsDirectory, string imagesDirectory)
		{
			int count = 0;
			Directory.CreateDirectory(imagesDirectory);
			foreach (var (target, layers) in Mapping())
			{
				var group = new DrawingGroup();
				foreach (var layer in layers)
				{
					var path = Path.Combine(iconsDirectory, layer + ".svg");
					if (!File.Exists(path))
						throw new FileNotFoundException("Lipsește iconița NetSpy", path);
					var reader = new FileSvgReader(new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true });
					var drawing = reader.Read(path) ?? throw new InvalidDataException("SVG invalid: " + path);
					group.Children.Add(drawing);
				}
				// toate SVG-urile NetSpy au viewBox 0 0 16 16: fixăm zona de desen la 16x16 (un dreptunghi transparent)
				group.Children.Insert(0, new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
				group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 16, 16));

				var viewbox = new Viewbox {
					Width = 16,
					Height = 16,
					Stretch = Stretch.Uniform,
					Child = new Image { Source = new DrawingImage(group), Width = 16, Height = 16 },
				};
				var xaml = XamlWriter.Save(viewbox);
				File.WriteAllText(Path.Combine(imagesDirectory, target + ".xaml"),
					"<!-- Generat din iconițele NetSpy (MIT) de NetSpy Designer: " + string.Join(" + ", layers) + " -->\r\n" + xaml);
				count++;
			}
			return count;
		}
	}
}
