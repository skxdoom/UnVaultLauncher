using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.App.Services;
using UnVault.Core.Epic;
using UnVault.Core.Install;
using UnVault.Core.Util;

namespace UnVault.App.ViewModels;

public enum OperationState { Running, Completed, Failed, Cancelled }

/// <summary>
/// One running job (install, modify, verify, repair) shown in the downloads panel. The work reports through
/// an <see cref="InstallStatus"/> that a timer polls; retry re-runs the same work, which resumes.
/// </summary>
public partial class OperationViewModel : ViewModelBase
{
    public delegate Task<string?> Work(OperationViewModel operation, InstallStatus status, CancellationToken cancellationToken);

    private readonly MainViewModel _owner;

    /// <summary>Kept for retry; dropped once done, since it can hold an engine's whole manifest.</summary>
    private Work? _work;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _speedClock = new();
    private CancellationTokenSource? _cancellation;
    private InstallStatus _status = new();
    private long _lastBytes;
    private double _bytesPerSecond;

    public OperationViewModel(MainViewModel owner, string title, Work work, string? engineAppName = null, string? fabItemKey = null)
    {
        _owner = owner;
        _work = work;
        Title = title;
        EngineAppName = engineAppName;
        FabItemKey = fabItemKey;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateProgress());
    }

    public string Title { get; }

    /// <summary>The engine this works on (by app name, since library cards are rebuilt on refresh).</summary>
    public string? EngineAppName { get; }

    /// <summary>The Fab library row this works on (by its key, since rows are rebuilt on refresh).</summary>
    public string? FabItemKey { get; }

    [ObservableProperty] public partial string Phase { get; set; } = "Starting…";
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; set; } = true;
    [ObservableProperty] public partial string ProgressText { get; set; } = "";
    [ObservableProperty] public partial string SpeedText { get; set; } = "";
    [ObservableProperty] public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFinished), nameof(CanRetry), nameof(IsFailed), nameof(IsSucceededOrCancelled))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand), nameof(RetryCommand))]
    public partial OperationState State { get; set; }

    /// <summary>An optional next step offered when done, e.g. "Repair 3 files" after a verify.</summary>
    [ObservableProperty] public partial string? FollowUpText { get; set; }
    public Action? FollowUp { get; set; }

    public bool IsRunning => State == OperationState.Running;
    public bool IsFinished => !IsRunning;
    public bool IsFailed => State == OperationState.Failed;
    public bool IsSucceededOrCancelled => State is OperationState.Completed or OperationState.Cancelled;
    public bool CanRetry => State is OperationState.Failed or OperationState.Cancelled;

    public void SetPhase(string phase) => Dispatcher.UIThread.Post(() => Phase = phase);

    /// <summary>
    /// Starts a new step with its own progress (e.g. copying after a download), so the bar restarts instead of
    /// running past 100%. Use the returned status for that step.
    /// </summary>
    public InstallStatus BeginPhase(string phase)
    {
        var status = new InstallStatus();
        Volatile.Write(ref _status, status);
        _lastBytes = 0;
        SetPhase(phase);
        return status;
    }

    /// <summary>Offers a next step once done (callable from the work's background thread).</summary>
    public void OfferFollowUp(string text, Action action) => Dispatcher.UIThread.Post(() =>
    {
        FollowUp = action;
        FollowUpText = text;
    });

    public async Task RunAsync()
    {
        if (_work is not { } work)
            return;
        _cancellation = new CancellationTokenSource();
        _status = new InstallStatus();
        _lastBytes = 0;
        _bytesPerSecond = 0;
        _speedClock.Restart();
        State = OperationState.Running;
        Message = null;
        FollowUpText = null;
        _owner.SetBusy(this, true);
        _timer.Start();

        try
        {
            Message = await Task.Run(() => work(this, _status, _cancellation.Token));
            State = OperationState.Completed;
            Phase = Strings.PhaseDone;
            _work = null; // nothing to retry
        }
        catch (OperationCanceledException)
        {
            State = OperationState.Cancelled;
            Phase = Strings.PhasePaused;
            Message = Strings.StoppedRetry;
        }
        catch (Exception ex)
        {
            State = OperationState.Failed;
            Phase = Strings.PhaseFailed;
            Message = ex.Message;
            if (ex is NotLoggedInException ended)
                _owner.SessionEnded(ended);
        }
        finally
        {
            _timer.Stop();
            UpdateProgress();
            SpeedText = "";
            _owner.SetBusy(this, false);
            _cancellation.Dispose();
            _cancellation = null;
            MemoryRelief.Release();
        }

        if (State == OperationState.Completed)
            await _owner.OnOperationCompletedAsync(this);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => RunAsync();

    [RelayCommand]
    private void Dismiss()
    {
        if (IsRunning)
            return;
        _owner.Operations.Remove(this);
        MemoryRelief.Release(); // a failed install kept its manifest for retry
    }

    [RelayCommand]
    private void RunFollowUp()
    {
        FollowUp?.Invoke();
        _owner.Operations.Remove(this);
    }

    private void UpdateProgress()
    {
        var status = Volatile.Read(ref _status);
        bool downloading = status.DownloadTotal > 0;
        long done = downloading ? status.DownloadedBytes : status.WrittenBytes;
        long total = downloading ? status.DownloadTotal : status.WriteTotal;

        IsIndeterminate = total == 0;
        Progress = total == 0 ? 0 : 100.0 * done / total;
        ProgressText = total == 0 ? "" : Localized.Format(Strings.ProgressOf, ByteSize.Format(done), ByteSize.Format(total)) +
            (status.FilesTotal > 0 ? "  ·  " + Localized.Plural(nameof(Strings.ProgressFiles_Other), status.FilesTotal, status.FilesDone) : "");

        double seconds = _speedClock.Elapsed.TotalSeconds;
        if (seconds >= 1 && IsRunning)
        {
            // Smoothed speed so the number doesn't jump every tick.
            double instant = (done - _lastBytes) / seconds;
            _bytesPerSecond = _bytesPerSecond == 0 ? instant : _bytesPerSecond * 0.7 + instant * 0.3;
            _lastBytes = done;
            _speedClock.Restart();

            string eta = _bytesPerSecond > 0 && total > done ? "  ·  " + Localized.Format(Strings.TimeLeft, FormatETA((total - done) / _bytesPerSecond)) : "";
            SpeedText = Localized.Format(Strings.PerSecond, ByteSize.Format((long)_bytesPerSecond)) + eta;
        }
    }

    private static string FormatETA(double seconds) => seconds switch
    {
        < 60 => Localized.Format(Strings.DurationSeconds, seconds),
        < 3600 => Localized.Format(Strings.DurationMinutes, seconds / 60),
        _ => Localized.Format(Strings.DurationHoursMinutes, (int)(seconds / 3600), (int)(seconds % 3600 / 60)),
    };
}
