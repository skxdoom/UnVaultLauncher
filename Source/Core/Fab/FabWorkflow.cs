using UnVault.Core.EGL;
using UnVault.Core.Epic;
using UnVault.Core.Install;
using UnVault.Core.Manifests;
using UnVault.Core.Util;
using UnVault.Core.Vault;

namespace UnVault.Core.Fab;

/// <summary>One downloadable build of a Fab item: the item plus the version for a particular engine.</summary>
public sealed record FabArtifact(FabLibraryItem Item, FabProjectVersion Version)
{
    public string ArtifactID => Version.ArtifactID;
}

/// <summary>A planned plugin update in one engine: the files to download and the files to delete.</summary>
public sealed record PluginUpdate(InstallSource Source, FabArtifact Artifact, string EngineDirectory,
    IReadOnlyList<FileManifest> Write, IReadOnlyList<FileManifest> Delete);

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
    /// so both launchers can use it. Over an older copy it's an update: only new or changed files are downloaded and
    /// files the new build dropped are deleted. Resumable; the manifest is written last to mark it complete.
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
            Version = EngineLibrary.WithoutBranch(manifest.Meta.BuildVersion),
            StoredThumbnail = artifact.Item.ThumbnailURL,
            Categories = "|" + string.Join("|", (artifact.Item.Categories ?? []).Select(c => c.Name).Append(KindCategory(FabKinds.FromManifest(manifest)))) + "|",
            Directory = Path.Combine(vaultDirectory, EnginePlugins.ArtifactFolderName(artifact.ArtifactID)),
        };

        string stateDirectory = Path.Combine(entry.Directory, ".unvault");

        // An older copy: set its manifest aside first, so the folder reads as incomplete until the update is done
        // and a retry after an interruption still knows what was there. EGL's JSON copy of it would be stale too.
        string previous = Path.Combine(stateDirectory, "previous.manifest");
        if (File.Exists(entry.ManifestPath))
        {
            Directory.CreateDirectory(stateDirectory);
            File.Move(entry.ManifestPath, previous, overwrite: true);
            File.Delete(Path.Combine(entry.Directory, "manifest.json"));
        }
        var (write, delete) = Diff(TryLoadManifest(previous), manifest);
        InstallCleaner.DeleteFiles(entry.DataDirectory, delete, cancellationToken);

        var plan = InstallPlan.Create(manifest, write);
        entry.Size = manifest.Files.Sum(f => f.FileSize);
        await installer.InstallAsync(plan, entry.DataDirectory, source.Downloaded.Sources, source.Downloaded.Secrets, status, cancellationToken, stateDirectory);

        VaultCache.WriteEntry(entry, source.Downloaded.RawBytes);
        File.Delete(previous);
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

    /// <summary>
    /// Works out how to bring a plugin in an engine up to the build in <paramref name="source"/>. If UnVault installed it,
    /// its kept manifest says what's there; if EGL did (no file list), the plugin's folder is checked against the new
    /// build on disk, with progress going to <paramref name="checking"/>.
    /// </summary>
    public static async Task<PluginUpdate> PlanPluginUpdateAsync(
        InstallSource source, FabArtifact artifact, string engineDirectory, InstallStatus checking, CancellationToken cancellationToken)
    {
        var manifest = source.Manifest;
        RequirePlugin(manifest, artifact.Item.Title);

        if (PluginInstalls.Load(engineDirectory, artifact.ArtifactID) is { } installed)
        {
            var (write, delete) = Diff(installed.Manifest, manifest);
            return new PluginUpdate(source, artifact, engineDirectory, write, [.. delete.Where(f => EnginePlugins.IsPluginFile(f.Filename))]);
        }

        var bad = await Verifier.FindBadFilesAsync(manifest.Files, engineDirectory, checking, cancellationToken: cancellationToken);
        // Whatever else is in the plugin's own folder belongs to the old build (or was built from it).
        string folder = EnginePlugins.MarketplaceFolder(engineDirectory, artifact.ArtifactID);
        var wanted = manifest.Files.Select(f => f.Filename).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leftovers = !Directory.Exists(folder) ? [] : Directory.EnumerateFiles(folder, "*", EnginePlugins.AllFiles)
            .Select(path => Path.GetRelativePath(engineDirectory, path).Replace('\\', '/'))
            .Where(name => !wanted.Contains(name))
            .Select(name => new FileManifest { Filename = name })
            .ToList();
        return new PluginUpdate(source, artifact, engineDirectory, [.. bad.Select(b => b.File)], leftovers);
    }

    /// <summary>Downloads what <see cref="PlanPluginUpdateAsync"/> found changed, deletes what the new build dropped, and records the new build.</summary>
    public static async Task ApplyPluginUpdateAsync(
        PluginUpdate update, Installer installer, InstallStatus status, CancellationToken cancellationToken, string? launcherInstalledPath = null)
    {
        var (source, artifact, engineDirectory) = (update.Source, update.Artifact, update.EngineDirectory);
        InstallCleaner.DeleteFiles(engineDirectory, update.Delete, cancellationToken);
        var plan = InstallPlan.Create(source.Manifest, update.Write);
        await installer.InstallAsync(plan, engineDirectory, source.Downloaded.Sources, source.Downloaded.Secrets, status, cancellationToken,
            stateDirectory: PluginInstalls.StateDirectory(engineDirectory, artifact.ArtifactID));

        // From now on UnVault has the file list, so later updates and removal are exact even if EGL installed it.
        RecordPlugin(engineDirectory, new PluginRecord
        {
            ArtifactID = artifact.ArtifactID,
            Title = artifact.Item.Title,
            BuildVersion = source.Manifest.Meta.BuildVersion,
            AssetNamespace = artifact.Item.AssetNamespace,
            AssetID = artifact.Item.AssetID,
            InstallSize = source.Manifest.Files.Sum(f => f.FileSize),
        }, source.Downloaded.RawBytes, launcherInstalledPath);
    }

    /// <summary>
    /// What an update has to write (files that are new or changed) and delete (files the new build no longer has).
    /// Without the old manifest everything is written.
    /// </summary>
    internal static (List<FileManifest> Write, List<FileManifest> Delete) Diff(Manifest? old, Manifest current)
    {
        if (old is null)
            return ([.. current.Files], []);

        var before = old.Files.GroupBy(f => f.Filename, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var write = current.Files.Where(f => !before.TryGetValue(f.Filename, out var o) || !SameContent(o, f)).ToList();
        var now = current.Files.Select(f => f.Filename).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var delete = old.Files.Where(f => !now.Contains(f.Filename)).ToList();
        return (write, delete);

        // Only trust a hash both builds actually have.
        static bool SameContent(FileManifest a, FileManifest b) =>
            a.FileSize == b.FileSize && a.SHA1.Length == 20 && a.SHA1.AsSpan().SequenceEqual(b.SHA1);
    }

    private static Manifest? TryLoadManifest(string path)
    {
        try
        {
            return File.Exists(path) ? Manifest.Load(path) : null;
        }
        catch (Exception ex) when (ex is ManifestFormatException or IOException)
        {
            return null; // unreadable: download everything again
        }
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
            // Ours: exactly the files we put there (only plugin files, whatever an older record lists).
            result = InstallCleaner.DeleteFiles(engineDirectory, installed.Manifest.Files.Where(f => EnginePlugins.IsPluginFile(f.Filename)));
        }
        else
        {
            // EGL's, or copied in some other way: no file list, but the folder named after the artifact is the plugin.
            string folder = EnginePlugins.MarketplaceFolder(engineDirectory, artifactID);
            if (!Directory.Exists(folder))
                throw new InstallException($"{artifactID} isn't in Engine\\Plugins\\Marketplace, so its files can't be told apart from the engine's. Remove it with the Epic Games Launcher.");
            result = InstallCleaner.DeleteFolder(folder);
        }
        // Also whatever an interrupted install left there: its resume journal mustn't vouch for files that are gone now.
        PluginInstalls.Delete(engineDirectory, artifactID);
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
        if (manifest.Files.FirstOrDefault(f => !EnginePlugins.IsPluginFile(f.Filename)) is { } outside)
            throw new InstallException($"{title} has files outside a plugin folder ({outside.Filename}), so it isn't installed into an engine.");
    }

    /// <summary>
    /// Copies manifest files from one root to another, reporting progress in bytes and files. Each file is copied beside
    /// its target first and then moved over it, so a cancel or a full disk never leaves a cut-off file in its place.
    /// </summary>
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
            string target = ContainedPath.Resolve(root, file.Filename, "target folder");
            if (!File.Exists(source))
                throw new InstallException($"The downloaded copy is incomplete: {file.Filename} is missing. Download it again.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(target, FileAttributes.Normal);

            string temporary = AtomicFile.TemporaryFor(target);
            try
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan | FileOptions.Asynchronous))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        status.AddWritten(read);
                    }
                }
                File.Move(temporary, target, overwrite: true);
            }
            catch
            {
                AtomicFile.TryDelete(temporary);
                throw;
            }
            status.FileDone();
        }
    }

    private static string KindCategory(FabItemKind kind) => kind switch
    {
        FabItemKind.Plugin => "plugins",
        FabItemKind.Project => "projects",
        _ => "assets",
    };
}
