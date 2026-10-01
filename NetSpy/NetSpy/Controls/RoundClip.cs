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

using System.Windows;
using System.Windows.Media;

namespace NetSpy.Controls {
	/// <summary>
	/// ctrls:RoundClip.Radius="8" clips an element to a rounded rectangle (Border.CornerRadius only
	/// rounds the border, not its children). Used by the NetSpy card panels.
	/// </summary>
	static class RoundClip {
		public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
			"Radius", typeof(double), typeof(RoundClip), new PropertyMetadata(0.0, OnRadiusChanged));

		public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
		public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

		static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
			if (d is not FrameworkElement element)
				return;
			element.SizeChanged -= OnSizeChanged;
			element.SizeChanged += OnSizeChanged;
			Update(element);
		}

		static void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender!);

		static void Update(FrameworkElement element) {
			double r = GetRadius(element);
			element.Clip = r <= 0 ? null : new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), r, r);
		}
	}
}
