using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using NetSpy.Designer.Model;

namespace NetSpy.Designer.Controls
{
	/// <summary>Mostră de culoare + câmp hex; click pe mostră deschide selectorul HSV cu transparență.</summary>
	public partial class ColorBox : UserControl
	{
		public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
			nameof(Hex), typeof(string), typeof(ColorBox),
			new FrameworkPropertyMetadata("#000000", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorBox)d).OnHexChanged()));

		static readonly string[] QuickColors = {
			"#0067C0", "#005FB8", "#4CC2FF", "#3399FF", "#007ACC", "#0E639C", "#038387", "#107C10", "#6CCB5F", "#FFB900",
			"#CA5010", "#C42B1C", "#E3008C", "#8764B8", "#A371F7", "#7A7574", "#FFFFFF", "#F3F3F3", "#E5E5E5", "#FBFBFB",
			"#C5C5C5", "#8A8A8A", "#5F5F5F", "#333333", "#2B2B2B", "#272727", "#202020", "#1B1B1B", "#0D1117", "#000000",
		};

		double hue, saturation, value;
		byte alpha = 255;
		bool updatingFromPicker;

		public ColorBox()
		{
			InitializeComponent();
			Swatches.ItemsSource = QuickColors.Select(h => new SolidColorBrush(ColorUtil.Parse(h, Colors.Black))).ToList();
			PickerPopup.Opened += (_, _) => UpdateThumbs();
			OnHexChanged();
		}

		public string Hex
		{
			get => (string)GetValue(HexProperty);
			set => SetValue(HexProperty, value);
		}

		Color CurrentColor => ColorUtil.FromHsv(hue, saturation, value, alpha);

		void OnHexChanged()
		{
			if (!ColorUtil.TryParse(Hex, out var color))
				return;
			if (!updatingFromPicker)
			{
				var (h, s, v) = ColorUtil.ToHsv(color);
				// fără saturație (gri) sau fără lumină (negru) nuanța nu contează: o păstrăm pe cea veche
				if (s > 0 && v > 0)
					hue = h;
				saturation = s;
				value = v;
				alpha = color.A;
			}
			SwatchColor.Background = new SolidColorBrush(color);
			UpdateThumbs();
		}

		void Commit()
		{
			updatingFromPicker = true;
			try
			{
				Hex = ColorUtil.ToHex(CurrentColor);
			}
			finally
			{
				updatingFromPicker = false;
			}
		}

		void UpdateThumbs()
		{
			HueFill.Fill = new SolidColorBrush(ColorUtil.FromHsv(hue, 1, 1));
			var opaque = ColorUtil.FromHsv(hue, saturation, value);
			AlphaFill.Fill = new LinearGradientBrush(Color.FromArgb(0, opaque.R, opaque.G, opaque.B), opaque, 0);

			Place(SvThumb, saturation * SvArea.ActualWidth, (1 - value) * SvArea.ActualHeight);
			Place(HueThumb, hue / 360 * HueBar.ActualWidth, HueBar.ActualHeight / 2);
			Place(AlphaThumb, alpha / 255.0 * AlphaBar.ActualWidth, AlphaBar.ActualHeight / 2);
		}

		static void Place(FrameworkElement thumb, double x, double y)
		{
			Canvas.SetLeft(thumb, x - thumb.Width / 2);
			Canvas.SetTop(thumb, y - thumb.Height / 2);
		}

		static double Fraction(double position, double size) => size <= 0 ? 0 : Math.Clamp(position / size, 0, 1);

		void Swatch_Click(object sender, MouseButtonEventArgs e) => PickerPopup.IsOpen = true;

		void HexBox_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Enter)
				HexBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
		}

		void QuickSwatch_Click(object sender, MouseButtonEventArgs e)
		{
			if (((FrameworkElement)sender).DataContext is SolidColorBrush brush)
				Hex = ColorUtil.ToHex(brush.Color);
		}

		#region Tragere cu mouse-ul

		void Sv_MouseDown(object sender, MouseButtonEventArgs e) { SvArea.CaptureMouse(); SetSv(e.GetPosition(SvArea)); }
		void Sv_MouseMove(object sender, MouseEventArgs e) { if (SvArea.IsMouseCaptured) SetSv(e.GetPosition(SvArea)); }

		void SetSv(Point p)
		{
			saturation = Fraction(p.X, SvArea.ActualWidth);
			value = 1 - Fraction(p.Y, SvArea.ActualHeight);
			Commit();
			UpdateThumbs();
		}

		void Hue_MouseDown(object sender, MouseButtonEventArgs e) { HueBar.CaptureMouse(); SetHue(e.GetPosition(HueBar)); }
		void Hue_MouseMove(object sender, MouseEventArgs e) { if (HueBar.IsMouseCaptured) SetHue(e.GetPosition(HueBar)); }

		void SetHue(Point p)
		{
			hue = Fraction(p.X, HueBar.ActualWidth) * 360;
			Commit();
			UpdateThumbs();
		}

		void Alpha_MouseDown(object sender, MouseButtonEventArgs e) { AlphaBar.CaptureMouse(); SetAlpha(e.GetPosition(AlphaBar)); }
		void Alpha_MouseMove(object sender, MouseEventArgs e) { if (AlphaBar.IsMouseCaptured) SetAlpha(e.GetPosition(AlphaBar)); }

		void SetAlpha(Point p)
		{
			alpha = (byte)Math.Round(Fraction(p.X, AlphaBar.ActualWidth) * 255);
			Commit();
			UpdateThumbs();
		}

		void Area_MouseUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

		#endregion
	}
}
