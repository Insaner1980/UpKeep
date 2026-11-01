using Microsoft.UI.Xaml;
using UpKeep_App.Platform;

namespace UpKeep_App;

public partial class App : Application
{
    private MainWindow? window;
    private SingleInstanceService? instance;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        instance = new SingleInstanceService();
        if (!instance.IsPrimary)
        {
            SingleInstanceService.ActivateExisting();
            instance.Dispose();
            Exit();
            return;
        }
        window = new MainWindow();
        window.Closed += (_, _) => instance.Dispose();
        window.ShowPanel();
    }
}
