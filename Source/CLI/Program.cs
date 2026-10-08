using Spectre.Console.Cli;
using UnVault.CLI;
using UnVault.CLI.Commands;
using UnVault.Core;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName(Services.CommandName);
    config.SetApplicationVersion(ProductInfo.Version);

    config.AddCommand<LoginCommand>("login")
        .WithDescription("Sign in to your Epic account (in your browser).");
    config.AddCommand<LogoutCommand>("logout")
        .WithDescription("Sign out and delete the saved session.");
    config.AddCommand<WhoAmICommand>("whoami")
        .WithDescription("Show the signed-in account.");

    config.AddCommand<OwnedCommand>("owned")
        .WithDescription("List the Unreal Engine versions your account can install, and which are installed or outdated.");

    config.AddCommand<FabCommand>("fab")
        .WithDescription("List your Fab library (plugins, asset packs) and where items are installed.")
        .WithExample("fab", "--engine", "UE_5.7");
    config.AddCommand<FabFilesCommand>("fab-files")
        .WithDescription("Fetch one Fab artifact's manifest and show what it contains.")
        .WithExample("fab-files", "MyPlugin");

    config.AddCommand<InstallCommand>("install")
        .WithDescription("Download and install an engine with the components you choose. Resumable.")
        .WithExample("install", "UE_5.8", "--with", "templates,engine_source")
        .WithExample("install", "UE_5.8", "--dir", @"D:\Engines\UE_5.8");
    config.AddCommand<ModifyCommand>("modify")
        .WithDescription("Add or remove optional components of an installed engine.")
        .WithExample("modify", "UE_5.5", "--remove", "editor_symbols")
        .WithExample("modify", "UE_5.7", "--add", "platform_Android");
    config.AddCommand<VerifyCommand>("verify")
        .WithDescription("Check an install's files against its manifest; --repair re-downloads damaged ones.")
        .WithExample("verify", "UE_5.7")
        .WithExample("verify", "UE_5.7", "--only", "Engine/Binaries/**");

    config.AddCommand<ListInstallsCommand>("installs")
        .WithDescription("List the engines on this PC, whoever installed them, with their components and the Fab plugins in them.");

    config.AddCommand<ComponentsCommand>("components")
        .WithDescription("Show an engine's optional components and what adding/removing each would cost or save.")
        .WithExample("components", "UE_5.7")
        .WithExample("components", "UE_5.8", "--remote");

    config.AddCommand<InspectManifestCommand>("inspect")
        .WithDescription("Show what's inside a build manifest file: metadata, size, optional components.")
        .WithExample("inspect", @"""E:\Epic Games\UE_5.7\.egstore\0123456789ABCDEF0123456789ABCDEF.manifest""");
});

// First Ctrl+C cancels gracefully (installs stop and can resume); a second one kills the process.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (cancellation.IsCancellationRequested)
        return;
    e.Cancel = true;
    cancellation.Cancel();
};

return await app.RunAsync(args, cancellation.Token);
