using System;
using System.Globalization;
using System.Windows.Media;

namespace NetSpy.Designer.Model
{
	public static class ColorUtil
	{
		/// <summary>Acceptă "#RGB", "#RRGGBB" și "#AARRGGBB" (AA = transparența, FF = opac).</summary>
		public static bool TryParse(string? text, out Color color)
		{
			color = default;
			if (string.IsNullOrWhiteSpace(text))
				return false;
			var hex = text.Trim().TrimStart('#');
			if (hex.Length == 3)
				hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
			if (hex.Length == 6)
				hex = "FF" + hex;
			if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
				return false;
			color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
			return true;
		}

		public static Color Parse(string? text, Color fallback) => TryParse(text, out var c) ? c : fallback;

		public static string ToHex(Color c) =>
			c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

		/// <summary>Luminanța relativă (0 = negru, 1 = alb), după formula WCAG.</summary>
		public static double Luminance(Color c)
		{
			static double Channel(byte v)
			{
				double s = v / 255.0;
				return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
			}
			return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
		}

		/// <summary>RGB → HSV: nuanța 0..360, saturația și valoarea 0..1.</summary>
		public static (double H, double S, double V) ToHsv(Color c)
		{
			double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
			double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
			double delta = max - min;
			double h = 0;
			if (delta > 0)
			{
				if (max == r)
					h = 60 * (((g - b) / delta) % 6);
				else if (max == g)
					h = 60 * ((b - r) / delta + 2);
				else
					h = 60 * ((r - g) / delta + 4);
			}
			if (h < 0)
				h += 360;
			return (h, max == 0 ? 0 : delta / max, max);
		}

		public static Color FromHsv(double h, double s, double v, byte alpha = 255)
		{
			h = ((h % 360) + 360) % 360;
			double c = v * s;
			double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
			double m = v - c;
			(double r, double g, double b) = (int)(h / 60) switch {
				0 => (c, x, 0.0),
				1 => (x, c, 0.0),
				2 => (0.0, c, x),
				3 => (0.0, x, c),
				4 => (x, 0.0, c),
				_ => (c, 0.0, x),
			};
			return Color.FromArgb(alpha, ToByte(r + m), ToByte(g + m), ToByte(b + m));
		}

		static byte ToByte(double d) => (byte)Math.Round(Math.Clamp(d, 0, 1) * 255);
	}
}
