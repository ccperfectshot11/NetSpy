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

			int i = Array.IndexOf(e.Args, "--snapshot");
			if (i >= 0 && i + 1 < e.Args.Length)
			{
				SnapshotPath = Path.GetFullPath(e.Args[i + 1]);
				if (i + 2 < e.Args.Length && int.TryParse(e.Args[i + 2], out var preset))
					SnapshotPreset = preset;
				SnapshotOverrides = e.Args.Skip(i + 2).Where(a => a.Contains('=')).ToArray();
			}
			base.OnStartup(e);
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
