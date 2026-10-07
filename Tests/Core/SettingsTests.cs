using UnVault.Core.EGL;
using UnVault.Core.Vault;

namespace UnVault.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"unvault-settings-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Reads_EGL_launcher_settings_with_repeated_keys()
    {
        string vault = Directory.CreateDirectory(Path.Combine(_dir, "VaultCache")).FullName;
        string ini = Path.Combine(_dir, "GameUserSettings.ini");
        File.WriteAllText(ini,
            ";METADATA=(Diff=true, UseCommands=true)\r\n" +
            "[Launcher]\r\n" +
            "LastActiveVersion=20.3.4\r\n" +
            "VaultCacheDirectories=C:/Program Files (x86)/Epic Games/VaultCache/\r\n" +
            $"VaultCacheDirectories={vault.Replace('\\', '/')}/\r\n" +
            "DefaultAppInstallLocation=E:\\Epic Games\r\n" +
            "CreatedProjectPaths=D:/Projects/Unreal\r\n" +
            "CreatedProjectPaths=E:\r\n" +
            "[Portal.OSS]\r\n" +
            "VaultCacheDirectories=ignored, other section\r\n");

        var settings = EGLLauncherSettings.ReadFrom(ini)!;

        Assert.Equal([@"C:\Program Files (x86)\Epic Games\VaultCache", vault], settings.VaultCacheDirectories);
        Assert.Equal(@"E:\Epic Games", settings.DefaultAppInstallLocation);
        Assert.Equal([@"D:\Projects\Unreal", @"E:\"], settings.CreatedProjectPaths);
        Assert.Equal(vault, settings.ActiveVaultCache); // the last one that exists
    }

    [Fact]
    public void Missing_or_empty_EGL_settings_read_as_null() =>
        Assert.Null(EGLLauncherSettings.ReadFrom(Path.Combine(_dir, "nope.ini")));

    [Fact]
    public void User_folders_win_over_EGL_and_empty_means_automatic()
    {
        string eglVault = Directory.CreateDirectory(Path.Combine(_dir, "EGLVault")).FullName;
        string eglRoot = Directory.CreateDirectory(Path.Combine(_dir, "Engines")).FullName;
        var egl = new EGLLauncherSettings([eglVault], eglRoot, []);

        var automatic = new AppSettings();
        Assert.Equal(new ResolvedFolder(eglVault, SettingSource.EpicGamesLauncher), automatic.ResolveVaultCache(egl));
        Assert.Equal(new ResolvedFolder(eglRoot, SettingSource.EpicGamesLauncher), automatic.ResolveEngineInstallRoot(egl));

        var custom = new AppSettings { VaultCacheDirectory = @"F:\Vault", EngineInstallRoot = @"F:\Engines" };
        Assert.Equal(new ResolvedFolder(@"F:\Vault", SettingSource.User), custom.ResolveVaultCache(egl));
        Assert.Equal(new ResolvedFolder(@"F:\Engines", SettingSource.User), custom.ResolveEngineInstallRoot(egl));
    }

    [Fact]
    public void Without_any_setting_the_Vault_Cache_is_EGLs_default()
    {
        var vault = new AppSettings().ResolveVaultCache(EGLLauncherSettings.Empty);

        // Unless an earlier version's own default folder is in use and EGL's doesn't exist.
        string ownFolder = Path.Combine(AppPaths.DataDirectory, "VaultCache");
        bool keepsOwn = !Directory.Exists(AppSettings.EGLDefaultVaultCache) && Directory.Exists(ownFolder);
        Assert.Equal(keepsOwn ? ownFolder : AppSettings.EGLDefaultVaultCache, vault.Path);
        Assert.EndsWith(@"\Epic\EpicGamesLauncher\VaultCache", AppSettings.EGLDefaultVaultCache);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(500, AppSettings.MaxParallelDownloads)]
    [InlineData(8, 8)]
    public void Parallel_downloads_from_a_hand_edited_file_stay_usable(int written, int expected)
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, $$"""{ "ParallelDownloads": {{written}} }""");
        Assert.Equal(expected, AppSettings.Load(path).ParallelDownloads);
    }

    [Fact]
    public void Data_folder_moves_from_its_old_name_once()
    {
        string old = Path.Combine(_dir, "Unvault");
        Directory.CreateDirectory(Path.Combine(old, "thumbnails"));
        File.WriteAllText(Path.Combine(old, "session.dat"), "session");

        string resolved = AppPaths.ResolveDataDirectory(_dir);

        Assert.Equal(Path.Combine(_dir, "UnVaultLauncher"), resolved);
        Assert.Equal("session", File.ReadAllText(Path.Combine(resolved, "session.dat")));
        Assert.True(Directory.Exists(Path.Combine(resolved, "thumbnails")));
        Assert.False(Directory.Exists(old));

        Assert.Equal(resolved, AppPaths.ResolveDataDirectory(_dir)); // nothing left to move
    }

    [Fact]
    public void Data_folder_merges_when_both_names_exist()
    {
        // As found on a real machine: a stray new folder with older copies, the live data still in the old one.
        string old = Path.Combine(_dir, "Unvault"), current = Path.Combine(_dir, "UnVaultLauncher");
        Directory.CreateDirectory(Path.Combine(old, "manifests"));
        Directory.CreateDirectory(Path.Combine(old, "WebView2", "EBWebView"));
        Directory.CreateDirectory(Path.Combine(current, "manifests"));
        Write(Path.Combine(old, "session.dat"), "session", hoursAgo: 1);
        Write(Path.Combine(old, "fab-library.json"), "fresh list", hoursAgo: 1);
        Write(Path.Combine(old, "manifests", "A.manifest"), "a", hoursAgo: 30);
        Write(Path.Combine(old, "WebView2", "EBWebView", "Local State"), "profile", hoursAgo: 1);
        Write(Path.Combine(old, "crash.log"), "old log", hoursAgo: 40);
        Write(Path.Combine(current, "fab-library.json"), "stale list", hoursAgo: 24);
        Write(Path.Combine(current, "manifests", "B.manifest"), "b", hoursAgo: 30);
        Write(Path.Combine(current, "crash.log"), "new log", hoursAgo: 2);

        Assert.Equal(current, AppPaths.ResolveDataDirectory(_dir));

        Assert.False(Directory.Exists(old));
        Assert.Equal("session", File.ReadAllText(Path.Combine(current, "session.dat")));
        Assert.Equal("fresh list", File.ReadAllText(Path.Combine(current, "fab-library.json")));
        Assert.Equal("new log", File.ReadAllText(Path.Combine(current, "crash.log")));
        Assert.Equal(["A.manifest", "B.manifest"], Directory.GetFiles(Path.Combine(current, "manifests")).Select(Path.GetFileName).Order());
        Assert.Equal("profile", File.ReadAllText(Path.Combine(current, "WebView2", "EBWebView", "Local State")));

        static void Write(string path, string text, int hoursAgo)
        {
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-hoursAgo));
        }
    }

    [Fact]
    public void Project_folders_are_the_users_else_EGLs_else_documents()
    {
        string eglFirst = Path.Combine(_dir, "Missing");
        string eglSecond = Directory.CreateDirectory(Path.Combine(_dir, "Unreal Projects")).FullName;
        var egl = new EGLLauncherSettings([], null, [eglFirst, eglSecond]);

        var fromEGL = new AppSettings().ResolveProjectFolders(egl);
        Assert.Equal(SettingSource.EpicGamesLauncher, fromEGL.Source);
        Assert.Equal([eglFirst, eglSecond], fromEGL.Paths);
        Assert.Equal(eglSecond, fromEGL.ForNewProjects); // automatic: the first one that exists

        var fallback = new AppSettings().ResolveProjectFolders(EGLLauncherSettings.Empty);
        Assert.Equal(SettingSource.Default, fallback.Source);
        Assert.Equal([AppSettings.DefaultProjectFolder], fallback.Paths);

        // The user's choice is taken as is, even before the first folder exists.
        var user = new AppSettings { ProjectDirectories = [@"F:\Projects", eglSecond] }.ResolveProjectFolders(egl);
        Assert.Equal((SettingSource.User, @"F:\Projects"), (user.Source, user.ForNewProjects));
        Assert.Equal(SettingSource.EpicGamesLauncher, new AppSettings { ProjectDirectories = [] }.ResolveProjectFolders(egl).Source);
    }

    [Fact]
    public void Settings_round_trip_and_survive_a_broken_file()
    {
        string path = Path.Combine(_dir, "settings.json");
        new AppSettings { VaultCacheDirectory = @"E:\Epic Games\VaultCache", ParallelDownloads = 32, ProjectDirectories = [@"D:\Projects\Unreal", @"E:\Unreal Projects"] }.Save(path);

        var loaded = AppSettings.Load(path);
        Assert.Equal(@"E:\Epic Games\VaultCache", loaded.VaultCacheDirectory);
        Assert.Equal([@"D:\Projects\Unreal", @"E:\Unreal Projects"], loaded.ProjectDirectories!);
        Assert.Null(loaded.EngineInstallRoot);
        Assert.Equal(32, loaded.ParallelDownloads);

        File.WriteAllText(path, "{ not json");
        Assert.Equal(AppSettings.DefaultParallelDownloads, AppSettings.Load(path).ParallelDownloads);
    }

    [Fact]
    public void Scans_an_EGL_vault_cache()
    {
        string entry = Directory.CreateDirectory(Path.Combine(_dir, "Vault", "Warehous3c4d5e6f7a8bV5")).FullName;
        File.WriteAllText(Path.Combine(entry, "vault.json"),
            """{"path":"C:\\old\\place\\","size":3344583239,"version":"5.8.0-46051459","build":"5.8.0-46051459+++UE5+Dev-Marketplace-Windows","assetId":"Warehous3c4d5e6f7a8bV5","id":"1f2e3d4c5b6a79881f2e3d4c5b6a7988","title":"Modular Warehouse  V. 2","vaultJSONVersion":1}""");
        Directory.CreateDirectory(Path.Combine(_dir, "Vault", "NotAnAsset"));

        var only = Assert.Single(VaultCache.Scan(Path.Combine(_dir, "Vault")));
        Assert.Equal("Warehous3c4d5e6f7a8bV5", only.ArtifactID);
        Assert.Equal(entry, only.Directory); // where it is now, not EGL's possibly stale "path"
        Assert.Equal(new VaultSummary(1, 3344583239), VaultCache.Summarize(Path.Combine(_dir, "Vault")));
    }
}
