using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using UnVault.Core.EGL;
using UnVault.Core.Manifests;
using UnVault.Core.Util;

namespace UnVault.CLI.Commands;

internal sealed class ListInstallsCommand : Command<ListInstallsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-detect")]
        [Description("Skip reading engine manifests to detect installed components (faster).")]
        public bool NoDetect { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var items = EGLInstallations.ReadItems();
        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No Epic Games Launcher installs found in[/] {Markup.Escape(EGLInstallations.ItemsDirectory)}");
            return 0;
        }

        var engines = items.Where(i => i.IsEngine).OrderBy(i => i.AppName, StringComparer.OrdinalIgnoreCase).ToList();
        var hostByGUID = items.ToDictionary(i => i.InstallationGUID, StringComparer.OrdinalIgnoreCase);

        var table = new Table().Title("Engines")
            .AddColumn("App").AddColumn("Version").AddColumn("Location")
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn("Installed components").AddColumn("Not installed");

        AnsiConsole.Status().Start("Reading engine manifestsâ€¦", _ =>
        {
            foreach (var engine in engines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (installed, notInstalled) = settings.NoDetect ? ("[grey]-[/]", "[grey]-[/]") : DescribeComponents(engine);
                table.AddRow(
                    Markup.Escape(engine.AppName),
                    Markup.Escape(ShortVersion(engine.AppVersionString)),
                    Markup.Escape(engine.InstallLocation),
                    ByteSize.Format(engine.InstallSize),
                    installed,
                    notInstalled);
            }
        });
        AnsiConsole.Write(table);

        var others = items.Where(i => !i.IsEngine).OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        if (others.Count > 0)
        {
            var pluginTable = new Table().Title("Plugins and other items")
                .AddColumn("Name").AddColumn("App").AddColumn("Version").AddColumn("Installed into")
                .AddColumn(new TableColumn("Size").RightAligned()).AddColumn("Fab");

            foreach (var item in others)
            {
                var host = hostByGUID.TryGetValue(item.HostInstallationGUID, out var h) ? h.AppName : "";
                pluginTable.AddRow(
                    Markup.Escape(item.DisplayName),
                    Markup.Escape(item.AppName),
                    Markup.Escape(ShortVersion(item.AppVersionString)),
                    Markup.Escape(host.Length > 0 ? host : item.InstallLocation),
                    ByteSize.Format(item.InstallSize),
                    item.bIsFab ? "yes" : "");
            }
            AnsiConsole.Write(pluginTable);
        }

        return 0;
    }

    private static (string Installed, string NotInstalled) DescribeComponents(EGLItem engine)
    {
        if (!Directory.Exists(engine.InstallLocation))
            return ("[red]folder missing â€” stale EGL record[/]", "");
        if (!File.Exists(engine.CompleteManifestPath))
            return ("[yellow]manifest missing[/]", "");
        try
        {
            var manifest = Manifest.Load(engine.CompleteManifestPath);
            var available = manifest.GetInstallTags();
            var installed = manifest.DetectInstalledTags(engine.InstallLocation);
            if (available.Count == 0)
                return ("[grey]none offered[/]", "");
            return (
                installed.Count == 0 ? "[grey]core only[/]" : $"[green]{Markup.Escape(string.Join(", ", available.Where(installed.Contains)))}[/]",
                $"[grey]{Markup.Escape(string.Join(", ", available.Where(t => !installed.Contains(t))))}[/]");
        }
        catch (ManifestFormatException ex)
        {
            return ($"[red]{Markup.Escape(ex.Message)}[/]", "");
        }
    }

    /// <summary>"5.7.4-51494982+++UE5+Release-5.7-Windows" â†’ "5.7.4-51494982".</summary>
    private static string ShortVersion(string version)
    {
        int cut = version.IndexOf("+++", StringComparison.Ordinal);
        return cut > 0 ? version[..cut] : version;
    }
}
