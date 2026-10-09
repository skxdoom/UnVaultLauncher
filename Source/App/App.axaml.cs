using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using UnVault.App.Services;
using UnVault.App.ViewModels;
using UnVault.App.Views;

namespace UnVault.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        DropDownFocus.Register();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel(new AppServices());
            var window = new MainWindow { DataContext = viewModel };
            viewModel.Interaction = window;
            desktop.MainWindow = window;
            HandleUnexpectedErrors(viewModel);
            _ = viewModel.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// A bug on the UI thread shouldn't take down the launcher (possibly mid-download): log it, tell the user, carry on.
    /// Downloads are resumable, so even an interrupted one loses nothing.
    /// </summary>
    private static void HandleUnexpectedErrors(MainViewModel viewModel)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Write("UI thread", e.Exception);
            viewModel.Notice = Localized.Format(Strings.SomethingWentWrong, e.Exception.Message, CrashLog.FilePath);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("Background task", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                CrashLog.Write("Fatal", exception);
        };
    }
}
