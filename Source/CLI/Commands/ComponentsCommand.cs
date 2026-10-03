using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Unvault.Core.EGL;
using Unvault.Core.Install;
using Unvault.Core.Manifests;
using Unvault.Core.Util;

namespace Unvault.CLI.Commands;

internal sealed class ComponentsCommand : EpicCommand<ComponentsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<app>")]
        [Description("Engine, e.g. UE_5.7. Uses the local install if there is one, otherwise the latest build from Epic.")]
        public string AppName { get; init; } = "";

        [CommandOption("--remote")]
        [Description("Use the latest build from Epic even if the engine is installed locally.")]
        public bool Remote { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var install = settings.Remote ? null : InstallLocator.Find(settings.AppName);
        if (install is not null)
        {
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(install.Name)}[/] {Markup.Escape(install.Manifest.Meta.BuildVersion)} at {Markup.Escape(install.Directory)} [grey]({Markup.Escape(install.Origin)})[/]");
            WriteTable(install.Manifest, install.InstallTags);
            return 0;
        }

        var staleItem = EGLInstallations.ReadItems()
            .FirstOrDefault(i => string.Equals(i.AppName, settings.AppName, StringComparison.OrdinalIgnoreCase));
        if (staleItem is not null && !settings.Remote)
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(staleItem.AppName)} has an EGL record but no files on disk; showing the latest build from Epic instead.[/]");

        var downloaded = await AnsiConsole.Status().StartAsync($"Fetching {settings.AppName} manifest from Epic…", async _ =>
        {
            var assets = await Services.API.GetAssetsAsync(cancellationToken: cancellationToken);
            var asset = assets.FirstOrDefault(a => string.Equals(a.AppName, settings.AppName, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
                return null;
            var build = await Services.API.GetBuildInfoAsync(asset.Namespace, asset.CatalogItemID, asset.AppName, cancellationToken: cancellationToken);
            return await Services.API.DownloadManifestAsync(build, cancellationToken);
        });

        if (downloaded is null)
        {
            AnsiConsole.MarkupLine($"[red]Your account doesn't own an app named[/] {Markup.Escape(settings.AppName)}. Run [blue]{Services.CommandName} owned[/] to see what it does.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(downloaded.Manifest.Meta.AppName)}[/] {Markup.Escape(downloaded.Manifest.Meta.BuildVersion)} [grey](latest from Epic, not installed)[/]");
        WriteTable(downloaded.Manifest, new HashSet<string>());
        return 0;
    }

    private static void WriteTable(Manifest manifest, IReadOnlySet<string> installed)
    {
        var current = manifest.MeasureSelection(installed);
        string label = installed.Count == 0 && current.FileCount > 0 ? "Core only" : "Current selection";
        AnsiConsole.MarkupLine($"{label}: {ByteSize.Format(current.InstallBytes)} on disk ({ByteSize.Format(current.DownloadBytes)} download), {current.FileCount:N0} files");

        var table = new Table()
            .AddColumn("Component").AddColumn("Status")
            .AddColumn(new TableColumn("Disk change").RightAligned())
            .AddColumn(new TableColumn("Download").RightAligned());

        foreach (var impact in ComponentPlanner.Analyze(manifest, installed).OrderByDescending(i => i.DiskBytes))
        {
            table.AddRow(
                Markup.Escape(impact.Tag),
                impact.Installed ? "[green]installed[/]" : "[grey]not installed[/]",
                impact.Installed ? $"[green]−{ByteSize.Format(impact.DiskBytes)}[/] if removed" : $"+{ByteSize.Format(impact.DiskBytes)} if added",
                impact.Installed ? "" : ByteSize.Format(impact.DownloadBytes));
        }
        AnsiConsole.Write(table);
    }
}
