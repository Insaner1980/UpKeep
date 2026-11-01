using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UpKeep.Core.Cleanup;
using UpKeep.Core.Discovery;
using UpKeep.Core.Elevation;
using UpKeep.Core.Models;
using UpKeep.Core.Processes;
using UpKeep.Core.Storage;
using UpKeep.Core.Updates;
using UpKeep_App.Platform;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace UpKeep_App;

public sealed partial class MainWindow
{
    private const string UpdatesView = "updates";
    private const string CleanupView = "cleanup";
    private const string CanceledMessage = "Canceled";
    private const string CopyDetailsLabel = "Copy details";
    private readonly AppPaths paths;
    private readonly OperationGate gate = new();
    private SettingsStore settings = null!;
    private UpdateCoordinator coordinator = null!;
    private CleanupAnalyzer analyzer = null!;
    private CleanupExecutor executor = null!;
    private ManagedActions managed = null!;
    private OperationJournal journal = null!;
    private DiscoveryContext discovery = null!;
    private readonly List<IUpdateProvider> providers = [];
    private string? scanKind;
    private CleanupAnalysis? analysis;
    private IReadOnlyList<UpdateItem> updates = [];
    private IReadOnlyList<ManagedCleanupAction> actions = [];
    private readonly HashSet<InstallationKey> selectedUpdates = [];
    private readonly HashSet<string> selectedFiles = [];
    private readonly Dictionary<string, (long Bytes, string Group)> selectedFileMetadata = [];
    private readonly HashSet<string> selectedGroups = [];
    private readonly HashSet<string> selectedActions = [];
    private CancellationTokenSource? scanCancellation;
    private CancellationTokenSource? workCancellation;
    private bool busy, exitWhenIdle, updatingSelection, updating, projectFoldersConfigured;
    private Grid? detailsPanel;
    private Control? detailsOrigin;
    private CleanupCategory? detailsCategory;

    private void InitializeServices()
    {
        Directory.CreateDirectory(paths.Root);
        CleanupManifest.RemoveOrphans(paths);
        settings = new(paths);
        journal = new(paths);
        var context = discovery = new DiscoveryContext(new ToolLocator(), new ProcessRunner(), paths.Root);
        providers.Add(new WinGetProvider());
        foreach (var kind in Enum.GetValues<ManagerKind>().Where(k => k is not ManagerKind.Apt and not ManagerKind.Snap))
        {
            providers.Add(new CommandProvider(kind));
        }

        coordinator = new(providers, context, journal, gate);
        analyzer = new(paths, context);
        executor = new(new RecycleBinService(), journal, gate);
        managed = new(context);
        CheckButton.IsEnabled = true;
        AnalyzeButton.IsEnabled = true;
        CheckButton.Click += async (_, _) => await CheckUpdates();
        UpdateButton.Click += async (_, _) => await UpdateSelected();
        AnalyzeButton.Click += async (_, _) => await Analyze();
        CleanButton.Click += async (_, _) => await CleanSelected();
        SelectUpdates.Checked += (_, _) => SelectAllUpdates(true);
        SelectUpdates.Unchecked += (_, _) => SelectAllUpdates(false);
        var prior = journal.Load();
        if (prior?.State == "Interrupted")
        {
            UpdateSummary.Text = CleanupSummary.Text = "The previous operation was interrupted. Check the results before trying again.";
        }

        RenderCleanup();
    }
    private static TextBlock Text(string value, double size = 14) => new() { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size };
    private static string Category(CleanupCategory category) => category switch { CleanupCategory.TemporaryFiles => "Temporary files", CleanupCategory.TestArtifacts => "Test artifacts", CleanupCategory.OldLogs => "Old logs", CleanupCategory.PackageCaches => "Package caches", _ => "Emulators" };
    private static string State(UpdateState state) => state switch { UpdateState.UpToDate => "Up to date", UpdateState.UpdateAvailable => "Update available", UpdateState.CheckUnavailable => "Check unavailable", UpdateState.UnsupportedInstallation => "Unsupported installation", UpdateState.CompletedUnverified => "Update finished; version not verified", UpdateState.SucceededVerified => "Installed version verified", UpdateState.NeedsInteraction => "Needs interaction", UpdateState.RestartRequired => "Restart required", _ => state.ToString() };
    private static Grid Row(string name, string subtitle, bool? selected, bool enabled, Action<bool> select, Action<Button> detail, string action = "Details")
    {
        var row = new Grid { Padding = new Thickness(16, 10, 12, 10), ColumnSpacing = 8, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };
        row.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        row.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        var check = new CheckBox { IsChecked = selected, IsThreeState = selected is null, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(check, "Select " + name);
        check.Click += (_, _) => select(check.IsChecked == true);
        row.Children.Add(check);
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(Text(name));
        var secondary = Text(subtitle, 12);
        secondary.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        content.Children.Add(secondary);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        var button = new Button { Content = Text(action, 12), Style = (Style)Application.Current.Resources["SubtleButtonStyle"], VerticalAlignment = VerticalAlignment.Center, MaxWidth = 94 };
        AutomationProperties.SetName(button, action + " " + name);
        button.Click += (_, _) => detail(button);
        Grid.SetColumn(button, 2);
        row.Children.Add(button);
        return row;
    }
    private void SetBusy(bool value, bool mutation = false)
    {
        busy = value;
        CheckButton.IsEnabled = !value;
        AnalyzeButton.IsEnabled = !value;
        SelectUpdates.IsEnabled = !value;
        UpdateList.IsEnabled = CleanupList.IsEnabled = !value;
        SetBusyActions(value, mutation);
        if (!value && exitWhenIdle)
        {
            RequestExit();
        }
    }
    private async Task CheckUpdates()
    {
        if (busy)
        {
            if (scanKind == UpdatesView)
            {
                await (scanCancellation?.CancelAsync() ?? Task.CompletedTask);
            }

            return;
        }
        scanKind = UpdatesView;
        SetBusy(true);
        scanCancellation = new();
        UpdateSummary.Text = "Checking installed tools…";
        try
        {
            updates = await Task.Run(() => coordinator.CheckAsync(scanCancellation.Token), scanCancellation.Token);
            selectedUpdates.Clear();
            RenderUpdates();
            CheckButton.Content = "Check again";
        }
        catch (OperationCanceledException) { UpdateSummary.Text = CanceledMessage; }
        catch (Exception e) { UpdateSummary.Text = e.Message; }
        finally { scanCancellation.Dispose(); scanCancellation = null; scanKind = null; SetBusy(false); }
    }
    private void RenderUpdates()
    {
        UpdateList.Items.Clear();
        var count = updates.Count(i => i.Eligible);
        UpdateSummary.Text = UpdateSummaryText(count);
        SelectUpdates.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        updatingSelection = true;
        SelectUpdates.IsChecked = count > 0 && updates.Where(i => i.Eligible).All(i => selectedUpdates.Contains(i.Key));
        updatingSelection = false;
        foreach (var item in updates.Where(i => i.Eligible))
        {
            AddUpdate(item);
        }

        var others = updates.Where(i => !i.Eligible).ToArray();
        if (others.Length > 0)
        {
            var button = new Button { Content = $"Other tools ({others.Length})", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(16, 8, 16, 8) };
            button.Click += (_, _) => ShowUpdateDetails(others, button);
            UpdateList.Items.Add(button);
        }
        UpdateButton.IsEnabled = !busy && selectedUpdates.Count > 0;
    }
    private void AddUpdate(UpdateItem item) => UpdateList.Items.Add(Row(item.Name, item.Key.ProviderId + " / " + State(item.State) + (item.TargetVersion is null ? "" : " / " + item.TargetVersion), selectedUpdates.Contains(item.Key), item.CanRun, value => { if (value) { selectedUpdates.Add(item.Key); } else { selectedUpdates.Remove(item.Key); } updatingSelection = true; SelectUpdates.IsChecked = updates.Where(i => i.Eligible).All(i => selectedUpdates.Contains(i.Key)); updatingSelection = false; UpdateButton.IsEnabled = !busy && selectedUpdates.Count > 0; }, button => ShowUpdateDetails([item], button)));
    private void ShowUpdateDetails(IEnumerable<UpdateItem> items, Control origin)
    {
        var list = OpenDetails("Update details", origin);
        foreach (var item in items)
        {
            list.Items.Add(Row(item.Name, State(item.State) + "\n" + item.Detail + "\nInstalled: " + (item.CurrentVersion ?? "Unknown") + "\nTarget: " + (item.TargetVersion ?? "Unknown") + "\n" + item.Key.InstallIdentity, selectedUpdates.Contains(item.Key), !busy && item.CanRun, value => { if (value) { selectedUpdates.Add(item.Key); } else { selectedUpdates.Remove(item.Key); } }, async _ => await RunItemAction(item), ItemAction(item)));
        }
    }
    private async Task CheckDistro(UpdateItem item)
    {
        if (!await Confirm("Check " + item.Name + "?", "This starts the selected distribution if stopped. Package indexes are not refreshed. No packages will be installed.", "Check distro"))
        {
            return;
        }

        var provider = providers.OfType<WslDistroProvider>().FirstOrDefault(p => p.Id == "wsl-distro:" + item.Name);
        if (provider is null)
        {
            provider = new(item.Name);
            providers.Add(provider);
        }
        CloseDetails();
        scanKind = UpdatesView;
        SetBusy(true);
        scanCancellation = new();
        try
        {
            var result = await Task.Run(() => provider.CheckAsync(discovery, scanCancellation.Token), scanCancellation.Token);
            updates = updates.Where(i => i.Key.ProviderId != provider.Id).Concat(result.Items).ToArray();
            RenderUpdates();
        }
        catch (OperationCanceledException) { UpdateSummary.Text = CanceledMessage; }
        catch (Exception e) { UpdateSummary.Text = e.Message; }
        finally { scanCancellation.Dispose(); scanCancellation = null; scanKind = null; SetBusy(false); }
    }
    private async Task UpdateSelected()
    {
        if (gate.IsBusy)
        {
            coordinator.StopAfterCurrent();
            await (workCancellation?.CancelAsync() ?? Task.CompletedTask);
            if (updating)
            {
                UpdateStage.Text = "Stopping after the current update finishes…";
            }

            return;
        }
        if (busy)
        {
            return;
        }

        var selected = updates.Where(i => selectedUpdates.Contains(i.Key) && i.CanRun).ToArray();
        if (selected.Length == 0)
        {
            UpdateSummary.Text = "Select at least one item.";
            return;
        }
        SetBusy(true);
        try
        {
            if (selected.Any(i => i.Kind == UpdateKind.RunUpdater) && !await Confirm("Run selected updaters?", string.Join("\n", selected.Select(i => i.Name + ": " + i.Detail)), "Run updater"))
            {
                return;
            }
            if (!await ReviewDistroTransactionsAsync(selected) || exitWhenIdle)
            {
                return;
            }
            updating = true;
            SetBusy(true, true);
            UpdateSummary.Text = $"Preparing {selected.Length} selected update(s)…";
            UpdateStage.Text = "Checking installation before updating…";
            var progress = UpdateProgressReporter(selected.Length);
            var results = await Task.Run(() => coordinator.ExecuteAsync(selected, progress, CancellationToken.None), CancellationToken.None);
            updates = updates.Select(i => results.FirstOrDefault(r => r.Key == i.Key) is var found && found.Result is not null ? i with { State = found.Result.State, Detail = found.Result.Detail } : i).ToArray();
            selectedUpdates.Clear();
            RenderUpdates();
            UpdateSummary.Text = "Update run finished. Review results in Other tools.";
        }
        catch (Exception e) { UpdateSummary.Text = e.Message; }
        finally { updating = false; SetBusy(false); }
    }
    private async Task Analyze()
    {
        if (busy)
        {
            if (scanKind == CleanupView)
            {
                await (scanCancellation?.CancelAsync() ?? Task.CompletedTask);
            }

            return;
        }
        scanKind = CleanupView;
        SetBusy(true);
        scanCancellation = new();
        analysis?.Dispose();
        analysis = null;
        actions = [];
        selectedFiles.Clear();
        selectedFileMetadata.Clear();
        selectedGroups.Clear();
        selectedActions.Clear();
        CleanupSummary.Text = "Analyzing locations…";
        try
        {
            var load = settings.Load();
            projectFoldersConfigured = load.Settings.ProjectRoots.Count > 0;
            var scope = new CleanupScope(await Task.Run(() => CleanupRootResolver.Resolve(load.Settings.ProjectRoots), scanCancellation.Token));
            analysis = await analyzer.AnalyzeAsync(scope, new Progress<CleanupProgress>(p => CleanupSummary.Text = p.Message), scanCancellation.Token);
            actions = await Task.Run(() => managed.DiscoverAsync(scanCancellation.Token), scanCancellation.Token);
            RenderCleanup();
            AnalyzeButton.Content = "Analyze again";
            CleanupSummary.Text = load.Warning ?? (analysis.Warnings.Count > 0 ? "Partial scan. See Details for unchecked locations." : "Choose what to clean");
        }
        catch (OperationCanceledException) { CleanupSummary.Text = CanceledMessage; }
        catch (Exception e) { CleanupSummary.Text = e.Message; }
        finally { scanCancellation.Dispose(); scanCancellation = null; scanKind = null; SetBusy(false); }
    }
    private void RenderCleanup()
    {
        CleanupList.Items.Clear();
        foreach (var category in Enum.GetValues<CleanupCategory>())
        {
            var groups = analysis?.Groups.Where(g => g.Category == category).ToArray() ?? [];
            var eligible = groups.Where(g => g.Eligibility == Eligibility.Eligible).ToArray();
            int selected = eligible.Count(g => selectedGroups.Contains(g.Id));
            var groupIds = groups.Select(g => g.Id).ToHashSet();
            bool any = groups.Any(g => selectedGroups.Contains(g.Id)) || selectedFileMetadata.Any(f => selectedFiles.Contains(f.Key) && groupIds.Contains(f.Value.Group));
            bool? state = any ? null : false;
            if (any && selected == eligible.Length && eligible.Length > 0)
                state = true;
            var managedCount = actions.Count(a => a.Category == category);
            CleanupList.Items.Add(Row(Category(category), analysis is null ? "Analyze to inspect locations." : CleanupText.CategorySummary(category, groups, managedCount, projectFoldersConfigured), state, eligible.Length > 0, value => SelectCategory(value, eligible, groups, groupIds), button => ShowCategory(category, button)));
        }
        RefreshSelection();
    }
    private void RefreshSelection()
    {
        var bytes = (analysis?.Groups.Where(g => selectedGroups.Contains(g.Id)).Sum(g => g.LogicalSize) ?? 0) + selectedFileMetadata.Where(f => selectedFiles.Contains(f.Key) && !selectedGroups.Contains(f.Value.Group)).Sum(f => f.Value.Bytes);
        SelectedSize.Text = CleanupText.SelectionSummary(bytes, selectedGroups.Count + selectedFiles.Count > 0, selectedActions.Count);
        CleanButton.IsEnabled = !busy && (selectedGroups.Count + selectedFiles.Count + selectedActions.Count > 0);
    }
    private ListView OpenDetails(string title, Control origin)
    {
        CloseDetails();
        detailsOrigin = origin;
        Navigation.Visibility = Visibility.Collapsed;
        detailsPanel = new Grid { Padding = new Thickness(16, 8, 16, 8) };
        Grid.SetRow(detailsPanel, 1);
        detailsPanel.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        detailsPanel.RowDefinitions.Add(new()
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        var back = new Button { Content = "Back", HorizontalAlignment = HorizontalAlignment.Left };
        back.Click += (_, _) => CloseDetails();
        AutomationProperties.SetName(back, "Back from " + title);
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(back);
        header.Children.Add(Text(title, 18));
        detailsPanel.Children.Add(header);
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(list, 1);
        detailsPanel.Children.Add(list);
        Root.Children.Add(detailsPanel);
        back.Focus(FocusState.Programmatic);
        return list;
    }
    private void CloseDetails()
    {
        if (detailsPanel is null)
        {
            return;
        }

        Root.Children.Remove(detailsPanel);
        detailsPanel = null;
        Navigation.Visibility = Visibility.Visible;
        RenderCleanup();
        if (detailsCategory is { } category)
        {
            detailsOrigin = CleanupList.Items.OfType<Grid>().ElementAt((int)category).Children.OfType<Button>().Single();
            CleanupList.UpdateLayout();
        }
        detailsOrigin?.Focus(FocusState.Programmatic);
        detailsOrigin = null;
        detailsCategory = null;
    }
    private void ShowCategory(CleanupCategory category, Control origin)
    {
        var list = OpenDetails(Category(category), origin);
        detailsCategory = category;
        if (analysis is not null && !analysis.Groups.Any(g => g.Category == category) && !actions.Any(a => a.Category == category))
        {
            list.Items.Add(Text(CleanupText.CategorySummary(category, [], 0, projectFoldersConfigured)));
        }

        foreach (var group in analysis?.Groups.Where(g => g.Category == category) ?? [])
        {
            list.Items.Add(Row(group.Name, group.Count == 0 ? group.Reason : $"{group.Count} files / {CleanupText.Size(group.LogicalSize)}\n{group.Reason}", selectedGroups.Contains(group.Id), !busy && group.Eligibility is Eligibility.Eligible or Eligibility.ReviewRequired, value => SelectGroup(group.Id, value), button => ShowFiles(group, button)));
        }

        foreach (var action in actions.Where(a => a.Category == category))
        {
            list.Items.Add(Row(action.Name, action.Location + "\n" + action.Detail, selectedActions.Contains(action.Id), !busy && action.Eligibility is Eligibility.Eligible or Eligibility.ReviewRequired, value => SelectAction(action.Id, value), async _ => await CopyDetails(action.Name + "\n" + action.Location + "\n" + action.Detail), CopyDetailsLabel));
        }

        if (category == CleanupCategory.TemporaryFiles)
        {
            var system = new Button { Content = "Analyze system temporary files", IsEnabled = !busy, Margin = new Thickness(0, 12, 0, 0) };
            system.Click += async (_, _) => await AnalyzeProtected();
            list.Items.Add(system);
        }
    }
    private void ShowFiles(CleanupGroup group, Control origin, int offset = 0)
    {
        if (analysis is null)
        {
            return;
        }

        var list = OpenDetails(group.Name, origin);
        detailsCategory = group.Category;
        var page = analysis.Manifest.Page(group.Id, offset);
        foreach (var candidate in page)
        {
            list.Items.Add(Row(candidate.RelativePath, $"{candidate.LogicalSize.ToString(CultureInfo.InvariantCulture)} bytes / {candidate.Method}\n{candidate.Reason}", selectedGroups.Contains(candidate.Group) || selectedFiles.Contains(candidate.Id), !busy && !selectedGroups.Contains(candidate.Group) && candidate.Eligibility is Eligibility.Eligible or Eligibility.ReviewRequired, value => { if (value) { selectedFiles.Add(candidate.Id); selectedFileMetadata[candidate.Id] = (candidate.LogicalSize, candidate.Group); } else { selectedFiles.Remove(candidate.Id); selectedFileMetadata.Remove(candidate.Id); } RefreshSelection(); }, async _ => await CopyDetails(analysis.Roots.First(r => r.Id == candidate.RootId).Path + "\\" + candidate.RelativePath + "\n" + candidate.Reason), CopyDetailsLabel));
        }

        if (page.Count == 200)
        {
            var next = new Button { Content = "Next 200 files" };
            next.Click += (_, _) => ShowFiles(group, origin, offset + 200);
            list.Items.Add(next);
        }
    }
    private async Task CleanSelected()
    {
        if (gate.IsBusy)
        {
            await (workCancellation?.CancelAsync() ?? Task.CompletedTask);
            coordinator.StopAfterCurrent();
            return;
        }
        if (busy || analysis is null)
        {
            return;
        }

        if (selectedFiles.Count + selectedGroups.Count + selectedActions.Count == 0)
        {
            CleanupSummary.Text = "Select at least one item.";
            return;
        }
        var selection = new CleanupSelection(analysis, selectedFiles.ToHashSet(), selectedGroups.ToHashSet());
        var chosenActions = actions.Where(a => selectedActions.Contains(a.Id)).ToArray();
        SetBusy(true);
        try
        {
            var counts = await Task.Run(() => CleanupMethodCounts(selection), CancellationToken.None);
            if (exitWhenIdle || !await Confirm("Clean selected items?", string.Join("\n", counts.Concat(chosenActions.Select(a => a.Name + ": " + a.Detail))) + "\nChanged, inaccessible, or locked files will be skipped. Recycled data does not free disk space.", "Clean selected"))
            {
                return;
            }
            CloseDetails();
            SetBusy(true, true);
            workCancellation = new();
            var result = await executor.ExecuteAsync(selection, new Progress<CleanupProgress>(p => CleanupSummary.Text = $"{p.Completed} items cleaned, {p.Skipped} skipped"), workCancellation.Token);
            var items = result.Items.ToList();
            await ExecuteManagedActionsAsync(chosenActions, items);
            var complete = new CleanupResult(items);
            CleanupSummary.Text = $"{complete.Completed} items cleaned, {complete.Skipped} skipped, {complete.Failed} failed";
            try
            {
                journal.Write(new("Cleanup", "Completed", items.Count, items.Count, CleanupSummary.Text));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                CleanupSummary.Text += ". The summary could not be saved; review the results below.";
            }
            selectedFiles.Clear();
            selectedFileMetadata.Clear();
            selectedGroups.Clear();
            selectedActions.Clear();
            actions = [];
            analysis.Dispose();
            analysis = null;
            ShowCleanupResults(complete);
        }
        catch (Exception e) { CleanupSummary.Text = e.Message; }
        finally { workCancellation?.Dispose(); workCancellation = null; SetBusy(false); }
    }
    private void ShowCleanupResults(CleanupResult result, int offset = 0)
    {
        var list = OpenDetails("Cleanup results", CleanButton);
        list.Items.Add(Text($"{result.Completed} completed, {result.Skipped} skipped, {result.Failed} failed"));
        foreach (var item in result.Page(offset))
        {
            list.Items.Add(Text(item.State + ": " + item.Detail));
        }

        if (offset > 0)
        {
            var previous = new Button { Content = "Previous 200 results" };
            previous.Click += (_, _) => ShowCleanupResults(result, Math.Max(0, offset - 200));
            list.Items.Add(previous);
        }
        if (offset + 200 < result.Items.Count)
        {
            var next = new Button { Content = "Next 200 results" };
            next.Click += (_, _) => ShowCleanupResults(result, offset + 200);
            list.Items.Add(next);
        }
    }
    private async Task AnalyzeProtected()
    {
        if (busy)
        {
            return;
        }

        if (!await Confirm("Administrator access required", "Analyze Windows temporary files using a short-lived helper? No files will be removed.", "Analyze"))
        {
            return;
        }

        dialogOpen = true;
        SetBusy(true);
        try
        {
            var result = await ElevationClient.ExecuteAsync("WindowsTemp", ProtectedAction.Analyze, [], CancellationToken.None);
            var list = OpenDetails("System temporary files", AnalyzeButton);
            list.Items.Add(Text(result.State + ": " + result.Detail));
            foreach (var file in result.Candidates.Take(200))
            {
                list.Items.Add(Text(file.RelativePath + " / " + CleanupText.Size(file.LogicalSize)));
            }

            list.Items.Add(Text("Protected removal is not enabled until the elevated handshake has been verified in an authorized Windows test session."));
        }
        catch (System.ComponentModel.Win32Exception error) { CleanupSummary.Text = "Could not start protected analysis: " + error.Message; }
        finally { SetBusy(false); dialogOpen = false; }
    }
    private async Task<bool> Confirm(string title, string message, string primary)
    {
        dialogOpen = true;
        try
        {
            return await new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = new ScrollViewer { Content = Text(message), MaxHeight = 300 }, PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { dialogOpen = false; }
    }
    private async Task CopyDetails(string details)
    {
        if (await Confirm(CopyDetailsLabel, details, CopyDetailsLabel))
        {
            var package = new DataPackage();
            package.SetText(details);
            Clipboard.SetContent(package);
        }
    }
    private async Task ShowSettings()
    {
        var load = settings.Load();
        var roots = load.Settings.ProjectRoots.ToList();
        var content = new StackPanel { Spacing = 8 };
        var list = new ListView { MaxHeight = 180, SelectionMode = ListViewSelectionMode.Single };
        foreach (var root in roots)
        {
            list.Items.Add(root);
        }

        content.Children.Add(list);
        var add = new Button { Content = "Add folder" };
        add.Click += async (_, _) =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && PathGuard.ValidLocalRoot(folder.Path) && !roots.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(folder.Path);
                list.Items.Add(folder.Path);
            }
        };
        content.Children.Add(add);
        var remove = new Button { Content = "Remove selected folder" };
        remove.Click += (_, _) => { if (list.SelectedItem is string path) { roots.Remove(path); list.Items.Remove(path); } };
        content.Children.Add(remove);
        var suggested = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Dev");
        CheckBox? suggestion = null;
        if (Directory.Exists(suggested) && !roots.Contains(suggested, StringComparer.OrdinalIgnoreCase))
        {
            suggestion = new CheckBox { Content = "Include suggested folder: " + suggested, IsChecked = false };
            content.Children.Add(suggestion);
        }
        if (load.Warning is not null)
        {
            content.Children.Add(Text(load.Warning));
        }

        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Project folders", Content = content, PrimaryButtonText = "Save", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = load.Warning is null };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (suggestion?.IsChecked == true)
            {
                roots.Add(suggested);
            }

            settings.Save(new(1, roots));
        }
    }

    private void SelectAllUpdates(bool selected)
    {
        if (updatingSelection)
        {
            return;
        }

        selectedUpdates.Clear();
        if (selected)
        {
            selectedUpdates.UnionWith(updates.Where(i => i.Eligible).Select(i => i.Key));
        }

        RenderUpdates();
    }
    private string UpdateSummaryText(int count)
    {
        if (count == 1)
        {
            return "1 update available";
        }

        if (count > 0)
        {
            return $"{count} updates available";
        }

        return updates.Count > 0 && updates.All(i => i.State is UpdateState.UpToDate or UpdateState.SucceededVerified)
            ? "Your supported tools are up to date." : "Some update checks are unavailable. Review Other tools.";
    }
    private static string ItemAction(UpdateItem item) => item switch
    {
        { Kind: UpdateKind.CheckEnvironment } => "Check distro",
        { CanRun: false } => CopyDetailsLabel,
        { Kind: UpdateKind.RunUpdater } => "Run updater",
        _ => "Update"
    };
    private async Task RunItemAction(UpdateItem item)
    {
        if (item.Kind == UpdateKind.CheckEnvironment && !busy)
        {
            await CheckDistro(item);
        }
        else if (item.CanRun && !busy)
        {
            selectedUpdates.Clear();
            selectedUpdates.Add(item.Key);
            CloseDetails();
            await UpdateSelected();
        }
        else
        {
            await CopyDetails(item.Name + "\n" + item.Detail + "\n" + item.Key.InstallIdentity);
        }
    }
    private void SetBusyActions(bool value, bool mutation)
    {
        SetScanActivity(value, mutation);
        UpdateButton.Content = Text(mutation && value ? "Stop after current" : "Update selected");
        CleanButton.Content = Text(mutation && value ? "Stop after current" : "Clean selected");
        UpdateButton.IsEnabled = mutation && value || !value && selectedUpdates.Count > 0;
        CleanButton.IsEnabled = mutation && value || !value && (selectedFiles.Count + selectedGroups.Count + selectedActions.Count > 0);
    }
    private void SetScanActivity(bool value, bool mutation)
    {
        bool updateActivity = value && (updating || scanKind == UpdatesView);
        UpdateProgress.IsIndeterminate = updateActivity;
        UpdateProgress.Visibility = updateActivity ? Visibility.Visible : Visibility.Collapsed;
        UpdateStage.Visibility = value && updating ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.Content = Text(value && scanKind == UpdatesView ? "Cancel check" : "Check again");
        AnalyzeButton.Content = Text(value && scanKind == CleanupView ? "Cancel analysis" : "Analyze");
        if (value && !mutation)
        {
            CheckButton.IsEnabled = scanKind == UpdatesView;
            AnalyzeButton.IsEnabled = scanKind == CleanupView;
        }
    }
    private void SelectCategory(bool value, CleanupGroup[] eligible, CleanupGroup[] groups, HashSet<string> groupIds)
    {
        if (value)
        {
            foreach (var group in eligible)
            {
                selectedGroups.Add(group.Id);
            }
        }
        else
        {
            foreach (var group in groups)
            {
                selectedGroups.Remove(group.Id);
            }
            foreach (var file in selectedFileMetadata.Where(f => groupIds.Contains(f.Value.Group)).Select(f => f.Key))
            {
                selectedFiles.Remove(file);
            }
        }
        RenderCleanup();
    }
    private void SelectGroup(string id, bool value)
    {
        if (value)
            selectedGroups.Add(id);
        else
            selectedGroups.Remove(id);
        RefreshSelection();
    }
    private void SelectAction(string id, bool value)
    {
        if (value)
            selectedActions.Add(id);
        else
            selectedActions.Remove(id);
        RefreshSelection();
    }
    private async Task<bool> ReviewDistroTransactionsAsync(UpdateItem[] selected)
    {
        foreach (var item in selected)
        {
            if (providers.First(p => p.Id == item.Key.ProviderId) is not WslDistroProvider distro)
            {
                continue;
            }

            try
            {
                var preview = await distro.PreviewAsync(item, CancellationToken.None);
                if (!await Confirm("Review distro transaction", preview, "Install selected"))
                {
                    return false;
                }

                distro.ApprovePreview(item, preview);
            }
            catch (Exception e) { UpdateSummary.Text = e.Message; return false; }
        }
        return true;
    }
    private async Task ExecuteManagedActionsAsync(ManagedCleanupAction[] chosenActions, List<CleanupItemResult> items)
    {
        if (chosenActions.Length > 0)
        {
            if (!gate.TryEnter())
            {
                throw new InvalidOperationException("Another operation started.");
            }

            try
            {
                foreach (var action in chosenActions)
                {
                    try
                    {
                        workCancellation!.Token.ThrowIfCancellationRequested();
                        journal.Write(new("Cleanup", "Running", chosenActions.Length, items.Count, "Manager action: " + action.Name));
                        var outcome = await managed.ExecuteAsync(action, workCancellation!.Token);
                        items.Add(outcome with
                        {
                            Detail = action.Name + " / " + action.Location + ": " + outcome.Detail
                        });
                    }
                    catch (OperationCanceledException) { items.Add(new(action.Id, CanceledMessage, 0, action.Name + ": Canceled before starting.")); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        items.Add(new(action.Id, "Failed", 0, action.Name + ": " + error.Message));
                        await workCancellation!.CancelAsync();
                    }
                }
            }
            finally { gate.Exit(); }
        }
    }
    private Progress<OperationProgress> UpdateProgressReporter(int total)
    {
        int current = 1;
        return new Progress<OperationProgress>(p =>
        {
            if (!updating)
            {
                return;
            }

            if (p.Current > 0)
            {
                current = p.Current;
            }

            UpdateSummary.Text = $"Update {current} of {total}: {p.Name}";
            UpdateStage.Text = (string.IsNullOrWhiteSpace(p.Detail) ? State(p.State) : p.Detail) + (coordinator.StopRequested ? " Remaining updates will not start." : "");
        });
    }
    private static string[] CleanupMethodCounts(CleanupSelection selection)
    {
        var methods = new Dictionary<RemovalMethod, int>();
        foreach (var candidate in selection.Analysis.Manifest.Read())
        {
            if ((selection.CandidateIds.Contains(candidate.Id) || selection.EligibleGroups.Contains(candidate.Group)) && candidate.Eligibility is Eligibility.Eligible or Eligibility.ReviewRequired)
            {
                methods[candidate.Method] = methods.GetValueOrDefault(candidate.Method) + 1;
            }
        }

        return methods.Select(g => $"{g.Value} files: {(g.Key == RemovalMethod.Recycle ? "move to Recycle Bin" : "remove permanently")}").ToArray();
    }
}
