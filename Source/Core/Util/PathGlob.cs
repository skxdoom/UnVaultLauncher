using System.Text;
using System.Text.RegularExpressions;

namespace Unvault.Core.Util;

/// <summary>Case-insensitive globs over manifest paths: <c>**</c> crosses folders, <c>*</c> and <c>?</c> don't.</summary>
public static class PathGlob
{
    public static Regex ToRegex(string glob)
    {
        var pattern = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                pattern.Append(".*");
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                    i++; // "**/" also matches zero folders
            }
            else if (c == '*')
                pattern.Append("[^/]*");
            else if (c == '?')
                pattern.Append("[^/]");
            else if (c == '\\')
                pattern.Append('/');
            else
                pattern.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(pattern.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
