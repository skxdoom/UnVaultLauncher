using System.Text;
using UnVault.Core.Epic;
using UnVault.Core.Fab;
using UnVault.Core.Install;
using UnVault.Core.Manifests;
using UnVault.Core.Projects;
using UnVault.Core.Vault;

namespace UnVault.Core.Tests;

/// <summary>Writes Epic's JSON manifest format (numbers as little-endian decimal "blobs") for test content.</summary>
internal static class JSONManifestBuilder
{
    public static string Build(string appName, string build, params (string Name, int Size)[] files)
    {
        var json = new StringBuilder();
        json.Append($$"""{"ManifestFileVersion":"{{Blob(13, 4)}}","AppNameString":"{{appName}}","BuildVersionString":"{{build}}","FileManifestList":[""");
        var guids = new List<string>();
        for (int i = 0; i < files.Length; i++)
        {
            string guid = (i + 1).ToString("X32");
            guids.Add(guid);
            if (i > 0) json.Append(',');
            json.Append($$"""{"Filename":"{{files[i].Name}}","FileHash":"{{Blob(0, 20)}}","FileChunkParts":[{"Guid":"{{guid}}","Offset":"{{Blob(0, 4)}}","Size":"{{Blob(files[i].Size, 4)}}"}]}""");
        }
        json.Append("""],"ChunkHashList":{""").Append(string.Join(",", guids.Select(g => $"\"{g}\":\"{Blob(0, 8)}\"")));
        json.Append("""},"DataGroupList":{""").Append(string.Join(",", guids.Select(g => $"\"{g}\":\"{Blob(0, 1)}\"")));
        json.Append("""},"ChunkFilesizeList":{""").Append(string.Join(",", guids.Select(g => $"\"{g}\":\"{Blob(100, 8)}\"")));
        return json.Append("}}").ToString();
    }

    private static string Blob(long value, int bytes)
    {
        var text = new StringBuilder();
        for (int i = 0; i < bytes; i++, value >>= 8)
            text.Append((value & 0xFF).ToString("D3"));
        return text.ToString();
    }
}

public sealed class FabTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"unvault-fab-{Guid.NewGuid():N}")).FullName;
    private string LauncherInstalled => Path.Combine(_dir, "LauncherInstalled.dat");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("Engine/Plugins/Marketplace/EdgeSmoo0a1b2c3d4e5fV6/EdgeSmoother.uplugin", FabItemKind.Plugin)]
    [InlineData("Warehouse.uproject", FabItemKind.Project)]
    [InlineData("Content/StreetVehicles/Car.uasset", FabItemKind.AssetPack)]
    [InlineData("Docs/readme.txt", FabItemKind.Unknown)]
    public void Kind_comes_from_where_the_manifest_puts_files(string file, FabItemKind expected) =>
        Assert.Equal(expected, FabKinds.FromManifest(Manifest.Parse(Encoding.UTF8.GetBytes(JSONManifestBuilder.Build("X", "1.0", (file, 10))))));

    /// <summary>Shape of a real fab.com/e/accounts/{id}/ue/library response (trimmed): numbers-as-strings, ENGINE items.</summary>
    [Fact]
    public void Parses_a_real_Fab_library_page()
    {
        const string page = """
            {"cursors":{"next":null},"results":[
              {"assetId":"a1b2c3d4e5f60718293a4b5c6d7e8f90","assetNamespace":"89efe5924d3d467c839449ab6ab52e7f",
               "categories":[{"id":"0bb33d40","name":"Vehicles & Transportation"}],"description":"Cargo","listingType":"3d-model",
               "seller":"Someone","distributionMethod":"ASSET_PACK",
               "images":[{"md5":null,"type":"Featured","url":"https://media.fab.com/a.jpg","width":"640","height":"349","uploadedDate":"2024-10-17T13:34:32Z"},
                         {"md5":null,"type":"Thumbnail","url":"https://media.fab.com/b.jpg","width":"320","height":"180","uploadedDate":"2024-10-17T13:34:32Z"}],
               "projectVersions":[{"artifactId":"CargoTru1a2b3c4d5e6fV1","engineVersions":["UE_4.22","UE_4.23"],"targetPlatforms":["Windows"],
                                   "buildVersions":[{"buildVersion":"4.23.0-13656544+++depot+UE4-UserContent-Windows","platform":"Windows"}]}],
               "source":"fab","title":"Cargo Trucks","url":"https://www.fab.com/listings/0000aaaa",
               "customAttributes":{"ListingIdentifier":"0000aaaa"},"legacyItemId":null},
              {"assetId":"x","assetNamespace":"ue","categories":[],"distributionMethod":"ENGINE","images":[],
               "projectVersions":[{"artifactId":"UE_5.8","engineVersions":[],"targetPlatforms":[],"buildVersions":[]}],
               "source":"uem","title":"Unreal Engine","url":null,"customAttributes":{},"legacyItemId":null},
              {"assetId":"y","assetNamespace":"89efe5924d3d467c839449ab6ab52e7f","distributionMethod":"CODE_PLUGIN","images":null,
               "projectVersions":[{"artifactId":"Plugin_57","engineVersions":["UE_5.7"]}],"title":"Some Plugin"}
            ]}
            """;

        var parsed = System.Text.Json.JsonSerializer.Deserialize(page, FabJSONContext.Default.FabLibraryPage)!;
        var items = parsed.Results!;

        Assert.Null(parsed.Cursors?.Next);
        Assert.Equal(FabItemKind.AssetPack, FabKinds.FromLibrary(items[0]));
        Assert.Equal(640, items[0].Images![0].Width);
        Assert.Equal("https://media.fab.com/b.jpg", items[0].ThumbnailURL); // 320 px: the narrowest at least 300 wide
        Assert.True(FabKinds.IsEngine(items[1]));
        Assert.Equal(FabItemKind.Plugin, FabKinds.FromLibrary(items[2]));
        Assert.Null(items[2].ThumbnailURL);
        Assert.Equal("Someone", items[0].Seller);
    }

    [Fact]
    public void Library_cache_round_trips_for_its_own_account_only()
    {
        string path = Path.Combine(Path.GetTempPath(), $"unvault-fab-library-{Guid.NewGuid():N}.json");
        try
        {
            FabLibraryCache.Save(new FabLibrarySnapshot
            {
                AccountID = "account1",
                FetchedAt = new DateTimeOffset(2026, 10, 2, 18, 0, 0, TimeSpan.Zero),
                Items =
                [
                    new FabLibraryItem
                    {
                        AssetID = "a", AssetNamespace = FabKinds.FabNamespace, Title = "Garden Pack", Seller = "Contoso Art", DistributionMethod = "ASSET_PACK",
                        Images = [new FabImage { URL = "https://media.fab.com/p.jpg", Width = 640, Height = 360 }],
                        ProjectVersions = [new FabProjectVersion { ArtifactID = "GardenPack", EngineVersions = ["UE_5.4"] }],
                    },
                ],
            }, path);

            var loaded = FabLibraryCache.Load("ACCOUNT1", path);
            var item = Assert.Single(loaded!.Items);
            Assert.Equal(("Garden Pack", "Contoso Art", "https://media.fab.com/p.jpg"), (item.Title, item.Seller, item.ThumbnailURL));
            Assert.Equal(["UE_5.4"], item.EngineVersions);
            Assert.Equal(FabItemKind.AssetPack, FabKinds.FromLibrary(item));

            Assert.Null(FabLibraryCache.Load("someone-else", path));
            File.WriteAllText(path, "{ damaged");
            Assert.Null(FabLibraryCache.Load("account1", path));
            FabLibraryCache.Delete(path);
            Assert.False(File.Exists(path));
            Assert.Null(FabLibraryCache.Load("account1", path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Library_entries_for_the_same_asset_are_merged()
    {
        // Fab lists some products twice, far apart in the list, sometimes with a newer picture on one copy.
        FabLibraryItem Entry(string assetID, string seller, string picture, DateTimeOffset uploaded, params FabProjectVersion[] versions) => new()
        {
            AssetID = assetID, AssetNamespace = FabKinds.FabNamespace, Title = "Garden Pack", Seller = seller,
            Images = [new FabImage { URL = picture, Width = 640, UploadedDate = uploaded }], ProjectVersions = [.. versions],
        };
        FabProjectVersion Version(string artifact, params string[] engines) => new() { ArtifactID = artifact, EngineVersions = [.. engines] };

        var merged = EpicAPIClient.MergeDuplicateEntries(
        [
            Entry("a", "Fabrikam Studio", "old.jpg", new(2024, 10, 18, 0, 0, 0, TimeSpan.Zero), Version("GardenV1", "UE_4.27"), Version("GardenV2", "UE_5.4")),
            Entry("b", "Contoso Art", "other.jpg", new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), Version("GardenPack", "UE_5.4")),
            Entry("a", "Fabrikam Studio", "new.jpg", new(2025, 6, 18, 0, 0, 0, TimeSpan.Zero), Version("GardenV2", "UE_5.4", "UE_5.5"), Version("GardenV3", "UE_5.8")),
        ]);

        // Same title, different asset: a different product, kept.
        Assert.Equal(["a", "b"], merged.Select(i => i.AssetID));
        var plants = merged[0];
        Assert.Equal("new.jpg", plants.ThumbnailURL);
        Assert.Equal(["GardenV2", "GardenV3", "GardenV1"], plants.ProjectVersions!.Select(v => v.ArtifactID));
        Assert.Equal(["UE_5.4", "UE_5.5"], plants.ProjectVersions![0].EngineVersions!);
    }

    [Theory]
    [InlineData("ASSET_PACK", FabItemKind.AssetPack)]
    [InlineData("CODE_PLUGIN", FabItemKind.Plugin)]
    [InlineData("COMPLETE_PROJECT", FabItemKind.Project)]
    public void Kind_from_Fab_distribution_method(string method, FabItemKind expected) =>
        Assert.Equal(expected, FabKinds.FromLibrary(new FabLibraryItem { DistributionMethod = method }));

    [Theory]
    [InlineData("|Engine Tools|plugins|", FabItemKind.Plugin)]
    [InlineData("|Vehicles & Transportation|assets|", FabItemKind.AssetPack)]
    [InlineData("|Industrial|projects|", FabItemKind.Project)]
    public void Kind_from_EGL_vault_categories(string categories, FabItemKind expected) =>
        Assert.Equal(expected, FabKinds.FromVault(new VaultEntry { Categories = categories }));

    [Theory]
    [InlineData("5.7.0-48201490", "UE_5.7")]
    [InlineData("4.27.0-17665030+++UE5+Dev-Marketplace-Windows", "UE_4.27")]
    [InlineData("nonsense", null)]
    public void Engine_from_build_version(string build, string? expected) =>
        Assert.Equal(expected, FabKinds.EngineAppFromBuild(build));

    [Fact]
    public void Picks_the_version_for_an_engine_with_older_fallback_only_for_content()
    {
        var item = new FabLibraryItem
        {
            ProjectVersions =
            [
                new FabProjectVersion { ArtifactID = "Pack_5.3", EngineVersions = ["UE_5.3", "UE_5.4"] },
                new FabProjectVersion { ArtifactID = "Pack_5.6", EngineVersions = ["UE_5.6"] },
            ],
        };

        Assert.Equal("Pack_5.3", FabWorkflow.PickVersion(item, "UE_5.4", allowOlder: false)?.ArtifactID);
        Assert.Null(FabWorkflow.PickVersion(item, "UE_5.7", allowOlder: false));
        Assert.Equal("Pack_5.6", FabWorkflow.PickVersion(item, "UE_5.7", allowOlder: true)?.ArtifactID);
        Assert.Equal("Pack_5.3", FabWorkflow.PickVersion(item, "UE_5.5", allowOlder: true)?.ArtifactID);
        Assert.Null(FabWorkflow.PickVersion(item, "UE_5.2", allowOlder: true));
    }

    [Fact]
    public async Task Installs_a_plugin_from_the_vault_and_removes_it_again()
    {
        var entry = MakeVaultEntry("EdgeSmoo0a1b2c3d4e5fV6", "Edge Smoother", "5.7.0-1+++UE5+Dev-Marketplace-Windows",
            ("Engine/Plugins/Marketplace/EdgeSmoo0a1b2c3d4e5fV6/EdgeSmoother.uplugin", 20),
            ("Engine/Plugins/Marketplace/EdgeSmoo0a1b2c3d4e5fV6/Binaries/Win64/EdgeSmoother.dll", 50));
        string engine = Directory.CreateDirectory(Path.Combine(_dir, "UE_5.7")).FullName;
        string keep = Path.Combine(engine, "Engine", "Binaries", "Win64", "UnrealEditor.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
        File.WriteAllText(keep, "engine");

        var status = new InstallStatus();
        await FabWorkflow.InstallPluginFromVaultAsync(entry, engine, status, default, LauncherInstalled);

        Assert.Equal(20, new FileInfo(Path.Combine(engine, "Engine/Plugins/Marketplace/EdgeSmoo0a1b2c3d4e5fV6/EdgeSmoother.uplugin")).Length);
        Assert.Equal(70, status.WrittenBytes);
        Assert.Contains("EdgeSmoo0a1b2c3d4e5fV6", PluginInstalls.List(engine).Select(p => p.ArtifactID));
        Assert.Contains(EGL.EGLInstallations.ReadLauncherInstalledFrom(LauncherInstalled), e => e.AppName == "EdgeSmoo0a1b2c3d4e5fV6" && e.InstallLocation == engine);

        var removed = FabWorkflow.UninstallPlugin(engine, "EdgeSmoo0a1b2c3d4e5fV6", LauncherInstalled);

        Assert.Equal(2, removed.FilesDeleted);
        Assert.False(Directory.Exists(Path.Combine(engine, "Engine", "Plugins")));
        Assert.True(File.Exists(keep));
        Assert.Empty(PluginInstalls.List(engine));
        Assert.Empty(EGL.EGLInstallations.ReadLauncherInstalledFrom(LauncherInstalled));
    }

    [Fact]
    public void Finds_and_removes_plugins_EGL_or_something_else_put_into_an_engine()
    {
        // As on a real machine: EGL lists one plugin; another plugin folder sits there unlisted.
        string engine = Directory.CreateDirectory(Path.Combine(_dir, "UE_5.7")).FullName;
        string listed = Path.Combine(engine, @"Engine\Plugins\Marketplace\EdgeSmoo0a1b2c3d4e5fV6");
        string unlisted = Path.Combine(engine, @"Engine\Plugins\Marketplace\Greybox9f8e7d6c5b4aV14");
        foreach (string folder in new[] { listed, unlisted })
        {
            Directory.CreateDirectory(Path.Combine(folder, @"Binaries\Win64"));
            File.WriteAllBytes(Path.Combine(folder, "Plugin.uplugin"), new byte[10]);
            File.WriteAllBytes(Path.Combine(folder, @"Binaries\Win64\Plugin.dll"), new byte[30]);
        }
        File.SetAttributes(Path.Combine(listed, "Plugin.uplugin"), FileAttributes.ReadOnly);
        EGL.EGLInstallations.RegisterLauncherInstall(new EGL.LauncherInstalledEntry { AppName = "EdgeSmoo0a1b2c3d4e5fV6", ArtifactID = "EdgeSmoo0a1b2c3d4e5fV6", InstallLocation = engine }, LauncherInstalled);
        EGL.EGLInstallations.RegisterLauncherInstall(new EGL.LauncherInstalledEntry { AppName = "UE_5.7", InstallLocation = engine }, LauncherInstalled);
        // EGL puts a few of Epic's own plugins in Engine\Plugins\<Name>, among the engine's own plugins: listed, not removable.
        Directory.CreateDirectory(Path.Combine(engine, @"Engine\Plugins\BundledTool"));
        EGL.EGLInstallations.RegisterLauncherInstall(new EGL.LauncherInstalledEntry { AppName = "BundledTool_5.7", ArtifactID = "BundledTool_5.7", InstallLocation = engine }, LauncherInstalled);

        var plugins = EnginePlugins.Find(engine, EGL.EGLInstallations.ReadLauncherInstalledFrom(LauncherInstalled));
        Assert.Equal(
            [("BundledTool_5.7", PluginSource.EGL, false), ("EdgeSmoo0a1b2c3d4e5fV6", PluginSource.EGL, true), ("Greybox9f8e7d6c5b4aV14", PluginSource.Unlisted, true)],
            plugins.Select(p => (p.ArtifactID, p.Source, p.CanRemove)).OrderBy(p => p.ArtifactID));
        Assert.Throws<InstallException>(() => FabWorkflow.UninstallPlugin(engine, "BundledTool_5.7", LauncherInstalled));
        Assert.Equal(40, EnginePlugins.FolderSize(listed));

        var removed = FabWorkflow.UninstallPlugin(engine, "EdgeSmoo0a1b2c3d4e5fV6", LauncherInstalled);

        Assert.Equal((2, 40L), (removed.FilesDeleted, removed.BytesFreed));
        Assert.False(Directory.Exists(listed));
        Assert.True(Directory.Exists(unlisted));
        Assert.Equal(["UE_5.7", "BundledTool_5.7"], EGL.EGLInstallations.ReadLauncherInstalledFrom(LauncherInstalled).Select(e => e.AppName));

        FabWorkflow.UninstallPlugin(engine, "Greybox9f8e7d6c5b4aV14", LauncherInstalled);
        Assert.Empty(Directory.EnumerateDirectories(EnginePlugins.MarketplaceDirectory(engine)));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\..\Engine")]
    [InlineData("")]
    public void Refuses_to_remove_anything_but_a_plugin_folder(string artifactID) =>
        Assert.Throws<InstallException>(() => FabWorkflow.UninstallPlugin(_dir, artifactID, LauncherInstalled));

    [Fact]
    public async Task Updating_a_vault_copy_swaps_its_manifest_and_drops_files_the_new_build_lacks()
    {
        var entry = MakeVaultEntry("GardenV2", "Garden Pack", "5.4.0-1", ("Content/Garden/Rock.uasset", 30), ("Content/Garden/Old.uasset", 20));
        File.WriteAllText(Path.Combine(entry.Directory, "manifest.json"), "{}"); // EGL's JSON copy of the old manifest
        string next = JSONManifestBuilder.Build("GardenV2", "5.4.0-2", ("Content/Garden/Rock.uasset", 30));
        var source = Source(next);

        var updated = await FabWorkflow.DownloadToVaultAsync(source, Artifact("GardenV2", "Garden Pack"), Path.Combine(_dir, "Vault"),
            new Installer(new HttpClient()), new InstallStatus(), default);

        Assert.Equal("5.4.0-2", updated.Build);
        Assert.Equal(next, File.ReadAllText(entry.ManifestPath));
        Assert.True(File.Exists(Path.Combine(entry.DataDirectory, "Content/Garden/Rock.uasset"))); // unchanged, kept
        Assert.False(File.Exists(Path.Combine(entry.DataDirectory, "Content/Garden/Old.uasset")));
        Assert.False(File.Exists(Path.Combine(entry.Directory, "manifest.json")));
        Assert.False(Directory.Exists(Path.Combine(entry.Directory, ".unvault")));
        Assert.Equal("5.4.0-2", VaultCache.Scan(Path.Combine(_dir, "Vault")).Single().Build);
    }

    [Fact]
    public async Task Plans_an_update_of_a_plugin_EGL_installed_from_what_is_on_disk()
    {
        string engine = Directory.CreateDirectory(Path.Combine(_dir, "UE_5.7")).FullName;
        const string Root = "Engine/Plugins/Marketplace/Greybox9f8e7d6c5b4aV14";
        Directory.CreateDirectory(Path.Combine(engine, Root, "Binaries/Win64"));
        File.WriteAllText(Path.Combine(engine, Root, "Greybox.uplugin"), "same");
        File.WriteAllText(Path.Combine(engine, Root, "Binaries/Win64/Old.dll"), "old build only");
        FileManifest Entry(string name, string content) => new()
            { Filename = name, FileSize = content.Length, SHA1 = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(content)) };
        var next = new Manifest
        {
            Version = 21, Meta = new ManifestMeta { FeatureLevel = 21, BuildVersion = "5.7.0-2" }, Chunks = [],
            Files = [Entry($"{Root}/Greybox.uplugin", "same"), Entry($"{Root}/Binaries/Win64/New.dll", "new build")],
            CustomFields = new Dictionary<string, string>(),
        };
        var source = new InstallSource(new DownloadedManifest(next, [], [new ChunkSource("https://cdn.test/CloudDir")], new Dictionary<string, string>()),
            FabKinds.FabNamespace, "item");

        var update = await FabWorkflow.PlanPluginUpdateAsync(source, Artifact("Greybox9f8e7d6c5b4aV14", "Greybox Tools"), engine, new InstallStatus(), default);

        Assert.Equal([$"{Root}/Binaries/Win64/New.dll"], update.Write.Select(f => f.Filename));
        Assert.Equal([$"{Root}/Binaries/Win64/Old.dll"], update.Delete.Select(f => f.Filename));
    }

    private static InstallSource Source(string jsonManifest) => new(
        new DownloadedManifest(Manifest.Parse(Encoding.UTF8.GetBytes(jsonManifest)), Encoding.UTF8.GetBytes(jsonManifest),
            [new ChunkSource("https://cdn.test/CloudDir")], new Dictionary<string, string>()),
        FabKinds.FabNamespace, "item");

    private static FabArtifact Artifact(string artifactID, string title) => new(
        new FabLibraryItem { AssetID = "item", AssetNamespace = FabKinds.FabNamespace, Title = title },
        new FabProjectVersion { ArtifactID = artifactID });

    [Fact]
    public async Task Refuses_to_install_content_into_an_engine()
    {
        var entry = MakeVaultEntry("Pack", "Pack", "5.7.0-1", ("Content/Pack/A.uasset", 5));
        await Assert.ThrowsAsync<InstallException>(() =>
            FabWorkflow.InstallPluginFromVaultAsync(entry, Path.Combine(_dir, "UE_5.7"), new InstallStatus(), default, LauncherInstalled));
    }

    [Fact]
    public async Task Adds_only_content_to_a_project()
    {
        var entry = MakeVaultEntry("StreetVehicles", "Street Vehicles", "5.7.0-1",
            ("Content/StreetVehicles/Car.uasset", 30), ("Config/DefaultInput.ini", 5), ("Pack.uproject", 5));
        string project = Directory.CreateDirectory(Path.Combine(_dir, "MyGame")).FullName;

        int copied = await FabWorkflow.AddToProjectAsync(entry, project, new InstallStatus(), default);

        Assert.Equal(1, copied);
        Assert.True(File.Exists(Path.Combine(project, "Content", "StreetVehicles", "Car.uasset")));
        Assert.False(Directory.Exists(Path.Combine(project, "Config")));
        Assert.False(File.Exists(Path.Combine(project, "Pack.uproject")));
    }

    [Fact]
    public async Task Creates_a_project_but_not_over_an_existing_one()
    {
        var entry = MakeVaultEntry("Warehouse", "Warehouse", "5.8.0-1",
            ("Warehouse.uproject", 10), ("Content/Maps/Main.umap", 40), ("Config/DefaultEngine.ini", 5));
        string target = Path.Combine(_dir, "Projects", "Warehouse");

        string uproject = await FabWorkflow.CreateProjectAsync(entry, target, new InstallStatus(), default);

        Assert.Equal(Path.Combine(target, "Warehouse.uproject"), uproject);
        Assert.True(File.Exists(Path.Combine(target, "Config", "DefaultEngine.ini")));
        await Assert.ThrowsAsync<InstallException>(() => FabWorkflow.CreateProjectAsync(entry, target, new InstallStatus(), default));
    }

    [Fact]
    public void Written_vault_entries_read_back_like_EGL_ones()
    {
        var entry = new VaultEntry
        {
            ArtifactID = "Greybox9f8e7d6c5b4aV14",
            Title = "Greybox Tools",
            Build = "5.7.0-48201490+++UE5+Dev-Marketplace-Windows",
            Version = "5.7.0-48201490",
            Size = 170,
            Categories = "|Engine Tools|plugins|",
            Directory = Path.Combine(_dir, "Vault", "Greybox9f8e7d6c5b4aV14"),
        };
        Directory.CreateDirectory(entry.DataDirectory);

        VaultCache.WriteEntry(entry, [1, 2, 3]);

        string json = File.ReadAllText(Path.Combine(entry.Directory, "vault.json"));
        Assert.Contains("+++UE5+Dev-Marketplace", json); // readable, like EGL's
        var read = Assert.Single(VaultCache.Scan(Path.Combine(_dir, "Vault")));
        Assert.Equal("Greybox Tools", read.Title);
        Assert.True(read.IsComplete);
        Assert.Equal(FabItemKind.Plugin, FabKinds.FromVault(read));
    }

    [Fact]
    public void Finds_recent_and_created_projects()
    {
        string config = Path.Combine(_dir, "UnrealEngine");
        string recent = WriteProject(Path.Combine(_dir, "Recent", "Recent.uproject"), "5.7");
        string created = WriteProject(Path.Combine(_dir, "Created", "Game", "Game.uproject"), "5.5");
        string ini = Path.Combine(config, "5.7", "Saved", "Config", "WindowsEditor", "EditorSettings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini,
            "[/Script/UnrealEd.EditorSettings]\r\n" +
            $"RecentlyOpenedProjectFiles=(ProjectName=\"{recent.Replace('\\', '/')}\",LastOpenTime=2026.09.27-23.18.27)\r\n" +
            "RecentlyOpenedProjectFiles=(ProjectName=\"Z:/Gone/Gone.uproject\",LastOpenTime=2026.01.01-00.00.00)\r\n");

        var projects = ProjectLocator.FindProjects(config, [Path.Combine(_dir, "Created")]);

        Assert.Equal(["Recent", "Game"], projects.Select(p => p.Name)); // recent first; missing files dropped
        Assert.Equal("UE_5.7", projects[0].EngineAppName);
        Assert.Equal(new DateTime(2026, 9, 27, 23, 18, 27), projects[0].LastOpened!.Value.ToUniversalTime());
        Assert.Equal("UE_5.5", projects[1].EngineAppName);
    }

    [Fact]
    public void Custom_engines_are_known_by_ID_and_versioned_from_their_folder()
    {
        string engine = Path.Combine(_dir, "MyEngine");
        Directory.CreateDirectory(Path.Combine(engine, "Engine", "Build"));
        File.WriteAllText(Path.Combine(engine, "Engine", "Build", "Build.version"),
            """{ "MajorVersion": 4, "MinorVersion": 27, "PatchVersion": 2, "Changelist": 0, "BranchName": "++UE4+Release-4.27" }""");

        Assert.Equal("4.27", CustomEngines.ReadVersion(engine));
        Assert.Null(CustomEngines.ReadVersion(Path.Combine(_dir, "NoEngineHere")));

        // Projects write the ID with braces; the registry may or may not.
        var engines = new Dictionary<string, CustomEngine>(StringComparer.OrdinalIgnoreCase)
        {
            ["5C3B2A10-0000-4000-8000-000000000001"] = new("{5C3B2A10-0000-4000-8000-000000000001}", engine, "4.27"),
        };
        Assert.Equal("4.27", CustomEngines.Find(engines, "{5c3b2a10-0000-4000-8000-000000000001}")?.Version);
        Assert.Null(CustomEngines.Find(engines, "{5C3B2A10-0000-4000-8000-000000000002}"));
        Assert.Null(CustomEngines.Find(engines, ""));
    }

    private VaultEntry MakeVaultEntry(string artifact, string title, string build, params (string Name, int Size)[] files)
    {
        var entry = new VaultEntry { ArtifactID = artifact, Title = title, Build = build, Directory = Path.Combine(_dir, "Vault", artifact) };
        foreach (var (name, size) in files)
        {
            string path = Path.Combine(entry.DataDirectory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[size]);
        }
        File.WriteAllText(entry.ManifestPath, JSONManifestBuilder.Build(artifact, build, files));
        return entry;
    }

    private static string WriteProject(string path, string engine)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""{ "FileVersion": 3, "EngineAssociation": "{{engine}}", "Category": "" }""");
        return path;
    }
}
