using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using UnVault.Core.Epic;
using UnVault.Core.Install;

namespace UnVault.CLI.Commands;

internal sealed class OwnedCommand : EpicCommand<OwnedCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--all")]
        [Description("Also list everything else the account owns (plugins, games), not just engines.")]
        public bool All { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var assets = await AnsiConsole.Status().StartAsync("Asking Epic what you own…",
            _ => Services.API.GetAssetsAsync(cancellationToken: cancellationToken));

        // Whoever installed it; an existing folder wins over a stale EGL record for the same version.
        var localByApp = EngineLibrary.ScanLocal()
            .GroupBy(e => e.AppName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.FirstOrDefault(e => e.Exists) ?? g.First(), StringComparer.OrdinalIgnoreCase);

        var engines = assets.Where(a => a.IsEngine).OrderByDescending(a => EngineLibrary.ParseVersion(a.AppName)).ToList();

        var table = new Table().Title("Unreal Engine versions")
            .AddColumn("App").AddColumn("Latest build").AddColumn("Installed build").AddColumn("Status");

        foreach (var engine in engines)
        {
            string installed = "", status;
            if (!localByApp.TryGetValue(engine.AppName, out var local))
                status = "[grey]not installed[/]";
            else if (!local.Exists)
                status = "[red]stale EGL record (folder missing)[/]";
            else
            {
                installed = EngineLibrary.WithoutBranch(local.BuildVersion);
                status = local.BuildVersion == engine.BuildVersion ? "[green]up to date[/]" : "[yellow]update available[/]";
            }
            table.AddRow(Markup.Escape(engine.AppName), Markup.Escape(EngineLibrary.WithoutBranch(engine.BuildVersion)), Markup.Escape(installed), status);
        }
        AnsiConsole.Write(table);

        var others = assets.Where(a => !a.IsEngine).ToList();
        if (settings.All)
        {
            var otherTable = new Table().Title("Other owned items")
                .AddColumn("App").AddColumn("Namespace").AddColumn("Build");
            foreach (var asset in others.OrderBy(a => a.AppName, StringComparer.OrdinalIgnoreCase))
                otherTable.AddRow(Markup.Escape(asset.AppName), Markup.Escape(asset.Namespace), Markup.Escape(EngineLibrary.WithoutBranch(asset.BuildVersion)));
            AnsiConsole.Write(otherTable);
        }
        else if (others.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]Plus {others.Count} other items (plugins, games). Use --all to list them.[/]");
        }

        return 0;
    }
}
