using System.Xml.Linq;

namespace UnVault.Core.Tests;

public class ProductInfoTests
{
    [Fact]
    public void Version_comes_from_Directory_Build_props()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        string expected = XDocument.Load(Path.Combine(directory.FullName, "Directory.Build.props")).Descendants("Version").Single().Value;
        Assert.Equal(expected, ProductInfo.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+", ProductInfo.Version);
    }
}
