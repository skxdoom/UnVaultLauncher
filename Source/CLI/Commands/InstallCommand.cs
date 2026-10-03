using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Unvault.Core.Install;
using Unvault.Core.Manifests;
using Unvault.Core.Util;

namespace Unvault.CLI.Commands;

internal sealed class InstallCommand : EpicCommand<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<app>")]
        [Description("Engine to install, e.g. UE_5.8.")]
        public string AppName { get; init; } = "";

        [CommandOption("-d|--dir <DIR>")]
        [Description("Install folder. Default: next to your existing engines, e.g. E:\\Epic Games\\UE_5.8.")]
        public string? Directory { get; init; }

        [CommandOption("-w|--with <COMPONENTS>")]
        [Description("Optional components, comma-separated (e.g. templates,engine_source). Run 'components <app>' to see them. Core is always installed.")]
        public string? With { get; init; }

        [CommandOption("--only <GLOB>")]
        [Description("Install only files matching this pattern, e.g. \"Engine/Build/**\". For testing.")]
        public string? Only { get; init; }

        [CommandOption("-c|--connections <N>")]
        [Description("Parallel chunk downloads. Default: from settings (16).")]
        public int? Connections { get; init; }

        [CommandOption("--verify")]
        [Description("Check every written file's hash afterwards.")]
        public bool Verify { get; init; }

        [CommandOption("-y|--yes")]
        [Description("Don't ask for confirmation.")]
        public bool Yes { get; init; }

        [CommandOption("--no-register")]
        [Description("Don't add the engine to LauncherInstalled.dat (UE tools then won't find it).")]
        public bool NoRegister { get; init; }

        [CommandOption("--manifest <FILE>")]
        [Description("Use a local manifest instead of asking Epic (offline/testing). Needs --base-url unless EGL has the app.")]
        public string? Manifest { get; init; }

        [CommandOption("--base-url <URL>")]
        [Description("CloudDir URL to fetch chunks from (with --manifest). Repeatable.")]
        public string[] BaseURLs { get; init; } = [];
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var source = settings.Manifest is not null
            ? InstallWorkflow.FromLocalManifest(await File.ReadAllBytesAsync(settings.Manifest, cancellationToken), settings.BaseURLs)
            : await AnsiConsole.Status().StartAsync($"Fetching {settings.AppName} from Epic…",
                _ => InstallWorkflow.FetchLatestAsync(Services.API, settings.AppName, cancellationToken));
        var manifest = source.Manifest;

        var tags = (settings.With ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var only = InstallLocator.ParseFilter(settings.Only);
        var plan = InstallWorkflow.PlanInstall(source, tags, only);
        if (plan.Files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to install: no files match.[/]");
            return 1;
        }

        string directory = Path.GetFullPath(settings.Directory ?? InstallLocator.DefaultInstallDirectory(manifest.Meta.AppName, Services.Settings));

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(manifest.Meta.AppName)}[/] {Markup.Escape(manifest.Meta.BuildVersion)} → {Markup.Escape(directory)}");
        AnsiConsole.MarkupLine($"Components: core{(tags.Count > 0 ? ", " + Markup.Escape(string.Join(", ", tags.Order())) : "")}" +
                               (only is not null ? $" [yellow](only {Markup.Escape(settings.Only!)})[/]" : ""));
        AnsiConsole.MarkupLine($"{plan.Files.Count:N0} files, {ByteSize.Format(plan.InstallBytes)} on disk, {ByteSize.Format(plan.DownloadBytes)} to download ({plan.Chunks.Count:N0} chunks)");

        long free = new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace;
        if (free < plan.InstallBytes)
            AnsiConsole.MarkupLine($"[red]Warning:[/] only {ByteSize.Format(free)} free on {Markup.Escape(Path.GetPathRoot(directory)!)}.");

        if (!settings.Yes && !await AnsiConsole.ConfirmAsync("Start?", cancellationToken: cancellationToken))
            return 0;

        var status = new InstallStatus();
        var installer = new Installer(Services.HTTP) { MaxParallelDownloads = Math.Clamp(settings.Connections ?? Services.Settings.ParallelDownloads, 1, 64) };
        bool register = !settings.NoRegister;
        await ProgressDisplay.RunInstallAsync(status,
            () => InstallWorkflow.InstallAsync(source, plan, tags, settings.Only, directory, installer, status, register, cancellationToken));

        if (register && only is null && InstallWorkflow.IsEngine(manifest))
            AnsiConsole.MarkupLine($"Registered for Unreal Engine tools: projects associated with [bold]{Markup.Escape(manifest.Meta.AppName[3..])}[/] now open with this install.");

        if (settings.Verify)
            return await VerifyCommand.VerifyAndReportAsync(plan.Files, directory, cancellationToken) ? 0 : 1;
        return 0;
    }
}
