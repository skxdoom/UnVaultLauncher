using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.App.Services;

namespace UnVault.App.ViewModels;

/// <summary>A finished operation in the Downloads tab's history.</summary>
public partial class HistoryEntryViewModel(OperationRecord record) : ViewModelBase
{
    public OperationRecord Record { get; } = record;
    public string Title => Record.Title;
    public string? Message => Record.Message;
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool IsCompleted => Record.Outcome == OperationOutcome.Completed;
    public bool IsPaused => Record.Outcome == OperationOutcome.Paused;
    public bool IsFailed => Record.Outcome == OperationOutcome.Failed;

    public string OutcomeText => Record.Outcome switch
    {
        OperationOutcome.Completed => Strings.PhaseDone,
        OperationOutcome.Paused => Strings.PhasePaused,
        _ => Strings.PhaseFailed,
    };

    /// <summary>The time for today's, the date and time for older ones.</summary>
    public string WhenText
    {
        get
        {
            var when = Record.FinishedAt.ToLocalTime();
            return when.Date == DateTime.Today ? when.ToString("t", CultureInfo.CurrentCulture) : when.ToString("g", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>A next step the operation offered, e.g. Repair after a verify. This session only: it isn't saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFollowUp))]
    public partial string? FollowUpText { get; set; }

    public Action? FollowUp { get; set; }
    public bool HasFollowUp => FollowUpText is not null;

    /// <summary>Taken once: it starts a new operation of its own.</summary>
    [RelayCommand]
    private void RunFollowUp()
    {
        var followUp = FollowUp;
        FollowUp = null;
        FollowUpText = null;
        followUp?.Invoke();
    }
}
