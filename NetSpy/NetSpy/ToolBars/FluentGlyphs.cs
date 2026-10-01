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

using System.Collections.Generic;
using NetSpy.Contracts.Images;
using NetSpy.Themes;

namespace NetSpy.ToolBars {
	/// <summary>
	/// Segoe Fluent Icons (the Windows 11 icon font) glyph for a toolbar image.
	/// Images without a glyph keep their colored image.
	/// </summary>
	static class FluentGlyphs {
		// image name -> (glyph, size); arrows and some round glyphs fill the whole em box, so they are drawn smaller
		static readonly Dictionary<string, (string Glyph, double Size)> glyphs = new Dictionary<string, (string, double)> {
			["Backwards"] = ("\uE72B", 13),
			["Forwards"] = ("\uE72A", 13),
			["Open"] = ("\uE838", 16),
			["OpenFolder"] = ("\uE838", 16),
			["Save"] = ("\uE74E", 16),
			["SaveAll"] = ("\uE74E", 16),
			["Undo"] = ("\uE7A7", 15),
			["Redo"] = ("\uE7A6", 15),
			["Refresh"] = ("\uE72C", 14),
			["Run"] = ("\uE768", 15),
			["RunOutline"] = ("\uE768", 15),
			["Pause"] = ("\uE769", 15),
			["Stop"] = ("\uE71A", 15),
			["Search"] = ("\uE721", 15),
			["AutoSizeOptimize"] = ("\uE740", 15),
		};

		public static (string Glyph, double Size)? Get(ImageReference image) {
			if (!NetSpyDesign.FluentToolBarIcons || image.IsDefault)
				return null;
			return glyphs.TryGetValue(image.Name, out var g) ? g : null;
		}
	}
}
