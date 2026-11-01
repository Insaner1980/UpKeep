using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using UpKeep_App.Platform;
using Windows.System;

namespace UpKeep_App;

public sealed partial class MainWindow : Window
{
    private readonly TrayIconService tray;
    private bool exiting;
    private bool dialogOpen;
    private long? deactivationHideTime;

    public MainWindow(UpKeep.Core.Storage.AppPaths? storagePaths = null)
    {
        paths = storagePaths ?? new();
        InitializeComponent();
        AppWindow.IsShownInSwitchers = false;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        tray = new TrayIconService(this, TogglePanel, RequestExit);
        tray.PlacePanel();
        InitializeServices();
        AppWindow.Closing += (_, e) => { if (!exiting) { e.Cancel = true; AppWindow.Hide(); } };
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && !dialogOpen) { HideOnDeactivation(); } };
        Closed += (_, _) => { analysis?.Dispose(); tray.Dispose(); };
    }

    public void ShowPanel()
    {
        deactivationHideTime = null;
        tray.ShowPanel();
    }
    private void HideOnDeactivation()
    {
        if (AppWindow.IsVisible)
        {
            deactivationHideTime = Environment.TickCount64;
            AppWindow.Hide();
        }
    }
    private void TogglePanel()
    {
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
        }
        else if (deactivationHideTime is not { } hiddenAt || Environment.TickCount64 - hiddenAt > 300)
        {
            ShowPanel();
        }
        deactivationHideTime = null;
    }
    private async void RequestExit()
    {
        if (gate.IsBusy)
        {
            ShowPanel();
            if (await Confirm("An operation is still running", "Keep UpKeep running, or stop queued work and exit after the current operation finishes.", "Stop after current and exit"))
            {
                exitWhenIdle = true;
                coordinator.StopAfterCurrent();
                await (workCancellation?.CancelAsync() ?? Task.CompletedTask);
            }
            return;
        }
        if (busy)
        {
            exitWhenIdle = true;
            await (scanCancellation?.CancelAsync() ?? Task.CompletedTask);
            return;
        }
        exiting = true;
        Close();
        Application.Current.Exit();
    }
    private void OnHide(object sender, RoutedEventArgs e) => AppWindow.Hide();
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !dialogOpen)
        {
            if (detailsPanel is not null)
            {
                CloseDetails();
            }
            else
            {
                AppWindow.Hide();
            }

            e.Handled = true;
        }
    }
    private void OnTabChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (UpdatesPanel is null || CleanupPanel is null)
        {
            return;
        }

        bool updatesVisible = (args.SelectedItem as NavigationViewItem)?.Tag as string == "updates";
        UpdatesPanel.Visibility = updatesVisible ? Visibility.Visible : Visibility.Collapsed;
        CleanupPanel.Visibility = updatesVisible ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        dialogOpen = true;
        try
        {
            await ShowSettings();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await Confirm("Settings could not be saved", error.Message, "OK");
        }
        finally { dialogOpen = false; }
    }
}
