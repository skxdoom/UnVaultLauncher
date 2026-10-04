using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using UnVault.Core.EGL;
using UnVault.Core.Epic;
using UnVault.Core.Util;

namespace UnVault.CLI.Commands;

internal sealed class FabCommand : EpicCommand<FabCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-s|--search <TEXT>")]
        [Description("Only items whose title contains this text.")]
        public string? Search { get; init; }

        [CommandOption("-e|--engine <APP>")]
        [Description("Only items with a version for this engine, e.g. UE_5.7.")]
        public string? Engine { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var library = await AnsiConsole.Status().StartAsync("Reading your Fab library…",
            context => Services.API.GetFabLibraryAsync(new Progress<int>(n => context.Status($"Reading your Fab library… {n:N0} items")), cancellationToken));

        // Which artifacts EGL installed, and into which engine folder.
        var installedByArtifact = EGLInstallations.ReadLauncherInstalled()
            .GroupBy(e => e.ArtifactID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => Path.GetFileName(e.InstallLocation)).ToList(), StringComparer.OrdinalIgnoreCase);

        var items = library
            .Where(i => settings.Search is null || i.Title.Contains(settings.Search, StringComparison.OrdinalIgnoreCase))
            .Where(i => settings.Engine is null || i.EngineVersions.Contains(settings.Engine, StringComparer.OrdinalIgnoreCase))
            .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var table = new Table()
            .AddColumn("Title").AddColumn("Type").AddColumn("Engine versions").AddColumn("Installed into");

        foreach (var item in items)
        {
            var installedInto = (item.ProjectVersions ?? [])
                .SelectMany(v => installedByArtifact.GetValueOrDefault(v.ArtifactID) ?? [])
                .Distinct()
                .ToList();

            table.AddRow(
                Markup.Escape(item.Title),
                Markup.Escape(item.DistributionMethod ?? ""),
                Markup.Escape(string.Join(", ", item.EngineVersions.Select(v => v.Replace("UE_", "")))),
                installedInto.Count > 0 ? $"[green]{Markup.Escape(string.Join(", ", installedInto))}[/]" : "");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{items.Count} of {library.Count} library items shown. Use [blue]{Services.CommandName} fab-files <artifact>[/] to look inside one.[/]");
        return 0;
    }
}

internal sealed class FabFilesCommand : EpicCommand<FabFilesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<artifact>")]
        [Description("Artifact ID (one engine version of a Fab item), e.g. MyPlugin1a2b3c4d5e6fV3. Partial titles work too.")]
        public string Artifact { get; init; } = "";

        [CommandOption("-p|--platform <PLATFORM>")]
        [DefaultValue("Windows")]
        public string Platform { get; init; } = "Windows";
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var library = await AnsiConsole.Status().StartAsync("Reading your Fab library…",
            _ => Services.API.GetFabLibraryAsync(cancellationToken: cancellationToken));

        var match = FindArtifact(library, settings.Artifact);
        if (match is null)
        {
            AnsiConsole.MarkupLine($"[red]No artifact or title matching[/] {Markup.Escape(settings.Artifact)} [red]in your Fab library.[/]");
            return 1;
        }
        var (item, version) = match.Value;

        var downloaded = await AnsiConsole.Status().StartAsync($"Fetching manifest for {version.ArtifactID}…", async _ =>
        {
            var info = await Services.API.GetFabDownloadInfoAsync(version.ArtifactID, item.AssetNamespace, item.AssetID, settings.Platform, cancellationToken);
            return (Info: info, Manifest: await Services.API.DownloadFabManifestAsync(info, cancellationToken));
        });

        var manifest = downloaded.Manifest.Manifest;
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(item.Title)}[/] — {Markup.Escape(version.ArtifactID)} for {Markup.Escape(string.Join(", ", version.EngineVersions ?? []))}");
        AnsiConsole.MarkupLine($"Format: {Markup.Escape(downloaded.Info.AssetFormat ?? "?")}, build {Markup.Escape(manifest.Meta.BuildVersion)}, feature level {manifest.FeatureLevel}");
        AnsiConsole.MarkupLine($"{manifest.Files.Count:N0} files, {ByteSize.Format(manifest.Files.Sum(f => f.FileSize))} on disk, {ByteSize.Format(manifest.Chunks.Sum(c => c.FileSize))} download");

        var folders = new Table().Title("Top-level folders in the manifest")
            .AddColumn("Folder").AddColumn(new TableColumn("Files").RightAligned()).AddColumn(new TableColumn("Size").RightAligned());
        foreach (var group in manifest.Files.GroupBy(f => TopFolders(f.Filename, 3)).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(25))
            folders.AddRow(Markup.Escape(group.Key), $"{group.Count():N0}", ByteSize.Format(group.Sum(f => f.FileSize)));
        AnsiConsole.Write(folders);

        foreach (var plugin in manifest.Files.Where(f => f.Filename.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase)))
            AnsiConsole.MarkupLine($"Plugin descriptor: [blue]{Markup.Escape(plugin.Filename)}[/]");

        var source = downloaded.Manifest.Sources[0];
        AnsiConsole.MarkupLine($"[grey]Chunks from {Markup.Escape(source.BaseURL)} ({downloaded.Manifest.Sources.Count} mirrors, signed: {(source.Query.Length > 0 ? "yes" : "no")})[/]");
        return 0;
    }

    private static (FabLibraryItem Item, FabProjectVersion Version)? FindArtifact(List<FabLibraryItem> library, string query)
    {
        foreach (var item in library)
        {
            foreach (var version in item.ProjectVersions ?? [])
            {
                if (string.Equals(version.ArtifactID, query, StringComparison.OrdinalIgnoreCase))
                    return (item, version);
            }
        }

        // Title match: take the newest engine version's artifact.
        var byTitle = library.FirstOrDefault(i => i.Title.Contains(query, StringComparison.OrdinalIgnoreCase) && i.ProjectVersions is { Count: > 0 });
        return byTitle is null ? null : (byTitle, byTitle.ProjectVersions!.Last());
    }

    private static string TopFolders(string path, int depth)
    {
        var parts = path.Split('/');
        return parts.Length <= 1 ? "(root)" : string.Join('/', parts.Take(Math.Min(depth, parts.Length - 1)));
    }
}
