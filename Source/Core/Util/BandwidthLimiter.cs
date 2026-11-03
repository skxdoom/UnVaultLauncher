namespace UnVault.Core.Util;

/// <summary>
/// Caps how fast data arrives, across every download that shares it. Each read draws its bytes from a budget that
/// refills at <see cref="BytesPerSecond"/>; a read that overdraws it waits until the budget is back. Up to a second's
/// worth builds up while idle. 0 is no cap. A new rate applies from the next read on.
/// </summary>
public sealed class BandwidthLimiter(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private long _bytesPerSecond;
    private double _budget;
    private long _refilledAt;

    public long BytesPerSecond
    {
        get
        {
            lock (_lock)
                return _bytesPerSecond;
        }
        set
        {
            lock (_lock)
            {
                Refill();
                _bytesPerSecond = Math.Max(0, value);
                _budget = Math.Min(_budget, _bytesPerSecond); // a lower cap doesn't let a saved-up burst through
            }
        }
    }

    /// <summary>Counts <paramref name="bytes"/> just received; the task ends once the cap allows them.</summary>
    public Task WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        TimeSpan delay = Reserve(bytes);
        return delay == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, _time, cancellationToken);
    }

    /// <summary>Takes <paramref name="bytes"/> from the budget; returns how long to wait before reading on.</summary>
    internal TimeSpan Reserve(int bytes)
    {
        lock (_lock)
        {
            if (_bytesPerSecond == 0)
                return TimeSpan.Zero;
            Refill();
            _budget -= bytes;
            return _budget >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_budget / _bytesPerSecond);
        }
    }

    private void Refill()
    {
        long now = _time.GetTimestamp();
        if (_bytesPerSecond > 0)
            _budget = Math.Min(_bytesPerSecond, _budget + _time.GetElapsedTime(_refilledAt, now).TotalSeconds * _bytesPerSecond);
        _refilledAt = now;
    }
}
