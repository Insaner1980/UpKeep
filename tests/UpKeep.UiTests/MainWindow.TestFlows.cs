using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UpKeep.Core.Cleanup;
using UpKeep.Core.Discovery;
using UpKeep.Core.Models;
using UpKeep.Core.Processes;
using UpKeep.Core.Updates;

namespace UpKeep_App;

public sealed partial class MainWindow
{
    private int assertions;

    internal async Task RunTestFlowsAsync(UpKeep.Tests.Scratch scratch)
    {
        await UntilAsync(() => Root.XamlRoot is not null);
        Check(AppWindow.IsVisible, "The test panel is visible.");
        Check(CleanupList.Items.Count == 5, "All cleanup categories are rendered.");
        Check(!CleanButton.IsEnabled && !UpdateButton.IsEnabled, "No mutation is enabled before selection.");
        TogglePanel();
        Check(!AppWindow.IsVisible, "The panel can be hidden.");
        ShowPanel();
        Check(AppWindow.IsVisible, "The panel can be restored.");

        discovery = new(new ToolLocator([scratch.Root]), new NoProcesses(), scratch.Root);
        var provider = new FixtureProvider();
        providers.Clear();
        providers.Add(provider);
        coordinator = new(providers, discovery, journal, gate);
        analyzer = new(paths, discovery);
        managed = new(discovery, () => false);
        await CheckUpdates();
        Check(UpdateSummary.Text == "1 update available", "The update count is visible.");
        Check(UpdateList.Items.Count == 2, "Eligible updates and Other tools are separate.");
        SelectUpdates.IsChecked = true;
        Check(selectedUpdates.Count == 1 && UpdateButton.IsEnabled, "Select all enables only the eligible update.");
        SelectUpdates.IsChecked = false;
        Check(selectedUpdates.Count == 0, "Clearing selection disables updates.");
        ShowUpdateDetails(updates, CheckButton);
        Check(detailsPanel is not null && Navigation.Visibility == Visibility.Collapsed, "Update details replace navigation.");
        CloseDetails();
        SelectAllUpdates(true);
        await UpdateSelected();
        Check(provider.Mutations == 1, "Exactly one selected update ran.");
        Check(updates.Any(item => item.State == UpdateState.SucceededVerified), "A successful update is verified.");
        Check(selectedUpdates.Count == 0, "Completed update selection is cleared.");

        updates = [];
        RenderUpdates();
        Check(UpdateSummary.Text.Contains("unavailable", StringComparison.Ordinal), "An empty inventory is not reported as current.");
        updates = [provider.Item with { State = UpdateState.UpToDate }];
        RenderUpdates();
        Check(UpdateSummary.Text.Contains("up to date", StringComparison.Ordinal), "A fully current inventory is reported correctly.");
        foreach (var state in Enum.GetValues<UpdateState>())
            Check(State(state).Length > 0, "Every update state has a label.");

        var canceled = Confirm("Fixture confirmation", "No real files are affected.", "Continue");
        await CompleteDialogAsync(false);
        Check(!await canceled, "Cancel does not approve an operation.");
        var accepted = Confirm("Fixture confirmation", "No real files are affected.", "Continue");
        await CompleteDialogAsync(true);
        Check(await accepted, "The primary button approves the reviewed operation.");
        var settingsTask = ShowSettings();
        await CompleteDialogAsync(true);
        await settingsTask;
        Check(settings.Load().Settings.ProjectRoots.Count == 0, "Settings save only the fixture state.");

        await TestCleanupAsync(scratch);
        await TestBusyStatesAsync();
        await TestIndividualActionsAsync(scratch);
        var report = Environment.GetEnvironmentVariable("UPKEEP_UI_TEST_REPORT");
        if (report is not null)
            File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(new
            {
                passed = true,
                assertions
            }));
    }

    private async Task TestCleanupAsync(UpKeep.Tests.Scratch scratch)
    {
        for (int index = 0; index < 201; index++)
        {
            var path = scratch.Write($"temporary/{index}.tmp", "fixture");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        }
        var root = new CleanupRoot("fixture", Path.Combine(scratch.Root, "temporary"), RootKind.Temporary, CleanupCategory.TemporaryFiles);
        analysis = await analyzer.AnalyzeAsync(new([root]), new Progress<CleanupProgress>(), CancellationToken.None);
        projectFoldersConfigured = true;
        RenderCleanup();
        Check(analysis.Manifest.Read().Count() == 201, "All old fixture files are analyzed.");
        var group = analysis.Groups.Single();
        SelectGroup(group.Id, true);
        Check(selectedGroups.Contains(group.Id), "An individual group is selectable.");
        SelectGroup(group.Id, false);
        SelectCategory(true, [group], [group], [group.Id]);
        Check(selectedGroups.Contains(group.Id), "Category selection includes eligible groups.");
        var candidate = analysis.Manifest.Read().First();
        selectedFiles.Add(candidate.Id);
        selectedFileMetadata[candidate.Id] = (candidate.LogicalSize, candidate.Group);
        SelectCategory(false, [group], [group], [group.Id]);
        Check(selectedGroups.Count == 0 && selectedFiles.Count == 0, "Clearing a category clears its group and individual-file selections.");
        selectedFileMetadata.Clear();
        ShowCategory(CleanupCategory.TemporaryFiles, AnalyzeButton);
        Check(detailsPanel is not null, "Category details open.");
        ShowFiles(group, AnalyzeButton);
        Check(FindText(detailsPanel!, "Next 200 files"), "Large groups offer a second page.");
        ShowFiles(group, AnalyzeButton, 200);
        Check(!FindText(detailsPanel!, "Next 200 files"), "The last page has no extra next-page action.");
        CloseDetails();
        await CleanSelected();
        Check(CleanupSummary.Text == "Select at least one item.", "Cleanup requires a selection.");
        selectedGroups.Add(group.Id);
        RefreshSelection();
        Check(CleanButton.IsEnabled, "A reviewed selection enables cleanup.");
        var cleanup = CleanSelected();
        await CompleteDialogAsync(true);
        await cleanup;
        Check(!Directory.EnumerateFiles(root.Path).Any(), "Only the fixture's old temporary files were removed.");
        Check(analysis is null && selectedGroups.Count == 0, "Completed cleanup discards its manifest and selection.");
        Check(CleanupSummary.Text.StartsWith("201 items cleaned", StringComparison.Ordinal), "Cleanup reports all completed removals.");
        Check(FindText(detailsPanel!, "Next 200 results"), "Long result lists are paginated.");
        var results = new CleanupResult(Enumerable.Range(0, 201).Select(i => new CleanupItemResult(i.ToString(), "Removed", 1, "Fixture result")).ToArray());
        ShowCleanupResults(results, 200);
        Check(FindText(detailsPanel!, "Previous 200 results"), "The final result page can navigate back.");
        CloseDetails();
        foreach (var category in Enum.GetValues<CleanupCategory>())
        {
            ShowCategory(category, AnalyzeButton);
            Check(detailsPanel is not null, "Every cleanup category has a details surface.");
            CloseDetails();
        }
    }

    private async Task TestBusyStatesAsync()
    {
        scanCancellation = new();
        scanKind = UpdatesView;
        SetBusy(true);
        Check(CheckButton.IsEnabled && !AnalyzeButton.IsEnabled, "Only the active scan offers cancellation.");
        await CheckUpdates();
        Check(scanCancellation.IsCancellationRequested, "Cancel check signals the active scan.");
        scanCancellation.Dispose();
        scanCancellation = new();
        scanKind = CleanupView;
        SetBusy(true);
        await Analyze();
        Check(scanCancellation.IsCancellationRequested, "Cancel analysis signals the active scan.");
        scanCancellation.Dispose();
        scanCancellation = null;
        scanKind = null;
        SetBusy(false);
        Check(!busy && CheckButton.IsEnabled && AnalyzeButton.IsEnabled, "Idle controls are restored.");
        Check(gate.TryEnter(), "The fixture can hold the operation gate.");
        workCancellation = new();
        updating = true;
        await UpdateSelected();
        Check(coordinator.StopRequested && workCancellation.IsCancellationRequested, "Stopping an update cancels queued work.");
        await CleanSelected();
        RequestExit();
        await CompleteDialogAsync(false);
        Check(!exitWhenIdle, "Canceling exit leaves the active operation running.");
        RequestExit();
        await CompleteDialogAsync(true);
        await UntilAsync(() => exitWhenIdle);
        Check(workCancellation.IsCancellationRequested, "Confirmed exit cancels queued work before waiting.");
        exitWhenIdle = false;
        updating = false;
        workCancellation.Dispose();
        workCancellation = null;
        gate.Exit();
        scanCancellation = new();
        busy = true;
        RequestExit();
        await UntilAsync(() => scanCancellation.IsCancellationRequested);
        Check(exitWhenIdle, "Exit during a scan cancels it before closing.");
        exitWhenIdle = false;
        scanCancellation.Dispose();
        scanCancellation = null;
        SetBusy(false);
    }

    private async Task TestIndividualActionsAsync(UpKeep.Tests.Scratch scratch)
    {
        selectedUpdates.Clear();
        await UpdateSelected();
        Check(UpdateSummary.Text == "Select at least one item.", "Updating requires an eligible selection.");
        var unavailable = new FixtureProvider().Item with
        {
            State = UpdateState.CheckUnavailable
        };
        var details = RunItemAction(unavailable);
        await CompleteDialogAsync(false);
        await details;
        Check(!dialogOpen, "Unavailable item details close without changing the clipboard.");

        var provider = new FixtureProvider();
        providers.Clear();
        providers.Add(provider);
        coordinator = new(providers, discovery, journal, gate);
        updates = [provider.Item with { Kind = UpdateKind.RunUpdater, State = UpdateState.CheckUnavailable, Metadata = new(ManagerKind.Claude, new("fixture.exe", [], "fixture")) }];
        selectedUpdates.Add(updates[0].Key);
        var canceled = UpdateSelected();
        await CompleteDialogAsync(false);
        await canceled;
        Check(provider.Mutations == 0, "Declining updater confirmation starts no mutation.");
        updates = [provider.Item];
        await RunItemAction(updates[0]);
        Check(provider.Mutations == 1, "An individual update action runs only its reviewed item.");

        scratch.Write("wsl.exe", "");
        var runner = new DistroRunner();
        discovery = new(new ToolLocator([scratch.Root]), runner, scratch.Root);
        coordinator = new(providers, discovery, journal, gate);
        var distro = new FixtureProvider().Item with
        {
            Name = "FixtureOnly",
            Kind = UpdateKind.CheckEnvironment
        };
        var check = RunItemAction(distro);
        await CompleteDialogAsync(true);
        await check;
        var package = updates.Single(i => i.Key.ProviderId == "wsl-distro:FixtureOnly");
        Check(package.State == UpdateState.UpdateAvailable, "An explicitly selected distro is checked.");
        var declined = ReviewDistroTransactionsAsync([package]);
        await CompleteDialogAsync(false);
        Check(!await declined, "A distro transaction requires confirmation.");
        var approved = ReviewDistroTransactionsAsync([package]);
        await CompleteDialogAsync(true);
        Check(await approved, "An unchanged distro preview can be approved.");
        runner.FailPreview = true;
        Check(!await ReviewDistroTransactionsAsync([package]), "Failed previews cannot approve a transaction.");

        scratch.Write("uv.exe", "");
        discovery = new(new ToolLocator([scratch.Root]), new CacheRunner(scratch.Root), scratch.Root);
        managed = new(discovery, () => false);
        actions = await managed.DiscoverAsync(CancellationToken.None);
        var action = actions.Single();
        ShowCategory(CleanupCategory.PackageCaches, AnalyzeButton);
        Check(detailsPanel is not null, "Managed cleanup actions appear in category details.");
        CloseDetails();
        SelectAction(action.Id, true);
        Check(selectedActions.Contains(action.Id), "Managed actions can be selected.");
        SelectAction(action.Id, false);
        Check(selectedActions.Count == 0, "Managed actions can be deselected.");
        workCancellation = new();
        var outcomes = new List<CleanupItemResult>();
        await ExecuteManagedActionsAsync([action], outcomes);
        Check(outcomes.Single().State == "Completed", "A reviewed managed action reports completion.");
        workCancellation.Dispose();
        workCancellation = null;
        actions = [];

        var protectedCheck = AnalyzeProtected();
        await CompleteDialogAsync(false);
        await protectedCheck;
        Check(!busy, "Declining protected cleanup analysis does not request elevation.");
        Navigation.SelectedItem = Navigation.MenuItems[1];
        Check(CleanupPanel.Visibility == Visibility.Visible, "Cleanup navigation selects the cleanup surface.");
        Navigation.SelectedItem = Navigation.MenuItems[0];
        Check(UpdatesPanel.Visibility == Visibility.Visible, "Update navigation restores the update surface.");
        OnHide(this, new RoutedEventArgs());
        Check(!AppWindow.IsVisible, "The close control hides the panel.");
        TogglePanel();
        Check(AppWindow.IsVisible, "The tray action reopens a hidden panel.");
    }

    private void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
        assertions++;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private async Task CompleteDialogAsync(bool accept)
    {
        ContentDialog? dialog = null;
        await UntilAsync(() => (dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).OfType<ContentDialog>().FirstOrDefault()) is not null);
        if (!accept)
        {
            dialog!.Hide();
            return;
        }
        Button? button = null;
        await UntilAsync(() => (button = Descendants(dialog!).OfType<Button>().FirstOrDefault(b => b.Name == "PrimaryButton")) is not null);
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button!);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static bool FindText(DependencyObject parent, string text) => ((Grid)parent).Children.OfType<ListView>()
        .SelectMany(list => list.Items).OfType<Button>().Any(button => button.Content is string content && content == text);
    internal void CloseTestWindow()
    {
        exiting = true;
        Close();
    }

    private sealed class NoProcesses : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutput>? output, CancellationToken cancellationToken)
            => throw new InvalidOperationException("UI fixture must not launch external processes.");
    }

    private sealed class DistroRunner : IProcessRunner
    {
        public bool FailPreview
        {
            get; set;
        }
        public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutput>? output, CancellationToken cancellationToken)
        {
            if (request.Kind == ExecutionKind.Mutation)
                throw new InvalidOperationException("This UI test only previews distro changes.");
            var preview = request.Arguments.Contains("--simulate");
            return Task.FromResult(new ProcessResult(preview && FailPreview ? 1 : 0,
                preview ? "Inst fixture (2 fixture)" : request.Arguments.Contains("/usr/bin/apt")
                    ? "fixture/stable 2 amd64 [upgradable from: 1]" : "All snaps up to date", ""));
        }
    }

    private sealed class CacheRunner(string root) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutput>? output, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessResult(0, root, ""));
    }

    private sealed class FixtureProvider : IUpdateProvider
    {
        public string Id => "fixture";
        public int Mutations
        {
            get; private set;
        }
        public UpdateItem Item => new(new(Id, "Fixture", "package", "fixture-package"), "Fixture package", Mutations == 0 ? "1" : "2", "2", Mutations == 0 ? UpdateState.UpdateAvailable : UpdateState.UpToDate, "Fixture only");
        public Task<ProviderCheckResult> CheckAsync(DiscoveryContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ProviderCheckResult([Item, Item with { Key = new(Id, "Fixture", "other", "other"), State = UpdateState.CheckUnavailable }], true));
        public Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        {
            Mutations++;
            return Task.FromResult(new UpdateResult(UpdateState.CompletedUnverified, "Fixture update completed"));
        }
        public Task<VerificationResult> VerifyAsync(UpdateRequest request, CancellationToken cancellationToken) => Task.FromResult(new VerificationResult(Mutations == 1, "2"));
    }
}
