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

using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetSpy.Contracts.Controls;
using NetSpy.Contracts.ToolBars;
using NetSpy.Controls;

namespace NetSpy.MainApp {
	[Export]
	sealed class AppToolBar : IStackedContentChild {
		public object? UIObject => toolBar;
		readonly ToolBar toolBar;

		readonly IToolBarService toolBarService;

		[ImportingConstructor]
		public AppToolBar(IToolBarService toolBarService) {
			this.toolBarService = toolBarService;
			toolBar = new ToolBar { Padding = new Thickness(8, 4, 8, 4) };
		}

		internal void Initialize(MetroWindow window) {
			// the menu is moved to the title bar below; detach it first so it can be added to the toolbar again
			window.TitleBarContent = null;
			toolBarService.InitializeToolBar(toolBar, new Guid(ToolBarConstants.APP_TB_GUID), window);
			MoveMenuToTitleBar(window);
		}

		/// <summary>
		/// NetSpy: like WinUI apps, the main menu is shown in the title bar (the toolbar keeps the buttons)
		/// </summary>
		void MoveMenuToTitleBar(MetroWindow window) {
			var menu = toolBar.Items.OfType<Menu>().FirstOrDefault();
			if (menu is null)
				return;
			int index = toolBar.Items.IndexOf(menu);
			toolBar.Items.RemoveAt(index);
			// the separator that followed the menu
			if (index < toolBar.Items.Count && toolBar.Items[index] is Separator)
				toolBar.Items.RemoveAt(index);
			// outside the toolbar the menu no longer gets the toolbar's menu style automatically
			menu.SetResourceReference(FrameworkElement.StyleProperty, ToolBar.MenuStyleKey);
			menu.Background = Brushes.Transparent;
			window.TitleBarContent = menu;
		}
	}
}
