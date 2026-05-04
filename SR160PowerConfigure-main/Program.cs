using System;
using System.IO;
using Avalonia;

namespace SR160PowerConfig
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), $"Program.Main {DateTime.Now:O}{Environment.NewLine}");
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
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
    }
}
