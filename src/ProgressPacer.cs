using System.Windows.Threading;

namespace DrangedAddons;

/// <summary>
/// What a download button shows, paced so it reads well: the bar moves quickly (empty to full in FullFillSeconds at
/// most) but never ahead of the real download, so a slow download is followed exactly and an instant one still
/// visibly fills. Call <see cref="WhenFullAsync"/> to let the fill finish before moving on.
/// </summary>
public sealed class ProgressPacer
{
    const double FullFillSeconds = 0.6;

    readonly Action<double, DownloadProgress?> _show;
    readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    readonly TaskCompletionSource _full = new();
    DateTime _lastTick = DateTime.UtcNow;
    double _actual;
    double _shown;
    DownloadProgress? _last;

    /// <param name="show">Called with the fraction to display (0–1) and the latest real progress.</param>
    public ProgressPacer(Action<double, DownloadProgress?> show)
    {
        _show = show;
        _timer.Tick += (_, _) => Tick();
        _show(0, null);
    }

    /// <summary>The real download progress (call from the UI thread, e.g. via Progress&lt;T&gt;).</summary>
    public void Report(DownloadProgress progress)
    {
        if (_stopped) return;
        _last = progress;
        _actual = Math.Max(_actual, progress.Fraction);
        if (!_timer.IsEnabled)
        {
            _lastTick = DateTime.UtcNow;
            _timer.Start();
        }
    }

    /// <summary>Completes once the bar has visibly reached full (after the real download finished).</summary>
    public Task WhenFullAsync() => _shown >= 1 ? Task.CompletedTask : _full.Task;

    /// <summary>The download finished: let the bar run on to full (at its quick pace).</summary>
    public void Complete()
    {
        if (_stopped) return;
        _actual = 1;
        if (!_timer.IsEnabled)
        {
            _lastTick = DateTime.UtcNow;
            _timer.Start();
        }
    }

    bool _stopped;

    /// <summary>The download failed: stop updating the button.</summary>
    public void Stop()
    {
        _stopped = true;
        _timer.Stop();
        _full.TrySetResult();
    }

    void Tick()
    {
        if (_stopped) return;
        var now = DateTime.UtcNow;
        var step = (now - _lastTick).TotalSeconds / FullFillSeconds;
        _lastTick = now;
        _shown = Math.Min(_actual, _shown + step);
        _show(_shown, _last);
        if (_shown >= 1)
        {
            _timer.Stop();
            _full.TrySetResult();
        }
        else if (_shown >= _actual)
        {
            _timer.Stop(); // caught up: wait for the next real report
        }
    }
}
