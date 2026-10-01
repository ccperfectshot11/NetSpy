using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Win32;

using NetSpy.Designer.Model;

namespace NetSpy.Designer
{
	public partial class MainWindow : Window
	{
		/// <summary>Designul curent se salvează automat aici (de aici îl preia și implementarea în NetSpy).</summary>
		public static readonly string AutoSavePath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetSpy", "Designer", "autosave" + DesignDocument.FileExtension);

		const string FileFilter = "Design NetSpy (*.netspy-design.json)|*.netspy-design.json|JSON (*.json)|*.json";

		readonly DesignDocument document = new();
		readonly DispatcherTimer autoSaveTimer;
		string? currentFile;

		public MainWindow()
		{
			InitializeComponent();
			PresetBox.ItemsSource = Preset.All;
			PresetBox.SelectedIndex = 0;
			Groups.ItemsSource = document.Groups;

			if (App.SnapshotPath is not null)
			{
				// mod de verificare: temă + valori din linia de comandă, fără salvare automată
				// fereastra e desenată în afara ecranului: nu apare și nu dispare în fața utilizatorului
				WindowStartupLocation = WindowStartupLocation.Manual;
				Left = -20000;
				Top = -20000;
				ShowInTaskbar = false;
				ShowActivated = false;
				PresetBox.SelectedIndex = App.SnapshotPreset ?? 0;
				document.ApplyPreset(Preset.All[PresetBox.SelectedIndex]);
				document.SetValues(App.SnapshotOverrides.Select(a => a.Split('=', 2)).Select(p => new KeyValuePair<string, string>(p[0], p[1])), resetOthers: false);
				Preview.Attach(document);
				ContentRendered += (_, _) => App.TakeSnapshot(this);
				autoSaveTimer = new DispatcherTimer();
				return;
			}

			FitToScreen();

			if (File.Exists(AutoSavePath))
			{
				try
				{
					document.Load(AutoSavePath);
				}
				catch (Exception ex)
				{
					MessageBox.Show(this, "Designul salvat automat nu a putut fi citit:\n" + ex.Message, Title);
				}
			}

			Preview.Attach(document);

			// salvare automată la 0,6 s după ultima modificare (nu la fiecare pas al unui slider)
			autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
			autoSaveTimer.Tick += (_, _) => { autoSaveTimer.Stop(); AutoSave(); };
			document.Changed += (_, _) => { autoSaveTimer.Stop(); autoSaveTimer.Start(); };
			Closing += (_, _) => AutoSave();
			UpdateStatus("Gata.");
		}

		/// <summary>
		/// Mărimea din XAML (1560×940) e în pixeli logici; cu scalare Windows 125–150% poate fi mai mare
		/// decât ecranul și fereastra iese în afară. O micșorăm la zona utilă sau o maximizăm.
		/// </summary>
		void FitToScreen()
		{
			var area = SystemParameters.WorkArea; // ecranul fără bara de activități, în pixeli logici
			if (Width > area.Width - 24 || Height > area.Height - 24)
			{
				Width = Math.Min(Width, area.Width);
				Height = Math.Min(Height, area.Height);
				MinWidth = Math.Min(MinWidth, area.Width);
				MinHeight = Math.Min(MinHeight, area.Height);
				WindowState = WindowState.Maximized;
			}
		}

		void AutoSave()
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(AutoSavePath)!);
				document.Save(AutoSavePath);
				UpdateStatus($"Salvat automat la {DateTime.Now:HH:mm:ss}.");
			}
			catch (Exception ex)
			{
				UpdateStatus("Salvarea automată a eșuat: " + ex.Message);
			}
		}

		void UpdateStatus(string message) =>
			StatusText.Text = $"{message}   Fișier: {currentFile ?? "(nesalvat)"}   ·   Salvare automată: {AutoSavePath}";

		void ApplyPreset_Click(object sender, RoutedEventArgs e)
		{
			if (PresetBox.SelectedItem is Preset preset)
				document.ApplyPreset(preset);
		}

		void Export_Click(object sender, RoutedEventArgs e)
		{
			var themes = ThemeExporter.FindThemesDirectory();
			if (themes is null)
			{
				MessageBox.Show(this, "Nu am găsit sursa NetSpy (dnSpy.sln) deasupra folderului designerului.", Title);
				return;
			}
			try
			{
				var path = ThemeExporter.Export(document, themes);
				UpdateStatus($"Exportat în {path}. Recompilează NetSpy ca să vezi tema.");
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "Exportul a eșuat:\n" + ex.Message, Title);
			}
		}

		void New_Click(object sender, RoutedEventArgs e)
		{
			document.ApplyPreset(Preset.All[0]);
			PresetBox.SelectedIndex = 0;
			currentFile = null;
			UpdateStatus("Design nou.");
		}

		void Open_Click(object sender, RoutedEventArgs e)
		{
			var dialog = new OpenFileDialog { Filter = FileFilter, Title = "Deschide un design NetSpy" };
			if (dialog.ShowDialog(this) != true)
				return;
			try
			{
				document.Load(dialog.FileName);
				currentFile = dialog.FileName;
				UpdateStatus("Deschis.");
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "Fișierul nu a putut fi deschis:\n" + ex.Message, Title);
			}
		}

		void Save_Click(object sender, RoutedEventArgs e)
		{
			if (currentFile is null)
				SaveAs_Click(sender, e);
			else
				SaveTo(currentFile);
		}

		void SaveAs_Click(object sender, RoutedEventArgs e)
		{
			var dialog = new SaveFileDialog {
				Filter = FileFilter,
				Title = "Salvează designul NetSpy",
				FileName = "NetSpy" + DesignDocument.FileExtension,
			};
			if (dialog.ShowDialog(this) == true)
				SaveTo(dialog.FileName);
		}

		void SaveTo(string path)
		{
			try
			{
				document.Save(path);
				currentFile = path;
				AutoSave();
				UpdateStatus("Salvat.");
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "Designul nu a putut fi salvat:\n" + ex.Message, Title);
			}
		}
	}
}
