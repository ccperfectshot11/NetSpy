using System.Windows;
using System.Windows.Media;

namespace NetSpy.Designer.Controls
{
	/// <summary>
	/// local:RoundClip.Radius="8" taie conținutul unui element după un dreptunghi rotunjit
	/// (Border.CornerRadius rotunjește doar chenarul, nu și ce e înăuntru).
	/// </summary>
	public static class RoundClip
	{
		public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
			"Radius", typeof(double), typeof(RoundClip), new PropertyMetadata(0.0, OnRadiusChanged));

		public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
		public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

		static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is not FrameworkElement element)
				return;
			element.SizeChanged -= OnSizeChanged;
			element.SizeChanged += OnSizeChanged;
			Update(element);
		}

		static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);

		static void Update(FrameworkElement element)
		{
			double r = GetRadius(element);
			element.Clip = r <= 0
				? null
				: new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), r, r);
		}
	}
}
