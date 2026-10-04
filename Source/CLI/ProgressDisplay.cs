using System.Diagnostics;
using Spectre.Console;
using UnVault.Core.Install;
using UnVault.Core.Util;

namespace UnVault.CLI;

/// <summary>Live progress bars for an install/verify task, polling its <see cref="InstallStatus"/>.</summary>
internal static class ProgressDisplay
{
    public static async Task RunInstallAsync(InstallStatus status, Func<Task> work)
    {
        var timer = Stopwatch.StartNew();
        await NewProgress().StartAsync(async context =>
        {
            var download = context.AddTask("Download", autoStart: true, maxValue: 1);
            var write = context.AddTask("Write", autoStart: true, maxValue: 1);
            await PollAsync(work(), () =>
            {
                download.MaxValue = Math.Max(1, status.DownloadTotal);
                download.Value = status.DownloadedBytes;
                write.MaxValue = Math.Max(1, status.WriteTotal);
                write.Value = status.WrittenBytes;
                write.Description = $"Write [grey]{status.FilesDone:N0}/{status.FilesTotal:N0} files[/]";
            });
        });

        long downloaded = status.DownloadedBytes;
        double seconds = Math.Max(timer.Elapsed.TotalSeconds, 0.001);
        AnsiConsole.MarkupLine(
            $"[green]Done[/] in {timer.Elapsed:hh\\:mm\\:ss}: {ByteSize.Format(downloaded)} downloaded " +
            $"({ByteSize.Format((long)(downloaded / seconds))}/s), {ByteSize.Format(status.WrittenBytes)} written" +
            (status.Retries > 0 ? $", {status.Retries} retried chunk downloads" : ""));
    }

    public static async Task RunVerifyAsync(InstallStatus status, Func<Task> work)
    {
        await NewProgress().StartAsync(async context =>
        {
            var check = context.AddTask("Verify", autoStart: true, maxValue: 1);
            await PollAsync(work(), () =>
            {
                check.MaxValue = Math.Max(1, status.WriteTotal);
                check.Value = status.WrittenBytes;
                check.Description = $"Verify [grey]{status.FilesDone:N0}/{status.FilesTotal:N0} files[/]";
            });
        });
    }

    private static Progress NewProgress() =>
        AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn(),
                new TransferSpeedColumn(),
                new RemainingTimeColumn());

    private static async Task PollAsync(Task work, Action update)
    {
        while (!work.IsCompleted)
        {
            update();
            await Task.WhenAny(work, Task.Delay(250));
        }
        update();
        await work; // surface exceptions
    }
}
