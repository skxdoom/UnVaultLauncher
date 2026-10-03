using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Unvault.Core.Install;
using Unvault.Core.Manifests;
using Unvault.Core.Util;

namespace Unvault.CLI.Commands;

internal sealed class VerifyCommand : EpicCommand<VerifyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[target]")]
        [Description("Installed app (e.g. UE_5.7) or install folder.")]
        public string? Target { get; init; }

        [CommandOption("-d|--dir <DIR>")]
        [Description("Install folder (instead of a target name).")]
        public string? Directory { get; init; }

        [CommandOption("--only <GLOB>")]
        [Description("Check only files matching this pattern, e.g. \"Engine/Binaries/**\".")]
        public string? Only { get; init; }

        [CommandOption("--repair")]
        [Description("Re-download files that are missing or damaged.")]
        public bool Repair { get; init; }

        [CommandOption("--manifest <FILE>")]
        [Description("Check against this manifest file (with --dir).")]
        public string? Manifest { get; init; }

        [CommandOption("--base-url <URL>")]
        [Description("CloudDir URL for --repair when the install doesn't record one. Repeatable.")]
        public string[] BaseURLs { get; init; } = [];
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var install = InstallLocator.Find(settings.Target, settings.Directory, settings.Manifest, settings.BaseURLs);
        if (install is null)
        {
            AnsiConsole.MarkupLine($"[red]No install found.[/] Give an installed app name (see [blue]{Services.CommandName} installs[/]) or a folder installed by Unvault Launcher.");
            return 1;
        }

        var only = InstallLocator.ParseFilter(settings.Only);
        var files = install.SelectFiles(only).ToList();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(install.Name)}[/] {Markup.Escape(install.Manifest.Meta.BuildVersion)} at {Markup.Escape(install.Directory)} [grey]({Markup.Escape(install.Origin)})[/]");
        AnsiConsole.MarkupLine($"Components: core{(install.InstallTags.Count > 0 ? ", " + Markup.Escape(string.Join(", ", install.InstallTags.Order())) : "")}" +
                               (only is not null ? $" [yellow](only {Markup.Escape(settings.Only!)})[/]" : ""));

        var bad = await CheckAsync(files, install.Directory, cancellationToken);
        if (bad.Count == 0 || !settings.Repair)
            return bad.Count == 0 ? 0 : 1;

        if (install.Sources.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Can't repair: no download location known for this install. Use --base-url.[/]");
            return 1;
        }

        var plan = InstallPlan.Create(install.Manifest, bad.Select(b => b.File));
        AnsiConsole.MarkupLine($"Repairing {plan.Files.Count:N0} files ({ByteSize.Format(plan.DownloadBytes)} to download)…");
        var status = new InstallStatus();
        await ProgressDisplay.RunInstallAsync(status,
            () => new Installer(Services.HTTP) { MaxParallelDownloads = Services.Settings.ParallelDownloads }.InstallAsync(plan, install.Directory, install.Sources, new Dictionary<string, string>(), status, cancellationToken));

        return await VerifyAndReportAsync(plan.Files, install.Directory, cancellationToken) ? 0 : 1;
    }

    /// <summary>Verifies files and prints a summary. Returns true when all are good.</summary>
    public static async Task<bool> VerifyAndReportAsync(IReadOnlyList<FileManifest> files, string directory, CancellationToken cancellationToken) =>
        (await CheckAsync(files, directory, cancellationToken)).Count == 0;

    private static async Task<IReadOnlyList<BadFile>> CheckAsync(IReadOnlyList<FileManifest> files, string directory, CancellationToken cancellationToken)
    {
        var status = new InstallStatus();
        IReadOnlyList<BadFile> bad = [];
        await ProgressDisplay.RunVerifyAsync(status, async () =>
            bad = await Verifier.FindBadFilesAsync(files, directory, status, cancellationToken: cancellationToken));

        if (bad.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]All {files.Count:N0} files OK[/] ({ByteSize.Format(files.Sum(f => f.FileSize))}).");
            return bad;
        }

        var table = new Table().AddColumn("Problem").AddColumn("File").AddColumn(new TableColumn("Size").RightAligned());
        foreach (var file in bad.Take(30))
            table.AddRow(file.Problem.ToString(), Markup.Escape(file.File.Filename), ByteSize.Format(file.File.FileSize));
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[red]{bad.Count:N0} of {files.Count:N0} files have problems[/]" + (bad.Count > 30 ? " (first 30 shown)" : "") + ".");
        return bad;
    }
}
