using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Follower;

internal sealed class SpikeProfiler
{
    private sealed class Metric
    {
        public long Count;
        public double TotalMs;
        public double MaxMs;
        public double LastMs;
        public DateTime LastSpikeUtc;
    }

    private readonly Follower _plugin;
    private readonly Dictionary<string, Metric> _metrics = new(StringComparer.Ordinal);
    private readonly StringBuilder _buffer = new(16 * 1024);
    private DateTime _nextFlushUtc = DateTime.UtcNow.AddSeconds(1);
    private string? _sessionFile;
    private bool _writeFailureReported;

    public SpikeProfiler(Follower plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
    }

    public long Begin(string scopeName)
    {
        return IsEnabled ? Stopwatch.GetTimestamp() : 0L;
    }

    public void End(string scopeName, long startTimestamp)
    {
        if (startTimestamp == 0L || !IsEnabled || string.IsNullOrWhiteSpace(scopeName))
            return;

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        if (!_metrics.TryGetValue(scopeName, out var metric))
        {
            metric = new Metric();
            _metrics.Add(scopeName, metric);
        }

        metric.Count++;
        metric.TotalMs += elapsedMs;
        metric.LastMs = elapsedMs;
        if (elapsedMs > metric.MaxMs)
            metric.MaxMs = elapsedMs;

        var thresholdMs = Math.Max(1, Safe(() => _plugin.Settings.Debug.SpikeProfilerThresholdMs.Value, 8));
        var logEverySample = Safe(() => _plugin.Settings.Debug.SpikeProfilerLogEverySample.Value, false);

        if (elapsedMs >= thresholdMs || logEverySample)
        {
            metric.LastSpikeUtc = DateTime.UtcNow;
            AppendSample(scopeName, elapsedMs, thresholdMs, logEverySample ? "sample" : "spike");
        }
    }

    public void FlushIfNeeded()
    {
        if (!IsEnabled)
            return;

        var now = DateTime.UtcNow;
        if (now < _nextFlushUtc)
            return;

        var intervalMs = Math.Max(250, Safe(() => _plugin.Settings.Debug.SpikeProfilerFlushIntervalMs.Value, 1000));
        _nextFlushUtc = now.AddMilliseconds(intervalMs);

        AppendSummary(now);
        FlushBuffer();
    }

    private bool IsEnabled => Safe(() => _plugin.Settings.Debug.EnableSpikeProfiler.Value, false);

    private void AppendSample(string scopeName, double elapsedMs, int thresholdMs, string kind)
    {
        _buffer
            .Append(DateTime.Now.ToString("O", CultureInfo.InvariantCulture))
            .Append(" | ").Append(kind)
            .Append(" | ").Append(scopeName)
            .Append(" | elapsed=").Append(elapsedMs.ToString("0.###", CultureInfo.InvariantCulture)).Append("ms")
            .Append(" | threshold=").Append(thresholdMs).Append("ms")
            .AppendLine();
    }

    private void AppendSummary(DateTime nowUtc)
    {
        if (_metrics.Count == 0)
            return;

        var top = _metrics
            .OrderByDescending(x => x.Value.MaxMs)
            .ThenByDescending(x => x.Value.TotalMs)
            .Take(25)
            .ToArray();

        _buffer
            .Append(DateTime.Now.ToString("O", CultureInfo.InvariantCulture))
            .AppendLine(" | summary | top plugin option costs by max elapsed");

        for (var i = 0; i < top.Length; i++)
        {
            var metric = top[i].Value;
            var avg = metric.Count == 0 ? 0.0 : metric.TotalMs / metric.Count;
            _buffer
                .Append("  #").Append(i + 1)
                .Append(" | ").Append(top[i].Key)
                .Append(" | count=").Append(metric.Count)
                .Append(" | last=").Append(metric.LastMs.ToString("0.###", CultureInfo.InvariantCulture)).Append("ms")
                .Append(" | avg=").Append(avg.ToString("0.###", CultureInfo.InvariantCulture)).Append("ms")
                .Append(" | max=").Append(metric.MaxMs.ToString("0.###", CultureInfo.InvariantCulture)).Append("ms")
                .AppendLine();
        }
    }

    private void FlushBuffer()
    {
        if (_buffer.Length == 0)
            return;

        try
        {
            var path = EnsureSessionFile();
            File.AppendAllText(path, _buffer.ToString(), Encoding.UTF8);
            _buffer.Clear();
        }
        catch (Exception ex)
        {
            _buffer.Clear();
            if (_writeFailureReported)
                return;

            _writeFailureReported = true;
            try { _plugin.LogMessage("SpikeProfiler write failed: " + ex.Message, 5); } catch { }
        }
    }

    private string EnsureSessionFile()
    {
        if (!string.IsNullOrEmpty(_sessionFile))
            return _sessionFile;

        var directory = Safe(() => _plugin.Settings.Debug.SpikeProfilerDirectory.Value, string.Empty);
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Path.GetTempPath(), "Follower-SpikeProfiler");

        Directory.CreateDirectory(directory);
        _sessionFile = Path.Combine(directory, "Follower-SpikeProfiler-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");

        File.AppendAllText(
            _sessionFile,
            "Follower Spike Profiler started " + DateTime.Now.ToString("O", CultureInfo.InvariantCulture) + Environment.NewLine +
            "Logs are written only for spikes by default. Enable 'Spike Profiler log every sample' for full per-frame samples." + Environment.NewLine,
            Encoding.UTF8);

        try { _plugin.LogMessage("SpikeProfiler log: " + _sessionFile, 5); } catch { }
        return _sessionFile;
    }

    private static T Safe<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }
}
