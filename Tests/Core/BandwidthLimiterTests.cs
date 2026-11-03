using UnVault.Core.Util;

namespace UnVault.Core.Tests;

public sealed class BandwidthLimiterTests
{
    private const int MB = 1024 * 1024;

    /// <summary>A clock that only moves when told to.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Fact]
    public void Without_a_limit_nothing_waits()
    {
        var limiter = new BandwidthLimiter(new ManualClock());
        for (int i = 0; i < 100; i++)
            Assert.Equal(TimeSpan.Zero, limiter.Reserve(MB));
        Assert.True(limiter.WaitAsync(MB, default).IsCompleted);
    }

    [Fact]
    public void Reads_wait_until_the_limit_allows_them()
    {
        var clock = new ManualClock();
        var limiter = new BandwidthLimiter(clock) { BytesPerSecond = MB };

        Assert.Equal(TimeSpan.FromSeconds(0.5), limiter.Reserve(MB / 2));
        clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(TimeSpan.FromSeconds(0.5), limiter.Reserve(MB / 2));
    }

    /// <summary>Downloads running at once share the limit: each waits behind what the others took.</summary>
    [Fact]
    public void Downloads_at_once_share_the_limit()
    {
        var limiter = new BandwidthLimiter(new ManualClock()) { BytesPerSecond = MB };
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.Reserve(MB));
        Assert.Equal(TimeSpan.FromSeconds(2), limiter.Reserve(MB));
        Assert.Equal(TimeSpan.FromSeconds(3), limiter.Reserve(MB));
    }

    [Fact]
    public void An_idle_while_saves_up_at_most_a_second()
    {
        var clock = new ManualClock();
        var limiter = new BandwidthLimiter(clock) { BytesPerSecond = MB };
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.Zero, limiter.Reserve(MB));
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.Reserve(MB));
    }

    [Fact]
    public void A_lower_limit_does_not_let_what_was_saved_up_through()
    {
        var clock = new ManualClock();
        var limiter = new BandwidthLimiter(clock) { BytesPerSecond = 10L * MB };
        clock.Advance(TimeSpan.FromSeconds(1)); // 10 MB saved up

        limiter.BytesPerSecond = MB;
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(MB));
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.Reserve(MB));
    }

    [Fact]
    public void Removing_the_limit_takes_effect_right_away()
    {
        var limiter = new BandwidthLimiter(new ManualClock()) { BytesPerSecond = MB };
        limiter.Reserve(10 * MB);

        limiter.BytesPerSecond = 0;
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(MB));
    }

    [Fact]
    public void A_negative_limit_is_no_limit()
    {
        var limiter = new BandwidthLimiter(new ManualClock()) { BytesPerSecond = -5 };
        Assert.Equal(0, limiter.BytesPerSecond);
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(MB));
    }
}
