using System;
using System.IO;
using Avalonia;
using Avalonia.Logging;

namespace SR160PowerConfig;

internal static class Program
{
	[STAThread]
	private static void Main(string[] args)
	{
		try
		{
			BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
		}
		catch (Exception ex)
		{
			File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), ex.ToString());
			Console.Error.WriteLine("FATAL: " + ex);
			Environment.Exit(1);
		}
	}

	public static AppBuilder BuildAvaloniaApp()
	{
		return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace(LogEventLevel.Warning);
	}
}
