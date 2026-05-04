using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.IO;

namespace SR160PowerConfig
{
    public class App : Application
    {
        public override void Initialize()
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), $"App.Initialize {DateTime.Now:O}{Environment.NewLine}");
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), $"FrameworkCompleted {DateTime.Now:O}{Environment.NewLine}");
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), $"Before MainWindow {DateTime.Now:O}{Environment.NewLine}");
                var mainWindow = new MainWindow();
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), $"After MainWindow {DateTime.Now:O}{Environment.NewLine}");
                desktop.MainWindow = mainWindow;
                Dispatcher.UIThread.Post(() =>
                {
                    mainWindow.Show();
                    mainWindow.Activate();
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), $"After Dispatcher Show {DateTime.Now:O}{Environment.NewLine}");
                }, DispatcherPriority.Loaded);
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
