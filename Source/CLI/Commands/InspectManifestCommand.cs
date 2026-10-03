using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Unvault.Core.Manifests;
using Unvault.Core.Util;

namespace Unvault.CLI.Commands;

internal sealed class InspectManifestCommand : Command<InspectManifestCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<manifest>")]
        [Description("Path to a .manifest file.")]
        public string Path { get; init; } = "";

        [CommandOption("--combos")]
        [Description("Also list every distinct combination of tags found on files.")]
        public bool ShowCombos { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var manifest = Manifest.Load(settings.Path);
        timer.Stop();

        var meta = manifest.Meta;
        long installSize = manifest.Files.Sum(f => f.FileSize);
        long downloadSize = manifest.Chunks.Sum(c => c.FileSize);

        var summary = new Grid().AddColumn().AddColumn();
        summary.AddRow("[grey]App[/]", Markup.Escape(meta.AppName));
        summary.AddRow("[grey]Build[/]", Markup.Escape(meta.BuildVersion));
        summary.AddRow("[grey]Build ID[/]", Markup.Escape(meta.BuildID));
        summary.AddRow("[grey]Launch exe[/]", Markup.Escape(meta.LaunchExe));
        summary.AddRow("[grey]Prereq[/]", Markup.Escape($"{meta.PrereqName} {meta.PrereqPath} {meta.PrereqArgs}".Trim()));
        summary.AddRow("[grey]Format[/]", $"file v{manifest.Version}, feature level {manifest.FeatureLevel}, meta v{meta.DataVersion}, chunks in {ChunkInfo.GetChunkDir(manifest.FeatureLevel)}");
        summary.AddRow("[grey]Files[/]", $"{manifest.Files.Count:N0}");
        summary.AddRow("[grey]Chunks[/]", $"{manifest.Chunks.Count:N0}");
        summary.AddRow("[grey]Install size (all)[/]", ByteSize.Format(installSize));
        summary.AddRow("[grey]Download size (all)[/]", ByteSize.Format(downloadSize));
        summary.AddRow("[grey]Parsed in[/]", $"{timer.ElapsedMilliseconds} ms");
        AnsiConsole.Write(new Panel(summary).Header("Manifest").Expand());

        if (manifest.CustomFields.Count > 0)
        {
            var fields = new Table().AddColumn("Custom field").AddColumn("Value");
            foreach (var (key, value) in manifest.CustomFields.OrderBy(kv => kv.Key))
                fields.AddRow(Markup.Escape(key), Markup.Escape(Truncate(value, 120)));
            AnsiConsole.Write(fields);
        }

        WriteTagTable(manifest);

        if (settings.ShowCombos)
            WriteComboTable(manifest);

        return 0;
    }

    private static void WriteTagTable(Manifest manifest)
    {
        var untagged = manifest.Files.Where(f => f.InstallTags.Count == 0).ToList();
        var byTag = manifest.Files
            .SelectMany(f => f.InstallTags.Select(tag => (tag, file: f)))
            .GroupBy(x => x.tag)
            .Select(g => (Tag: g.Key, Count: g.Count(), Size: g.Sum(x => x.file.FileSize)))
            .OrderByDescending(x => x.Size)
            .ToList();

        var table = new Table().Title("Install tags (a file with several tags counts toward each)")
            .AddColumn("Tag").AddColumn(new TableColumn("Files").RightAligned()).AddColumn(new TableColumn("Size").RightAligned());

        table.AddRow("[grey](untagged — always installed)[/]", $"{untagged.Count:N0}", ByteSize.Format(untagged.Sum(f => f.FileSize)));
        foreach (var (tag, count, size) in byTag)
            table.AddRow(Markup.Escape(tag.Length == 0 ? "\"\"" : tag), $"{count:N0}", ByteSize.Format(size));

        AnsiConsole.Write(table);
    }

    private static void WriteComboTable(Manifest manifest)
    {
        var combos = manifest.Files
            .GroupBy(f => string.Join(" + ", f.InstallTags.Order(StringComparer.Ordinal)))
            .Select(g => (Combo: g.Key, Count: g.Count(), Size: g.Sum(f => f.FileSize), Example: g.First().Filename))
            .OrderByDescending(x => x.Size)
            .ToList();

        var table = new Table().Title("Tag combinations")
            .AddColumn("Tags").AddColumn(new TableColumn("Files").RightAligned()).AddColumn(new TableColumn("Size").RightAligned()).AddColumn("Example file");

        foreach (var (combo, count, size, example) in combos)
            table.AddRow(Markup.Escape(combo.Length == 0 ? "(none)" : combo), $"{count:N0}", ByteSize.Format(size), Markup.Escape(Truncate(example, 70)));

        AnsiConsole.Write(table);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
