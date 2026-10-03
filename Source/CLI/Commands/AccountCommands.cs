using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Unvault.Core.Epic;
using Unvault.Core.Fab;

namespace Unvault.CLI.Commands;

internal sealed class LoginCommand : EpicCommand<LoginCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--code <CODE>")]
        [Description("Authorization code (or the JSON containing it). For scripting; normally you paste it when asked.")]
        public string? Code { get; init; }

        [CommandOption("--no-browser")]
        [Description("Don't open the browser; just print the sign-in link.")]
        public bool NoBrowser { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        string? input = settings.Code;
        if (input is null)
        {
            AnsiConsole.MarkupLine("[bold]1.[/] Sign in to your Epic account in the browser.");
            if (!settings.NoBrowser)
                TryOpenBrowser(EpicEndpoints.LoginURL);
            AnsiConsole.MarkupLine("   If no browser opened, go to:");
            AnsiConsole.WriteLine("   " + EpicEndpoints.LoginURL);
            AnsiConsole.MarkupLine("[bold]2.[/] After signing in, the page shows a short text containing [bold]\"authorizationCode\"[/].");
            AnsiConsole.MarkupLine("   Copy all of it (or just the code) and paste it here.");
            AnsiConsole.MarkupLine("   [grey]Epic warns not to share this code with third parties. It's fine here: this program runs on your PC and sends it only to Epic.[/]");
            input = await AnsiConsole.PromptAsync(new TextPrompt<string>("Code:").Secret(), cancellationToken);
        }

        string code = EpicAuthClient.ExtractAuthorizationCode(input);
        var session = await Services.Account.LoginWithAuthorizationCodeAsync(code, cancellationToken);

        AnsiConsole.MarkupLine($"[green]Logged in as[/] [bold]{Markup.Escape(session.DisplayName)}[/].");
        AnsiConsole.MarkupLine($"[grey]Session saved, encrypted for your Windows user: {Markup.Escape(SessionStore.Default.FilePath)}[/]");
        return 0;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser registered; the printed link still works.
        }
    }
}

internal sealed class LogoutCommand : EpicCommand<NoSettings>
{
    protected override async Task<int> RunAsync(NoSettings settings, CancellationToken cancellationToken)
    {
        await Services.Account.LogoutAsync(cancellationToken);
        FabLibraryCache.Delete();
        AnsiConsole.MarkupLine("Logged out; the saved session was removed.");
        return 0;
    }
}

internal sealed class WhoAmICommand : EpicCommand<NoSettings>
{
    protected override async Task<int> RunAsync(NoSettings settings, CancellationToken cancellationToken)
    {
        var session = await Services.Account.GetValidSessionAsync(cancellationToken);

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Account[/]", Markup.Escape(session.DisplayName));
        grid.AddRow("[grey]Account ID[/]", Markup.Escape(session.AccountID));
        grid.AddRow("[grey]Access token valid until[/]", session.AccessExpiresAt.ToLocalTime().ToString("g"));
        grid.AddRow("[grey]Stay signed in until[/]", session.RefreshExpiresAt.ToLocalTime().ToString("g"));
        AnsiConsole.Write(grid);
        return 0;
    }
}
