using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using UnVault.Core.Install;
using UnVault.Core.Util;

namespace UnVault.CLI.Commands;

internal sealed class ModifyCommand : EpicCommand<ModifyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Installed engine (e.g. UE_5.5) or install folder.")]
        public string Target { get; init; } = "";

        [CommandOption("-a|--add <COMPONENTS>")]
        [Description("Components to add, comma-separated.")]
        public string? Add { get; init; }

        [CommandOption("-r|--remove <COMPONENTS>")]
        [Description("Components to remove, comma-separated. Files another installed component needs are kept.")]
        public string? Remove { get; init; }

        [CommandOption("-c|--connections <N>")]
        [Description("Parallel chunk downloads. Default: from settings (16).")]
        public int? Connections { get; init; }

        [CommandOption("-y|--yes")]
        [Description("Don't ask for confirmation.")]
        public bool Yes { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var install = InstallLocator.Find(settings.Target);
        if (install is null)
        {
            AnsiConsole.MarkupLine($"[red]No install found for[/] {Markup.Escape(settings.Target)}. Run [blue]{Services.CommandName} installs[/] to see what's there.");
            return 1;
        }

        var newTags = install.InstallTags.Union(SplitTags(settings.Add)).Except(SplitTags(settings.Remove)).ToHashSet(StringComparer.Ordinal);
        var plan = InstallWorkflow.PlanModify(install, newTags);

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(install.Name)}[/] {Markup.Escape(install.Manifest.Meta.BuildVersion)} at {Markup.Escape(install.Directory)} [grey]({Markup.Escape(install.Origin)})[/]");
        AnsiConsole.MarkupLine($"Components now:   core{Describe(install.InstallTags)}");
        AnsiConsole.MarkupLine($"Components after: core{Describe(newTags)}");

        if (plan.IsEmpty)
        {
            AnsiConsole.MarkupLine("[green]Nothing to change.[/]");
            return 0;
        }

        if (plan.ToRemove.Count > 0)
            AnsiConsole.MarkupLine($"[red]Delete[/] {plan.ToRemove.Count:N0} files, freeing up to {ByteSize.Format(plan.BytesFreed)}");
        if (plan.ToAdd is { } add)
            AnsiConsole.MarkupLine($"[green]Add[/] {add.Files.Count:N0} files: {ByteSize.Format(add.InstallBytes)} on disk, {ByteSize.Format(add.DownloadBytes)} to download");
        if (install.IsEGLOnly)
            AnsiConsole.MarkupLine("[yellow]Note:[/] this engine was installed by the Epic Games Launcher. If EGL later verifies or updates it, " +
                                   "EGL may bring back components removed here; manage it with UnVault Launcher from now on.");

        if (!settings.Yes && !await AnsiConsole.ConfirmAsync("Apply these changes?", defaultValue: false, cancellationToken: cancellationToken))
            return 0;

        var status = new InstallStatus();
        var installer = new Installer(Services.HTTP) { MaxParallelDownloads = Math.Clamp(settings.Connections ?? Services.Settings.ParallelDownloads, 1, 64) };
        InstallCleaner.Result cleaned = default;
        if (plan.ToAdd is null)
            cleaned = await AnsiConsole.Status().StartAsync("Deleting files…", _ => InstallWorkflow.ApplyModifyAsync(plan, installer, status, cancellationToken));
        else
            await ProgressDisplay.RunInstallAsync(status, async () => cleaned = await InstallWorkflow.ApplyModifyAsync(plan, installer, status, cancellationToken));

        if (cleaned.FilesDeleted > 0)
            AnsiConsole.MarkupLine($"Deleted {cleaned.FilesDeleted:N0} files and {cleaned.FoldersRemoved:N0} empty folders, freed {ByteSize.Format(cleaned.BytesFreed)}.");
        return 0;
    }

    private static HashSet<string> SplitTags(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    private static string Describe(IEnumerable<string> tags)
    {
        var list = tags.Order(StringComparer.Ordinal).ToList();
        return list.Count == 0 ? "" : ", " + Markup.Escape(string.Join(", ", list));
    }
}
