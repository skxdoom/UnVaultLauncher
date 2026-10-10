using Avalonia.Headless.XUnit;
using UnVault.App.Services;
using UnVault.App.ViewModels;
using UnVault.Core.Epic;
using UnVault.Core.Install;

namespace UnVault.App.Tests;

/// <summary>Two operations never work on the same engine or Library item at once: they'd write the same files.</summary>
public sealed class OperationTests : IDisposable
{
    /// <summary>A history of each test's own: a shared one would carry what one test leaves into the next.</summary>
    private readonly string _history = Path.Combine(Path.GetTempPath(), $"unvault-history-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_history);

    private MainViewModel WithEngines()
    {
        var viewModel = new MainViewModel(new AppServices(), _history);
        viewModel.Settings.EngineInstallRoot = @"E:\Epic Games"; // set, so nothing asks this PC's Epic Games Launcher
        viewModel.SetEngines(
            [.. new[] { "UE_5.7", "UE_5.6", "UE_5.5" }.Select(app => new LocalEngine(app, $@"E:\Epic Games\{app}", "build", 1, LocalInstallKind.UnVault))],
            [.. new[] { "UE_5.8", "UE_5.7", "UE_5.6", "UE_5.5" }.Select(app => new EpicAsset { AppName = app, BuildVersion = "build", Namespace = "ue", CatalogItemID = "test" })]);
        return viewModel;
    }

    /// <summary>An operation that works until it's paused. None completes here: a finished one refreshes from this PC.</summary>
    private sealed class Started(MainViewModel owner, string title, string[] engines, string? item = null)
    {
        public OperationViewModel Operation { get; } = Add(owner, new OperationViewModel(owner, title,
            async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            }, engines, item));

        public Task Run { get; private set; } = Task.CompletedTask;

        private static OperationViewModel Add(MainViewModel owner, OperationViewModel operation)
        {
            owner.Operations.Insert(0, operation);
            return operation;
        }

        public Started Start()
        {
            Run = Operation.RunAsync();
            return this;
        }

        /// <summary>Not awaited: a run only ends once paused (one that has to wait returns at once).</summary>
        public void Retry() => Run = Operation.RunAsync();

        public async Task PauseAsync()
        {
            Operation.PauseCommand.Execute(null);
            await Run;
        }
    }

    private static bool Busy(MainViewModel viewModel, string app) => viewModel.Engines.Single(e => e.AppName == app).IsBusy;

    [AvaloniaFact]
    public async Task A_second_operation_on_the_same_engine_waits_for_the_first()
    {
        var viewModel = WithEngines();
        var modify = new Started(viewModel, "Changing components of Unreal Engine 5.7", ["UE_5.7"]).Start();
        var plugin = new Started(viewModel, "Installing Greybox Tools into UE 5.7", ["UE_5.7"], "fab:greybox").Start();
        Assert.True(plugin.Run.IsCompleted, "it waits instead of starting");

        Assert.Equal(OperationState.Running, modify.Operation.State);
        Assert.Equal(OperationState.Cancelled, plugin.Operation.State); // never started; ready to retry
        Assert.Equal(@"""Changing components of Unreal Engine 5.7"" is working on the same files. Retry once it's done.", plugin.Operation.Message);
        Assert.True(Busy(viewModel, "UE_5.7"));

        // Retried too early: still waits. Once the other is done, it runs.
        plugin.Retry();
        Assert.Equal(OperationState.Cancelled, plugin.Operation.State);
        await modify.PauseAsync();
        Assert.False(Busy(viewModel, "UE_5.7"));
        plugin.Retry();
        Assert.Equal(OperationState.Running, plugin.Operation.State);
        Assert.True(Busy(viewModel, "UE_5.7"));
        await plugin.PauseAsync();
    }

    [AvaloniaFact]
    public async Task Two_operations_on_one_Library_item_never_run_at_once()
    {
        var viewModel = WithEngines();
        var download = new Started(viewModel, "Downloading Garden Pack", [], "fab:garden").Start();
        var add = new Started(viewModel, "Adding Garden Pack to Hillside", [], "fab:garden").Start();
        Assert.True(add.Run.IsCompleted, "it waits instead of starting");

        Assert.Equal((OperationState.Running, OperationState.Cancelled), (download.Operation.State, add.Operation.State));
        await download.PauseAsync();
    }

    /// <summary>An update of a plugin in several engines holds all of them, and one operation ending frees only its own.</summary>
    [AvaloniaFact]
    public async Task An_engine_stays_busy_until_the_last_operation_on_it_ends()
    {
        var viewModel = WithEngines();
        var update = new Started(viewModel, "Updating Greybox Tools", ["UE_5.7", "UE_5.6"], "fab:greybox").Start();
        var verify = new Started(viewModel, "Verifying Unreal Engine 5.5", ["UE_5.5"]).Start();
        Assert.True(Busy(viewModel, "UE_5.7") && Busy(viewModel, "UE_5.6") && Busy(viewModel, "UE_5.5"));

        await verify.PauseAsync();
        Assert.Equal([true, true, false], new[] { "UE_5.7", "UE_5.6", "UE_5.5" }.Select(app => Busy(viewModel, app)));
        Assert.False(viewModel.Engines.Single(e => e.AppName == "UE_5.6").VerifyCommand.CanExecute(null));

        await update.PauseAsync();
        Assert.All(viewModel.Engines, e => Assert.False(e.IsBusy));
    }

    /// <summary>Offered again while paused, a version could be installed twice into the same folder; put in the history, it's offered again.</summary>
    [AvaloniaFact]
    public async Task A_paused_install_keeps_its_version_out_of_the_install_list()
    {
        var viewModel = WithEngines();
        Assert.Equal(["UE_5.8"], viewModel.InstallableEngines.Select(e => e.AppName));

        var install = new Started(viewModel, "Installing Unreal Engine 5.8", ["UE_5.8"]).Start();
        Assert.Empty(viewModel.InstallableEngines);
        await install.PauseAsync();
        Assert.Empty(viewModel.InstallableEngines); // waiting for Retry

        install.Operation.MoveToHistoryCommand.Execute(null);
        Assert.Equal(["UE_5.8"], viewModel.InstallableEngines.Select(e => e.AppName));
    }

    /// <summary>Done ones go into the history at once, with what they said; it's still there after a restart.</summary>
    [AvaloniaFact]
    public async Task A_finished_operation_goes_into_the_history_and_stays_there()
    {
        var viewModel = WithEngines();
        bool repaired = false;
        var verify = new OperationViewModel(viewModel, "Verifying Unreal Engine 5.6", (operation, _, _) =>
        {
            operation.OfferFollowUp("Repair", () => repaired = true);
            return Task.FromResult<string?>("3 of 120 files are missing or damaged.");
        }, ["UE_5.6"]);
        viewModel.Operations.Insert(0, verify);
        try
        {
            await verify.RunAsync();

            Assert.Empty(viewModel.Operations);
            var entry = Assert.Single(viewModel.History);
            Assert.Equal(("Verifying Unreal Engine 5.6", OperationOutcome.Completed, "3 of 120 files are missing or damaged."),
                (entry.Title, entry.Record.Outcome, entry.Message));
            // The next step it offered goes with it, once.
            Assert.True(entry.HasFollowUp);
            entry.RunFollowUpCommand.Execute(null);
            Assert.True(repaired);
            Assert.False(entry.HasFollowUp);

            var restarted = new MainViewModel(new AppServices(), _history);
            Assert.Equal(["Verifying Unreal Engine 5.6"], restarted.History.Select(h => h.Title));
            Assert.False(restarted.History[0].HasFollowUp); // what it would run isn't kept
        }
        finally
        {
            viewModel.ClearHistoryCommand.Execute(null);
        }
        Assert.Empty(new MainViewModel(new AppServices(), _history).History);
    }

    /// <summary>Paused and failed ones stay in progress until resumed, or put in the history by hand.</summary>
    [AvaloniaFact]
    public async Task Paused_and_failed_operations_wait_until_moved_to_the_history()
    {
        var viewModel = WithEngines();
        var paused = new Started(viewModel, "Downloading Garden Pack", [], "fab:garden").Start();
        await paused.PauseAsync();
        var failed = new OperationViewModel(viewModel, "Installing Greybox Tools into UE 5.7",
            (_, _, _) => throw new InstallException("The disk is full."), ["UE_5.7"]);
        viewModel.Operations.Insert(0, failed);
        await failed.RunAsync();
        try
        {
            Assert.Equal(["Retry", "Resume"], viewModel.Operations.Select(o => o.RetryText));
            Assert.Empty(viewModel.History);

            paused.Operation.MoveToHistoryCommand.Execute(null);
            failed.MoveToHistoryCommand.Execute(null);

            Assert.Empty(viewModel.Operations);
            Assert.Equal(
                [("Installing Greybox Tools into UE 5.7", OperationOutcome.Failed, "The disk is full."),
                 ("Downloading Garden Pack", OperationOutcome.Paused, "Stopped before it finished.")],
                viewModel.History.Select(h => (h.Title, h.Record.Outcome, h.Message)));
        }
        finally
        {
            viewModel.ClearHistoryCommand.Execute(null);
        }
    }

    [AvaloniaFact]
    public async Task The_Downloads_tab_shows_a_dot_while_something_runs()
    {
        var viewModel = WithEngines();
        Assert.False(viewModel.HasRunningOperations);
        var download = new Started(viewModel, "Downloading Garden Pack", [], "fab:garden").Start();
        Assert.True(viewModel.HasRunningOperations);
        await download.PauseAsync();
        Assert.False(viewModel.HasRunningOperations); // paused: nothing is going on
    }

    [Fact]
    public void The_saved_history_keeps_the_newest()
    {
        string path = Path.Combine(Path.GetTempPath(), $"unvault-history-{Guid.NewGuid():N}.json");
        try
        {
            var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
            OperationHistory.Save(Enumerable.Range(0, OperationHistory.MaxEntries + 20)
                .Select(i => new OperationRecord($"Download {i}", OperationOutcome.Completed, null, start.AddMinutes(-i))), path);

            var loaded = OperationHistory.Load(path);
            Assert.Equal(OperationHistory.MaxEntries, loaded.Count);
            Assert.Equal("Download 0", loaded[0].Title);

            File.WriteAllText(path, "{ damaged");
            Assert.Empty(OperationHistory.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Nothing resumes after a restart, so closing the app puts what's unfinished into the history, newest on top.</summary>
    [AvaloniaFact]
    public async Task Closing_the_app_puts_unfinished_operations_into_the_history()
    {
        var viewModel = WithEngines();
        var paused = new Started(viewModel, "Downloading Garden Pack", [], "fab:garden").Start();
        await paused.PauseAsync();
        var failed = new OperationViewModel(viewModel, "Installing Greybox Tools into UE 5.7",
            (_, _, _) => throw new InstallException("The disk is full."), ["UE_5.7"]);
        viewModel.Operations.Insert(0, failed);
        await failed.RunAsync();
        var running = new Started(viewModel, "Installing Unreal Engine 5.8", ["UE_5.8"]).Start();
        try
        {
            viewModel.RecordUnfinished();

            Assert.Empty(viewModel.Operations);
            Assert.Equal(
                [("Installing Unreal Engine 5.8", OperationOutcome.Paused, "Stopped before it finished."),
                 ("Installing Greybox Tools into UE 5.7", OperationOutcome.Failed, "The disk is full."),
                 ("Downloading Garden Pack", OperationOutcome.Paused, "Stopped before it finished.")],
                viewModel.History.Select(h => (h.Title, h.Record.Outcome, h.Message)));
            Assert.Equal(3, new MainViewModel(new AppServices(), _history).History.Count); // saved
        }
        finally
        {
            await running.PauseAsync();
            viewModel.ClearHistoryCommand.Execute(null);
        }
    }
}
