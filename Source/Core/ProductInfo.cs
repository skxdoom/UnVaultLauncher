using System.Reflection;

namespace UnVault.Core;

public static class ProductInfo
{
    /// <summary>The product version, e.g. "0.1.0": the &lt;Version&gt; in Directory.Build.props, stamped into every assembly at build time.</summary>
    public static string Version { get; } =
        typeof(ProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
