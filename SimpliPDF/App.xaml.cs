using Microsoft.UI.Xaml;

namespace SimpliPDF;

public partial class App : Application
{
    public static Window MainWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    ~App()
    {
        Services.ScanService.Cleanup();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
            $"[{DateTime.Now}] {e.Exception}\n\n");
        e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
