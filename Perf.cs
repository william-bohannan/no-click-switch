using System.Diagnostics;
using System.IO;
using System.Text;

namespace NoClickSwitch;

/// <summary>
/// Lightweight timing for hot paths. Writes slow samples to
/// %LocalAppData%\NoClickSwitch\perf.log (throttled).
/// </summary>
internal static class Perf
{
    private static readonly object Gate = new();
    private static string? _logPath;
    private static DateTime _lastFlushUtc = DateTime.MinValue;
    private static readonly Dictionary<string, (long totalMs, int count, long maxMs)> Stats = new(StringComparer.Ordinal);

    /// <summary>Measure <paramref name="action"/>; log if slower than <paramref name="warnMs"/>.</summary>
    public static void Time(string name, Action action, int warnMs = 8)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            sw.Stop();
            Record(name, sw.ElapsedMilliseconds, warnMs);
        }
    }

    public static T Time<T>(string name, Func<T> func, int warnMs = 8)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return func();
        }
        finally
        {
            sw.Stop();
            Record(name, sw.ElapsedMilliseconds, warnMs);
        }
    }

    public static void Record(string name, long elapsedMs, int warnMs = 8)
    {
        lock (Gate)
        {
            if (Stats.TryGetValue(name, out var s))
                Stats[name] = (s.totalMs + elapsedMs, s.count + 1, Math.Max(s.maxMs, elapsedMs));
            else
                Stats[name] = (elapsedMs, 1, elapsedMs);

            if (elapsedMs >= warnMs)
                AppendLine($"SLOW {name}={elapsedMs}ms");

            // Periodic summary every ~15s
            if ((DateTime.UtcNow - _lastFlushUtc).TotalSeconds >= 15)
            {
                _lastFlushUtc = DateTime.UtcNow;
                var sb = new StringBuilder();
                sb.AppendLine($"--- summary {DateTime.Now:HH:mm:ss} ---");
                foreach (var kv in Stats.OrderByDescending(k => k.Value.totalMs))
                {
                    var avg = kv.Value.count > 0 ? (double)kv.Value.totalMs / kv.Value.count : 0;
                    sb.AppendLine(
                        $"  {kv.Key}: n={kv.Value.count} total={kv.Value.totalMs}ms avg={avg:0.0}ms max={kv.Value.maxMs}ms");
                }

                AppendLine(sb.ToString());
                Stats.Clear();
            }
        }
    }

    private static void AppendLine(string line)
    {
        try
        {
            _logPath ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInstaller.AppName,
                "perf.log");
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 128 * 1024)
                File.WriteAllText(_logPath, "");

            File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
        }
        catch
        {
            // never break the app for perf logging
        }
    }
}
