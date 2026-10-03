using Unvault.Core.EGL;
using Unvault.Core.Epic;
using Unvault.Core.Install;
using Unvault.Core.Manifests;
using Unvault.Core.Vault;

namespace Unvault.Core.Fab;

/// <summary>One downloadable build of a Fab item: the item plus the version for a particular engine.</summary>
public sealed record FabArtifact(FabLibraryItem Item, FabProjectVersion Version)
{
    public string ArtifactID => Version.ArtifactID;
}

/// <summary>Downloading Fab content and putting it where Unreal expects it. Shared by the CLI and GUI.</summary>
public static class FabWorkflow
{
    /// <summary>Asks Fab for an artifact's (signed, expiring) manifest and download locations.</summary>
    public static async Task<InstallSource> FetchAsync(EpicAPIClient api, FabArtifact artifact, CancellationToken cancellationToken, string platform = "Windows")
    {
        var info = await api.GetFabDownloadInfoAsync(artifact.ArtifactID, artifact.Item.AssetNamespace, artifact.Item.AssetID, platform, cancellationToken);
        var downloaded = await api.DownloadFabManifestAsync(info, cancellationToken);
        return new InstallSource(downloaded, artifact.Item.AssetNamespace, artifact.Item.AssetID);
    }

    /// <summary>
    /// Downloads an artifact into the vault cache in EGL's layout ({vault}\{artifact}\data + manifest + vault.json),
    /// so both launchers can use it. Resumable; the manifest is written last to mark it complete.
    /// </summary>
    public static async Task<VaultEntry> DownloadToVaultAsync(
        InstallSource source, FabArtifact artifact, string vaultDirectory, Installer installer, InstallStatus status, CancellationToken cancellationToken)
    {
        var manifest = source.Manifest;
        var entry = new VaultEntry
        {
            ArtifactID = artifact.ArtifactID,
            ItemID = artifact.Item.AssetID,
            Title = artifact.Item.Title,
            Build = manifest.Meta.BuildVersion,
            Version = ShortVersion(manifest.Meta.BuildVersion),
            Thumbnail = artifact.Item.ThumbnailURL,
            Categories = "|" + string.Join("|", (artifact.Item.Categories ?? []).Select(c => c.Name).Append(KindCategory(FabKinds.FromManifest(manifest)))) + "|",
            Directory = Path.Combine(vaultDirectory, artifact.ArtifactID),
        };

        var plan = InstallPlan.Create(manifest, manifest.Files);
        entry.Size = plan.InstallBytes;
        string stateDirectory = Path.Combine(entry.Directory, ".unvault");
        await installer.InstallAsync(plan, entry.DataDirectory, source.Downloaded.Sources, source.Downloaded.Secrets, status, cancellationToken, stateDirectory);

        VaultCache.WriteEntry(entry, source.Downloaded.RawBytes);
        // The resume journal is gone once complete; don't leave an empty folder beside EGL's files.
        if (Directory.Exists(stateDirectory) && !Directory.EnumerateFileSystemEntries(stateDirectory).Any())
            Directory.Delete(stateDirectory);
        return entry;
    }

    /// <summary>Installs a plugin straight into an engine (files land in Engine\Plugins\Marketplace\…), like EGL.</summary>
    public static async Task InstallPluginAsync(
        InstallSource source, FabArtifact artifact, string engineDirectory, Installer installer, InstallStatus status, CancellationToken cancellationToken,
        string? launcherInstalledPath = null)
    {
        var manifest = source.Manifest;
        RequirePlugin(manifest, artifact.Item.Title);

        var plan = InstallPlan.Create(manifest, manifest.Files);
        await installer.InstallAsync(plan, engineDirectory, source.Downloaded.Sources, source.Downloaded.Secrets, status, cancellationToken,
            stateDirectory: PluginInstalls.StateDirectory(engineDirectory, artifact.ArtifactID));

        RecordPlugin(engineDirectory, new PluginRecord
        {
            ArtifactID = artifact.ArtifactID,
            Title = artifact.Item.Title,
            BuildVersion = manifest.Meta.BuildVersion,
            AssetNamespace = artifact.Item.AssetNamespace,
            AssetID = artifact.Item.AssetID,
            InstallSize = plan.InstallBytes,
        }, source.Downloaded.RawBytes, launcherInstalledPath);
    }

    /// <summary>Installs a plugin from an already downloaded vault copy: no network needed.</summary>
    public static async Task InstallPluginFromVaultAsync(VaultEntry entry, string engineDirectory, InstallStatus status, CancellationToken cancellationToken, string? launcherInstalledPath = null)
    {
        byte[] raw = await File.ReadAllBytesAsync(entry.ManifestPath, cancellationToken);
        var manifest = Manifest.Parse(raw);
        RequirePlugin(manifest, entry.Title);

        await CopyFilesAsync(entry.DataDirectory, engineDirectory, manifest.Files, status, cancellationToken);
        RecordPlugin(engineDirectory, new PluginRecord
        {
            ArtifactID = entry.ArtifactID,
            Title = entry.Title,
            BuildVersion = manifest.Meta.BuildVersion,
            AssetNamespace = FabKinds.FabNamespace,
            AssetID = entry.ItemID,
            InstallSize = manifest.Files.Sum(f => f.FileSize),
        }, raw, launcherInstalledPath);
    }

    /// <summary>Removes a Fab plugin from an engine, whoever installed it, and its LauncherInstalled.dat entry.</summary>
    public static InstallCleaner.Result UninstallPlugin(string engineDirectory, string artifactID, string? launcherInstalledPath = null)
    {
        InstallCleaner.Result result;
        if (PluginInstalls.Load(engineDirectory, artifactID) is { } installed)
        {
            // Ours: exactly the files we put there.
            result = InstallCleaner.DeleteFiles(engineDirectory, installed.Manifest.Files);
            PluginInstalls.Delete(engineDirectory, artifactID);
        }
        else
        {
            // EGL's, or copied in some other way: no file list, but the folder named after the artifact is the plugin.
            string folder = EnginePlugins.MarketplaceFolder(engineDirectory, artifactID);
            if (!Directory.Exists(folder))
                throw new InstallException($"{artifactID} isn't in Engine\\Plugins\\Marketplace, so its files can't be told apart from the engine's. Remove it with the Epic Games Launcher.");
            result = InstallCleaner.DeleteFolder(folder);
        }
        EGLInstallations.UnregisterLauncherInstall(artifactID, engineDirectory, launcherInstalledPath);
        return result;
    }

    /// <summary>Copies an asset pack's Content into a project (EGL's "Add to project"). Returns the number of files.</summary>
    public static async Task<int> AddToProjectAsync(VaultEntry entry, string projectDirectory, InstallStatus status, CancellationToken cancellationToken)
    {
        var manifest = Manifest.Load(entry.ManifestPath);
        // Only content: a pack's Config or .uproject would overwrite the project's own.
        var files = manifest.Files.Where(f => f.Filename.StartsWith("Content/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0)
            throw new InstallException($"{entry.Title} has no Content folder to add to a project.");
        await CopyFilesAsync(entry.DataDirectory, projectDirectory, files, status, cancellationToken);
        return files.Count;
    }

    /// <summary>Copies a complete project into a new folder (EGL's "Create project"). Returns the .uproject path.</summary>
    public static async Task<string> CreateProjectAsync(VaultEntry entry, string targetDirectory, InstallStatus status, CancellationToken cancellationToken)
    {
        if (Directory.Exists(targetDirectory) && Directory.EnumerateFileSystemEntries(targetDirectory).Any())
            throw new InstallException($"{targetDirectory} already exists and isn't empty.");

        var manifest = Manifest.Load(entry.ManifestPath);
        var projectFile = manifest.Files.FirstOrDefault(f => !f.Filename.Contains('/') && f.Filename.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase))
            ?? throw new InstallException($"{entry.Title} isn't a complete project (no .uproject).");
        await CopyFilesAsync(entry.DataDirectory, targetDirectory, manifest.Files, status, cancellationToken);
        return Path.Combine(targetDirectory, projectFile.Filename);
    }

    /// <summary>The item's version for an engine; or, for content (not plugins), the newest older one as a fallback.</summary>
    public static FabProjectVersion? PickVersion(FabLibraryItem item, string engineAppName, bool allowOlder)
    {
        var versions = item.ProjectVersions ?? [];
        var exact = versions.FirstOrDefault(v => (v.EngineVersions ?? []).Contains(engineAppName, StringComparer.OrdinalIgnoreCase));
        if (exact is not null || !allowOlder)
            return exact;

        var target = EngineLibrary.ParseVersion(engineAppName);
        return versions
            .Select(v => (Version: v, Best: (v.EngineVersions ?? []).Select(EngineLibrary.ParseVersion).Where(e => e <= target).DefaultIfEmpty().Max()))
            .Where(x => x.Best is not null)
            .OrderByDescending(x => x.Best)
            .Select(x => x.Version)
            .FirstOrDefault();
    }

    private static void RecordPlugin(string engineDirectory, PluginRecord record, byte[] rawManifest, string? launcherInstalledPath)
    {
        record.InstalledAt = DateTimeOffset.Now;
        PluginInstalls.Save(engineDirectory, record, rawManifest);
        // Same entry EGL writes for a plugin, so EGL and UE tools see it as installed into this engine.
        EGLInstallations.RegisterLauncherInstall(new LauncherInstalledEntry
        {
            InstallLocation = engineDirectory,
            NamespaceID = record.AssetNamespace,
            ItemID = record.AssetID,
            ArtifactID = record.ArtifactID,
            AppVersion = record.BuildVersion,
            AppName = record.ArtifactID,
        }, launcherInstalledPath);
    }

    private static void RequirePlugin(Manifest manifest, string title)
    {
        if (FabKinds.FromManifest(manifest) != FabItemKind.Plugin)
            throw new InstallException($"{title} isn't an engine plugin, so it can't be installed into an engine.");
    }

    /// <summary>Copies manifest files from one root to another, reporting progress in bytes and files.</summary>
    internal static async Task CopyFilesAsync(string sourceRoot, string targetRoot, IReadOnlyCollection<FileManifest> files, InstallStatus status, CancellationToken cancellationToken)
    {
        status.WriteTotal = files.Sum(f => f.FileSize);
        status.FilesTotal = files.Count;
        string root = Path.GetFullPath(targetRoot);
        byte[] buffer = new byte[1024 * 1024];

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = Path.Combine(sourceRoot, file.Filename);
            string target = Path.GetFullPath(Path.Combine(root, file.Filename));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Manifest path escapes the target folder: {file.Filename}");
            if (!File.Exists(source))
                throw new InstallException($"The downloaded copy is incomplete: {file.Filename} is missing. Download it again.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(target, FileAttributes.Normal);

            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    status.AddWritten(read);
                }
            }
            status.FileDone();
        }
    }

    private static string ShortVersion(string build)
    {
        int cut = build.IndexOf("+++", StringComparison.Ordinal);
        return cut > 0 ? build[..cut] : build;
    }

    private static string KindCategory(FabItemKind kind) => kind switch
    {
        FabItemKind.Plugin => "plugins",
        FabItemKind.Project => "projects",
        _ => "assets",
    };
}
