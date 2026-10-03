using System.Text.RegularExpressions;
using Unvault.Core.EGL;
using Unvault.Core.Epic;
using Unvault.Core.Manifests;

namespace Unvault.Core.Install;

/// <summary>A build to install: its manifest and download locations, plus catalog identity for registration.</summary>
public sealed record InstallSource(DownloadedManifest Downloaded, string CatalogNamespace, string CatalogItemID)
{
    public Manifest Manifest => Downloaded.Manifest;
}

/// <summary>What a modify would do: the new selection, files to delete, and files to download.</summary>
public sealed record ModifyPlan(ExistingInstall Install, IReadOnlySet<string> NewTags, IReadOnlyList<FileManifest> ToRemove, InstallPlan? ToAdd)
{
    public bool IsEmpty => ToRemove.Count == 0 && ToAdd is null;
    public long BytesFreed => ToRemove.Sum(f => f.FileSize);
}

/// <summary>The install/modify steps shared by the CLI and the GUI.</summary>
public static class InstallWorkflow
{
    /// <summary>The latest Live build of an app the account owns.</summary>
    public static async Task<InstallSource> FetchLatestAsync(EpicAPIClient api, string appName, CancellationToken cancellationToken)
    {
        var assets = await api.GetAssetsAsync(cancellationToken: cancellationToken);
        var asset = assets.FirstOrDefault(a => string.Equals(a.AppName, appName, StringComparison.OrdinalIgnoreCase))
            ?? throw new EpicAPIException($"Your account doesn't own {appName}.");
        return await FetchAsync(api, asset, cancellationToken);
    }

    public static async Task<InstallSource> FetchAsync(EpicAPIClient api, EpicAsset asset, CancellationToken cancellationToken)
    {
        var build = await api.GetBuildInfoAsync(asset.Namespace, asset.CatalogItemID, asset.AppName, cancellationToken: cancellationToken);
        var downloaded = await api.DownloadManifestAsync(build, cancellationToken);
        return new InstallSource(downloaded, asset.Namespace, asset.CatalogItemID);
    }

    /// <summary>A local manifest file; chunk locations come from <paramref name="baseURLs"/> or EGL's record of the same app.</summary>
    public static InstallSource FromLocalManifest(byte[] raw, IReadOnlyList<string> baseURLs)
    {
        var manifest = Manifest.Parse(raw);
        var item = EGLInstallations.ReadItems().FirstOrDefault(i => string.Equals(i.AppName, manifest.Meta.AppName, StringComparison.OrdinalIgnoreCase));
        var urls = baseURLs.Count > 0 ? baseURLs : item?.BaseURLs ?? [];
        if (urls.Count == 0)
            throw new InstallException("No download location is known for this build; pass a CloudDir base URL.");

        var sources = urls.Select(u => new ChunkSource(u.TrimEnd('/'))).ToList();
        return new InstallSource(new DownloadedManifest(manifest, raw, sources, new Dictionary<string, string>()),
            item?.CatalogNamespace ?? "", item?.CatalogItemID ?? "");
    }

    public static InstallPlan PlanInstall(InstallSource source, IReadOnlySet<string> tags, Regex? only = null)
    {
        var unknown = tags.Except(source.Manifest.GetInstallTags()).ToList();
        if (unknown.Count > 0)
            throw new InstallException($"Unknown component(s): {string.Join(", ", unknown)}.");
        return InstallPlan.Create(source.Manifest, source.Manifest.SelectFiles(tags).Where(f => only is null || only.IsMatch(f.Filename)));
    }

    /// <summary>
    /// Downloads the plan into <paramref name="directory"/>, then records the install and (for complete engine
    /// installs) registers it in LauncherInstalled.dat so UE tools find it. Re-running resumes.
    /// </summary>
    public static async Task InstallAsync(
        InstallSource source, InstallPlan plan, IReadOnlySet<string> tags, string? fileFilter, string directory,
        Installer installer, InstallStatus status, bool register, CancellationToken cancellationToken)
    {
        await installer.InstallAsync(plan, directory, source.Downloaded.Sources, source.Downloaded.Secrets, status, cancellationToken);

        var manifest = source.Manifest;
        new InstallRecord
        {
            AppName = manifest.Meta.AppName,
            BuildVersion = manifest.Meta.BuildVersion,
            CatalogNamespace = source.CatalogNamespace,
            CatalogItemID = source.CatalogItemID,
            InstallTags = [.. tags.Order(StringComparer.Ordinal)],
            InstallSize = plan.InstallBytes,
            FileFilter = fileFilter,
            BaseURLs = [.. source.Downloaded.Sources.Where(s => s.Query.Length == 0).Select(s => s.BaseURL)],
            InstalledAt = DateTimeOffset.Now,
        }.Save(directory, source.Downloaded.RawBytes);

        // Partial installs aren't usable engines, so UE tools shouldn't be pointed at them.
        if (register && fileFilter is null && IsEngine(manifest))
        {
            EGLInstallations.RegisterLauncherInstall(new LauncherInstalledEntry
            {
                InstallLocation = directory,
                NamespaceID = source.CatalogNamespace.Length > 0 ? source.CatalogNamespace : "ue",
                ItemID = source.CatalogItemID,
                ArtifactID = manifest.Meta.AppName,
                AppVersion = manifest.Meta.BuildVersion,
                AppName = manifest.Meta.AppName,
            });
        }
    }

    public static bool IsEngine(Manifest manifest) => manifest.Meta.AppName.StartsWith("UE_", StringComparison.Ordinal);

    public static ModifyPlan PlanModify(ExistingInstall install, IReadOnlySet<string> newTags)
    {
        var unknown = newTags.Except(install.Manifest.GetInstallTags()).ToList();
        if (unknown.Count > 0)
            throw new InstallException($"Unknown component(s): {string.Join(", ", unknown)}.");

        var currentFiles = install.SelectFiles(null).ToHashSet();
        var newFiles = install.SelectFiles(newTags).ToHashSet();
        var toRemove = currentFiles.Where(f => !newFiles.Contains(f)).ToList();
        var toAdd = newFiles.Where(f => !currentFiles.Contains(f)).ToList();
        return new ModifyPlan(install, newTags, toRemove, toAdd.Count > 0 ? InstallPlan.Create(install.Manifest, toAdd) : null);
    }

    /// <summary>
    /// Applies a modify: records the new selection first (so an interrupted download is repaired towards it),
    /// deletes deselected files, then downloads added ones.
    /// </summary>
    public static async Task<InstallCleaner.Result> ApplyModifyAsync(ModifyPlan plan, Installer installer, InstallStatus status, CancellationToken cancellationToken)
    {
        var install = plan.Install;
        if (plan.ToAdd is not null && install.Sources.Count == 0)
            throw new InstallException("Can't add components: no download location is known for this install.");

        install.ToRecord(plan.NewTags).Save(install.Directory, await File.ReadAllBytesAsync(install.ManifestPath, cancellationToken));

        var cleaned = plan.ToRemove.Count > 0
            ? await Task.Run(() => InstallCleaner.DeleteFiles(install.Directory, plan.ToRemove, cancellationToken), cancellationToken)
            : default;

        if (plan.ToAdd is not null)
            await installer.InstallAsync(plan.ToAdd, install.Directory, install.Sources, new Dictionary<string, string>(), status, cancellationToken);

        return cleaned;
    }
}
