using Microsoft.UI.Xaml;
using UpKeep.Core.Storage;

namespace UpKeep_UiTests;

public partial class App : Application
{
    private UpKeep_App.MainWindow? window;
    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        using var scratch = new UpKeep.Tests.Scratch();
        try
        {
            window = new(new AppPaths(Path.Combine(scratch.Root, "state")));
            window.ShowPanel();
            await window.RunTestFlowsAsync(scratch);
            Environment.ExitCode = 0;
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            // Keep failure text in the explicitly requested report, outside disposable fixtures.
            var report = Environment.GetEnvironmentVariable("UPKEEP_UI_TEST_REPORT");
            if (report is not null)
                File.WriteAllText(report, error.ToString());
        }
        finally
        {
            window?.CloseTestWindow();
            Exit();
        }
    }
}
