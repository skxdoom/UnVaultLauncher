namespace UnVault.App.ViewModels;

/// <summary>A dialog that shows which engines or items an operation is working on, kept current while it's open.</summary>
internal interface IShowsBusy
{
    /// <summary>An operation started or ended.</summary>
    void UpdateBusy();
}
