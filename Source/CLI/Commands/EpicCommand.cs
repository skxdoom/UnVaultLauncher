using Spectre.Console;
using Spectre.Console.Cli;
using Unvault.Core.Epic;
using Unvault.Core.Install;
using Unvault.Core.Manifests;

namespace Unvault.CLI.Commands;

/// <summary>Base for commands that talk to Epic: turns API failures into a readable one-line error.</summary>
internal abstract class EpicCommand<TSettings> : AsyncCommand<TSettings> where TSettings : CommandSettings
{
    public sealed override async Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(settings, cancellationToken);
        }
        catch (NotLoggedInException ex)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ex.Message)}[/] Run [blue]{Services.CommandName} login[/] to sign in.");
            return 2;
        }
        catch (EpicAPIException ex)
        {
            AnsiConsole.MarkupLine($"[red]Epic API error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/] Run the same command again to continue where it stopped.");
            return 130;
        }
        catch (InstallException ex)
        {
            AnsiConsole.MarkupLine($"[red]Install failed:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[grey]Run the same command again to retry; finished parts are kept.[/]");
            return 1;
        }
        catch (ManifestFormatException ex)
        {
            AnsiConsole.MarkupLine($"[red]Manifest problem:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Disk error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    protected abstract Task<int> RunAsync(TSettings settings, CancellationToken cancellationToken);
}

/// <summary>Settings for commands that take no arguments.</summary>
internal sealed class NoSettings : CommandSettings;
