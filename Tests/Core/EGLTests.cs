using System.Text;
using Unvault.Core.EGL;
using Unvault.Core.Install;
using Unvault.Core.Manifests;

namespace Unvault.Core.Tests;

public sealed class LauncherInstalledTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"unvault-egl-{Guid.NewGuid():N}");
    private string DatPath => Path.Combine(_dir, "LauncherInstalled.dat");

    public LauncherInstalledTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Register_keeps_other_entries_and_unknown_fields_and_writes_EGL_style()
    {
        string existingEngine = Directory.CreateDirectory(Path.Combine(_dir, "UE_5.7")).FullName;
        File.WriteAllText(DatPath,
            "{\r\n\t\"InstallationList\": [\r\n\t\t{\r\n" +
            $"\t\t\t\"InstallLocation\": \"{existingEngine.Replace("\\", "\\\\")}\",\r\n" +
            "\t\t\t\"NamespaceId\": \"ue\",\r\n\t\t\t\"AppName\": \"UE_5.7\",\r\n\t\t\t\"Future\": 1\r\n\t\t}\r\n\t]\r\n}");

        EGLInstallations.RegisterLauncherInstall(Entry("UE_5.8", Path.Combine(_dir, "UE_5.8"), "5.8.3-58210709+++UE5+Release-5.8-Windows"), DatPath);

        string text = File.ReadAllText(DatPath);
        Assert.Contains("\"Future\": 1", text);
        Assert.Contains("\"AppName\": \"UE_5.7\"", text);
        Assert.Contains("+++UE5+Release", text);   // '+' not escaped as +
        Assert.Contains("\r\n\t\t{", text);          // tabs + CRLF like EGL
        Assert.True(File.Exists(DatPath + ".unvault-backup"));
        Assert.Equal(["UE_5.7", "UE_5.8"], ReadAppNames());
    }

    [Fact]
    public void Register_replaces_same_folder_and_drops_entries_whose_folder_is_gone()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_dir, "UE_5.8")).FullName;
        EGLInstallations.RegisterLauncherInstall(Entry("UE_5.8", Path.Combine(_dir, "Gone", "UE_5.8"), "old"), DatPath);
        EGLInstallations.RegisterLauncherInstall(Entry("UE_5.8", folder, "5.8.2"), DatPath);
        EGLInstallations.RegisterLauncherInstall(Entry("UE_5.8", folder + "\\", "5.8.3"), DatPath);

        var entries = EGLInstallations.ReadLauncherInstalledFrom(DatPath);
        var only = Assert.Single(entries);
        Assert.Equal("5.8.3", only.AppVersion);
    }

    [Fact]
    public void Unregister_removes_only_that_app_in_that_folder()
    {
        string engine = Directory.CreateDirectory(Path.Combine(_dir, "UE_5.8")).FullName;
        EGLInstallations.RegisterLauncherInstall(Entry("UE_5.8", engine, "5.8.3"), DatPath);
        EGLInstallations.RegisterLauncherInstall(Entry("SomePlugin_5.8", engine, "1.0"), DatPath);

        Assert.True(EGLInstallations.UnregisterLauncherInstall("UE_5.8", engine, DatPath));
        Assert.False(EGLInstallations.UnregisterLauncherInstall("UE_5.8", engine, DatPath));
        Assert.Equal(["SomePlugin_5.8"], ReadAppNames());
    }

    private List<string> ReadAppNames() => EGLInstallations.ReadLauncherInstalledFrom(DatPath).Select(e => e.AppName).ToList();

    private static LauncherInstalledEntry Entry(string app, string location, string version) => new()
    {
        InstallLocation = location,
        NamespaceID = "ue",
        ItemID = "item",
        ArtifactID = app,
        AppVersion = version,
        AppName = app,
    };
}

public sealed class InstallCleanerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"unvault-clean-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Deletes_files_and_emptied_folders_but_keeps_the_rest()
    {
        Write("Engine/Binaries/Linux/a.so", 10);
        Write("Engine/Binaries/Linux/Sub/b.so", 20);
        Write("Engine/Binaries/Win64/keep.dll", 5);
        File.SetAttributes(Path.Combine(_dir, "Engine/Binaries/Linux/a.so"), FileAttributes.ReadOnly);

        var result = InstallCleaner.DeleteFiles(_dir, [ManifestFile("Engine/Binaries/Linux/a.so"), ManifestFile("Engine/Binaries/Linux/Sub/b.so"), ManifestFile("Engine/Missing.txt")]);

        Assert.Equal(2, result.FilesDeleted);
        Assert.Equal(30, result.BytesFreed);
        Assert.False(Directory.Exists(Path.Combine(_dir, "Engine/Binaries/Linux")));
        Assert.True(File.Exists(Path.Combine(_dir, "Engine/Binaries/Win64/keep.dll")));
        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void Refuses_paths_outside_the_install() =>
        Assert.Throws<IOException>(() => InstallCleaner.DeleteFiles(_dir, [ManifestFile("../outside.txt")]));

    private void Write(string relative, int size)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('x', size)));
    }

    private static FileManifest ManifestFile(string name) => new() { Filename = name };
}
