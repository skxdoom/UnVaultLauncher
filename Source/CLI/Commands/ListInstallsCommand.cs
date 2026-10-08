using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using UnVault.Core.EGL;
using UnVault.Core.Fab;
using UnVault.Core.Install;
using UnVault.Core.Manifests;
using UnVault.Core.Util;

namespace UnVault.CLI.Commands;

/// <summary>The engines on this PC, whoever installed them, and the Fab plugins in them: what the app's Engines tab shows.</summary>
internal sealed class ListInstallsCommand : Command<ListInstallsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-detect")]
        [Description("Skip reading engine manifests to find installed components (faster).")]
        public bool NoDetect { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var engines = EngineLibrary.ScanLocal();
        if (engines.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No engines found[/], from UnVault Launcher or the Epic Games Launcher.");
            return 0;
        }

        var table = new Table().Title("Engines")
            .AddColumn("App").AddColumn("Version").AddColumn("Location")
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn("Installed by").AddColumn("Installed components").AddColumn("Not installed");

        AnsiConsole.Status().Start("Reading engine manifests…", _ =>
        {
            foreach (var engine in engines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (installed, notInstalled) = !engine.Exists ? ("[red]folder missing: stale EGL record[/]", "")
                    : settings.NoDetect ? ("[grey]-[/]", "[grey]-[/]")
                    : DescribeComponents(engine);
                table.AddRow(
                    Markup.Escape(engine.AppName),
                    Markup.Escape(EngineLibrary.WithoutBranch(engine.BuildVersion)),
                    Markup.Escape(engine.Directory),
                    ByteSize.Format(engine.InstallSize),
                    InstalledBy(engine.Kind),
                    installed,
                    notInstalled);
            }
        });
        AnsiConsole.Write(table);

        WritePlugins(engines.Where(e => e.Exists).ToList());
        return 0;
    }

    /// <summary>Fab plugins in each engine, whoever put them there (as the app's Library sees them).</summary>
    private static void WritePlugins(IReadOnlyList<LocalEngine> engines)
    {
        var launcherInstalled = EGLInstallations.ReadLauncherInstalled();
        var eglNames = EGLInstallations.ReadItems().Where(i => !i.IsEngine)
            .GroupBy(i => i.AppName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase);

        var table = new Table().Title("Fab plugins")
            .AddColumn("Name").AddColumn("Artifact").AddColumn("Build").AddColumn("Engine").AddColumn("Installed by");
        foreach (var engine in engines)
        {
            try
            {
                var titles = PluginInstalls.List(engine.Directory).ToDictionary(p => p.ArtifactID, p => p.Title, StringComparer.OrdinalIgnoreCase);
                foreach (var plugin in EnginePlugins.Find(engine.Directory, launcherInstalled).OrderBy(p => p.ArtifactID, StringComparer.OrdinalIgnoreCase))
                {
                    string name = titles.GetValueOrDefault(plugin.ArtifactID) is { Length: > 0 } title ? title
                        : eglNames.GetValueOrDefault(plugin.ArtifactID) is { Length: > 0 } eglName ? eglName
                        : plugin.ArtifactID;
                    table.AddRow(
                        Markup.Escape(name),
                        Markup.Escape(plugin.ArtifactID),
                        Markup.Escape(EngineLibrary.WithoutBranch(plugin.BuildVersion ?? "")),
                        Markup.Escape(engine.AppName),
                        plugin.Source switch
                        {
                            PluginSource.UnVault => "UnVault Launcher",
                            PluginSource.EGL => "Epic Games Launcher",
                            _ => "[grey]neither (copied in)[/]",
                        });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                table.AddRow("[red]unreadable[/]", "", "", Markup.Escape(engine.AppName), Markup.Escape(ex.Message));
            }
        }
        if (table.Rows.Count > 0)
            AnsiConsole.Write(table);
    }

    private static (string Installed, string NotInstalled) DescribeComponents(LocalEngine engine)
    {
        try
        {
            if (engine.Load() is not { } install)
                return ("[yellow]manifest missing[/]", "");
            var available = install.Manifest.GetInstallTags();
            if (available.Count == 0)
                return ("[grey]none offered[/]", "");
            var installed = install.InstallTags;
            return (
                installed.Count == 0 ? "[grey]core only[/]" : $"[green]{Markup.Escape(string.Join(", ", available.Where(installed.Contains)))}[/]",
                $"[grey]{Markup.Escape(string.Join(", ", available.Where(t => !installed.Contains(t))))}[/]");
        }
        catch (Exception ex) when (ex is ManifestFormatException or IOException or JsonException)
        {
            return ($"[red]{Markup.Escape(ex.Message)}[/]", "");
        }
    }

    private static string InstalledBy(LocalInstallKind kind) => kind switch
    {
        LocalInstallKind.UnVault => "UnVault Launcher",
        LocalInstallKind.AdoptedFromEGL => "Epic Games Launcher, managed by UnVault",
        _ => "Epic Games Launcher",
    };
}
