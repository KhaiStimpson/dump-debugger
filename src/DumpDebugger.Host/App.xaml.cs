using Microsoft.UI.Xaml;

namespace DumpDebugger.Host;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(HostOptions.FromCommandLine(Environment.GetCommandLineArgs()));
        _window.Activate();
    }
}
