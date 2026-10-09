using UnVault.Core.Epic;
using UnVault.Core.Manifests;
using UnVault.Core.Vault;

namespace UnVault.Core.Fab;

public enum FabItemKind { Plugin, AssetPack, Project, Unknown }

/// <summary>Works out what a Fab item is, from whatever we have: library metadata, a vault entry, or its manifest.</summary>
public static class FabKinds
{
    /// <summary>All Fab content lives in this catalog namespace (seen on every Fab item EGL installed).</summary>
    public const string FabNamespace = "89efe5924d3d467c839449ab6ab52e7f";

    /// <summary>
    /// The Fab library also lists Unreal Engine itself (distributionMethod "ENGINE", artifacts UE_5.8 …); those
    /// belong on the Engines page, not among Fab content.
    /// </summary>
    public static bool IsEngine(FabLibraryItem item) =>
        string.Equals(item.DistributionMethod, "ENGINE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fab's distributionMethod values seen so far: ASSET_PACK, CODE_PLUGIN, COMPLETE_PROJECT, ENGINE.</summary>
    public static FabItemKind FromLibrary(FabLibraryItem item)
    {
        string method = item.DistributionMethod ?? "";
        if (method.Contains("plugin", StringComparison.OrdinalIgnoreCase))
            return FabItemKind.Plugin;
        if (method.Contains("project", StringComparison.OrdinalIgnoreCase))
            return FabItemKind.Project;
        if (method.Contains("asset", StringComparison.OrdinalIgnoreCase))
            return FabItemKind.AssetPack;

        var categories = (item.Categories ?? []).Select(c => c.Name).ToList();
        return FromWords(categories);
    }

    /// <summary>EGL's vault.json categories look like "|Engine Tools|plugins|".</summary>
    public static FabItemKind FromVault(VaultEntry entry) =>
        FromWords((entry.Categories ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The definitive answer once the manifest is known: where its files go.</summary>
    public static FabItemKind FromManifest(Manifest manifest)
    {
        if (manifest.Files.Any(f => f.Filename.StartsWith("Engine/Plugins/", StringComparison.OrdinalIgnoreCase)))
            return FabItemKind.Plugin;
        if (manifest.Files.Any(f => !f.Filename.Contains('/') && f.Filename.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase)))
            return FabItemKind.Project;
        if (manifest.Files.Any(f => f.Filename.StartsWith("Content/", StringComparison.OrdinalIgnoreCase)))
            return FabItemKind.AssetPack;
        return FabItemKind.Unknown;
    }

    /// <summary>"5.7.0-48201490" or "5.7.0-…+++UE5+Dev-Marketplace-Windows" → "UE_5.7".</summary>
    public static string? EngineAppFromBuild(string build)
    {
        var parts = build.Split('.', '-');
        return parts.Length >= 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor)
            ? $"UE_{major}.{minor}"
            : null;
    }

    private static FabItemKind FromWords(IEnumerable<string> words)
    {
        var list = words.ToList();
        if (list.Any(w => w.Contains("plugin", StringComparison.OrdinalIgnoreCase)))
            return FabItemKind.Plugin;
        if (list.Any(w => w.Contains("project", StringComparison.OrdinalIgnoreCase)))
            return FabItemKind.Project;
        if (list.Any(w => w.Equals("assets", StringComparison.OrdinalIgnoreCase) || w.Contains("asset", StringComparison.OrdinalIgnoreCase)))
            return FabItemKind.AssetPack;
        return FabItemKind.Unknown;
    }
}
