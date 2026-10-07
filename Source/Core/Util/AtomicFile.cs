using System.Text;

namespace UnVault.Core.Util;

/// <summary>
/// Replaces a file in one step: the new content goes beside it first and is then moved over it, so a crash or a full
/// disk midway leaves the previous copy intact instead of a cut-off one.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        string temporary = TemporaryFor(path);
        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, Encoding.UTF8.GetBytes(text));

    /// <summary>A name of its own beside <paramref name="path"/>, so two writers (the app and the CLI) never share one.</summary>
    public static string TemporaryFor(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temporary file is harmless.
        }
    }
}
