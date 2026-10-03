using Avalonia.Data.Converters;

namespace Unvault.App.ViewModels;

public static class Converters
{
    /// <summary>Dims list entries that can't be chosen (incompatible engine, missing version…).</summary>
    public static readonly IValueConverter AvailableOpacity = new FuncValueConverter<bool, double>(available => available ? 1.0 : 0.45);
}
