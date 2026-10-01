using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NetSpy.Designer
{
	public partial class App : Application
	{
		/// <summary>
		/// "NetSpy.Designer.exe --snapshot poza.png [indexTemă] [cheie=valoare ...]": desenează fereastra într-un PNG
		/// și se închide (pentru verificări automate; nu salvează nimic).
		/// </summary>
		public static string? SnapshotPath { get; private set; }
		public static int? SnapshotPreset { get; private set; }
		public static string[] SnapshotOverrides { get; private set; } = Array.Empty<string>();

		public static readonly string CrashLogPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetSpy", "Designer", "crash.log");

		static void LogException(Exception exception)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
				File.AppendAllText(CrashLogPath, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{exception}{Environment.NewLine}");
			}
			catch
			{
				// jurnalul e doar ajutător: nu lăsăm o eroare de scriere să închidă aplicația
			}
		}

		protected override void OnStartup(StartupEventArgs e)
		{
			// o eroare neprevăzută nu mai închide designerul: o scriem în crash.log și continuăm
			DispatcherUnhandledException += (_, args) => {
				LogException(args.Exception);
				args.Handled = true;
				MessageBox.Show($"A apărut o eroare (designerul rămâne deschis):\n\n{args.Exception.Message}\n\nDetalii în {CrashLogPath}",
					"NetSpy Designer", MessageBoxButton.OK, MessageBoxImage.Warning);
			};
			AppDomain.CurrentDomain.UnhandledException += (_, args) => {
				if (args.ExceptionObject is Exception ex)
					LogException(ex);
			};

			// "--export design.json": scrie tema NetSpy din design, fără fereastră
			int x = Array.IndexOf(e.Args, "--export");
			if (x >= 0 && x + 1 < e.Args.Length)
			{
				// fără ferestre (nici de eroare): rezultatul e codul de ieșire, eroarea ajunge în crash.log
				try
				{
					var doc = new Model.DesignDocument();
					doc.Load(e.Args[x + 1]);
					var themes = Model.ThemeExporter.FindThemesDirectory() ?? throw new DirectoryNotFoundException("Nu am găsit NetSpy.sln deasupra designerului.");
					Model.ThemeExporter.Export(doc, themes);
					Shutdown(0);
				}
				catch (Exception ex)
				{
					LogException(ex);
					Shutdown(1);
				}
				return;
			}

			// "--convert-icons": iconițele NetSpy devin iconițele arborelui din NetSpy (NetSpy.Images\Images\*.xaml)
			if (Array.IndexOf(e.Args, "--convert-icons") >= 0)
			{
				try
				{
					var images = Model.IconConverter.FindImagesDirectory() ?? throw new DirectoryNotFoundException("Nu am găsit NetSpy.sln deasupra designerului.");
					int count = Model.IconConverter.Convert(Path.Combine(AppContext.BaseDirectory, "Icons"), images);
					File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "convert-icons.log"), $"{count} iconițe scrise în {images}");
					Shutdown(0);
				}
				catch (Exception ex)
				{
					LogException(ex);
					Shutdown(1);
				}
				return;
			}

			int i = Array.IndexOf(e.Args, "--snapshot");
			if (i >= 0 && i + 1 < e.Args.Length)
			{
				SnapshotPath = Path.GetFullPath(e.Args[i + 1]);
				if (i + 2 < e.Args.Length && int.TryParse(e.Args[i + 2], out var preset))
					SnapshotPreset = preset;
				SnapshotOverrides = e.Args.Skip(i + 2).Where(a => a.Contains('=')).ToArray();
			}
			base.OnStartup(e);
			// (fără StartupUri în App.xaml: fereastra se creează aici, doar când nu exportăm)
			new MainWindow().Show();
		}

		public static void TakeSnapshot(Window window)
		{
			window.Dispatcher.BeginInvoke(() => {
				var content = (FrameworkElement)window.Content;
				var dpi = VisualTreeHelper.GetDpi(window);
				var bitmap = new RenderTargetBitmap(
					(int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
					dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
				bitmap.Render(content);
				var encoder = new PngBitmapEncoder();
				encoder.Frames.Add(BitmapFrame.Create(bitmap));
				using (var stream = File.Create(SnapshotPath!))
					encoder.Save(stream);
				Current.Shutdown();
			}, DispatcherPriority.ApplicationIdle);
		}
	}
}
