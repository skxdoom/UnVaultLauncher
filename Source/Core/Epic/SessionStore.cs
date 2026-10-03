using System.Security.Cryptography;
using System.Text.Json;

namespace Unvault.Core.Epic;

/// <summary>
/// Persists the Epic session. On Windows the file is encrypted with DPAPI for the current Windows user,
/// so copying it to another account or machine doesn't give access. Elsewhere it's a 0600 file.
/// </summary>
public sealed class SessionStore(string path)
{
    // Ties the DPAPI blob to this app; not a secret.
    private static readonly byte[] Entropy = "Unvault.EpicSession.v1"u8.ToArray();

    public static SessionStore Default { get; } = new(Path.Combine(AppPaths.DataDirectory, "session.dat"));

    public string FilePath => path;

    public EpicAuthSession? Load()
    {
        if (!File.Exists(path))
            return null;
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows())
                data = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize(data, EpicJSONContext.Default.EpicAuthSession);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Unreadable (other user, corrupted, old format): treat as logged out.
            return null;
        }
    }

    public void Save(EpicAuthSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] data = JsonSerializer.SerializeToUtf8Bytes(session, EpicJSONContext.Default.EpicAuthSession);
        if (OperatingSystem.IsWindows())
            data = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

        string temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
