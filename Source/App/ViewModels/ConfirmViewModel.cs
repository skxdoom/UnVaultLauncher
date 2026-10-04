using CommunityToolkit.Mvvm.Input;

namespace UnVault.App.ViewModels;

/// <summary>A yes/no question before something destructive (removing a plugin, deleting files).</summary>
public partial class ConfirmViewModel(MainViewModel owner, string title, string message, string confirmText, Action onConfirm) : ViewModelBase
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;

    [RelayCommand]
    private void Confirm()
    {
        owner.CloseDialog();
        onConfirm();
    }

    [RelayCommand]
    private void Cancel() => owner.CloseDialog();
}
