using Avalonia.Headless.XUnit;
using UnVault.App.Services;
using UnVault.App.ViewModels;
using UnVault.Core.Epic;
using UnVault.Core.Install;

namespace UnVault.App.Tests;

/// <summary>Two operations never work on the same engine or Library item at once: they'd write the same files.</summary>
public class OperationTests
{
    private static MainViewModel WithEngines()
    {
        var viewModel = new MainViewModel(new AppServices());
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
            Operation.CancelCommand.Execute(null);
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

    /// <summary>Offered again while paused, a version could be installed twice into the same folder.</summary>
    [AvaloniaFact]
    public async Task A_paused_install_keeps_its_version_out_of_the_install_list()
    {
        var viewModel = WithEngines();
        Assert.Equal(["UE_5.8"], viewModel.InstallableEngines.Select(e => e.AppName));

        var install = new Started(viewModel, "Installing Unreal Engine 5.8", ["UE_5.8"]).Start();
        Assert.Empty(viewModel.InstallableEngines);
        await install.PauseAsync();
        Assert.Empty(viewModel.InstallableEngines); // waiting for Retry

        install.Operation.DismissCommand.Execute(null);
        Assert.Equal(["UE_5.8"], viewModel.InstallableEngines.Select(e => e.AppName));
    }
}
