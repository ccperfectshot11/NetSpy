/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

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
using System.Windows.Input;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.MVVM;

namespace NetSpy.ToolBars {
	sealed class ToolBarButtonVM : ViewModelBase {
		public ICommand Command { get; }
		public IInputElement? CommandTarget { get; }
		public string? Header { get; }
		public bool HasHeader => !string.IsNullOrEmpty(Header);
		public string? ToolTip { get; }
		public ImageReference ImageReference { get; }

		public ToolBarButtonVM(ICommand command, IInputElement? commandTarget, string? header, string? toolTip, ImageReference? imageReference) {
			Command = command;
			CommandTarget = commandTarget;
			Header = string.IsNullOrEmpty(header) ? null : header;
			ToolTip = string.IsNullOrEmpty(toolTip) ? null : toolTip;
			ImageReference = imageReference ?? default;
			if (FluentGlyphs.Get(ImageReference) is var (glyph, size)) {
				Glyph = glyph;
				GlyphSize = size;
			}
		}

		/// <summary>Segoe Fluent Icons glyph shown instead of <see cref="ImageReference"/> (NetSpy design)</summary>
		public string? Glyph { get; }
		public double GlyphSize { get; } = 16;
		public bool HasGlyph => Glyph is not null;
	}
}
