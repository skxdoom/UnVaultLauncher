using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using UnVault.Core.EGL;
using UnVault.Core.Epic;

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

        var localByApp = EGLInstallations.ReadItems()
            .GroupBy(i => i.AppName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var engines = assets.Where(a => a.IsEngine).OrderByDescending(a => EngineVersion(a.AppName)).ToList();

        var table = new Table().Title("Unreal Engine versions")
            .AddColumn("App").AddColumn("Latest build").AddColumn("Installed build").AddColumn("Status");

        foreach (var engine in engines)
        {
            string installed = "", status;
            if (!localByApp.TryGetValue(engine.AppName, out var local))
                status = "[grey]not installed[/]";
            else if (!Directory.Exists(local.InstallLocation))
                status = "[red]stale EGL record (folder missing)[/]";
            else
            {
                installed = ShortVersion(local.AppVersionString);
                status = local.AppVersionString == engine.BuildVersion ? "[green]up to date[/]" : "[yellow]update available[/]";
            }
            table.AddRow(Markup.Escape(engine.AppName), Markup.Escape(ShortVersion(engine.BuildVersion)), Markup.Escape(installed), status);
        }
        AnsiConsole.Write(table);

        var others = assets.Where(a => !a.IsEngine).ToList();
        if (settings.All)
        {
            var otherTable = new Table().Title("Other owned items")
                .AddColumn("App").AddColumn("Namespace").AddColumn("Build");
            foreach (var asset in others.OrderBy(a => a.AppName, StringComparer.OrdinalIgnoreCase))
                otherTable.AddRow(Markup.Escape(asset.AppName), Markup.Escape(asset.Namespace), Markup.Escape(ShortVersion(asset.BuildVersion)));
            AnsiConsole.Write(otherTable);
        }
        else if (others.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]Plus {others.Count} other items (plugins, games). Use --all to list them.[/]");
        }

        return 0;
    }

    /// <summary>"UE_5.7" → 5.7, for sorting.</summary>
    private static Version EngineVersion(string appName) =>
        Version.TryParse(appName.AsSpan(3), out var version) ? version : new Version(0, 0);

    private static string ShortVersion(string version)
    {
        int cut = version.IndexOf("+++", StringComparison.Ordinal);
        return cut > 0 ? version[..cut] : version;
    }
}
