using BepInEx.Logging;

namespace Il2cppConsoleMod.Web;

/// <summary>
/// In-memory ring of recent BepInEx log events (all sources, Unity's included), so the web
/// UI can tail structured entries instead of re-parsing LogOutput.log.
/// </summary>
public sealed class LogBuffer : ILogListener
{
    private const int Capacity = 5000;

    private readonly object _sync = new();
    private readonly Entry[] _ring = new Entry[Capacity];
    private long _next;

    public sealed record Entry(long Seq, long Time, string Level, string Source, string Text);

    public LogLevel LogLevelFilter => LogLevel.All;

    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        var text = eventArgs.Data?.ToString() ?? "";
        var source = eventArgs.Source?.SourceName ?? "?";
        lock (_sync)
        {
            _ring[_next % Capacity] = new Entry(_next, DateTimeOffset.Now.ToUnixTimeMilliseconds(), LevelName(eventArgs.Level), source, text);
            _next++;
        }
    }

    /// <summary>Entries with sequence number greater than <paramref name="after"/> (-1 for the whole buffer).</summary>
    public (List<Entry> entries, long next, bool dropped) Since(long after, int max)
    {
        lock (_sync)
        {
            var first = Math.Max(0, _next - Capacity);
            var from = Math.Max(after + 1, first);
            var dropped = after >= 0 && after + 1 < first;
            var list = new List<Entry>((int)Math.Min(max, Math.Max(0, _next - from)));
            for (var seq = from; seq < _next && list.Count < max; seq++)
                list.Add(_ring[seq % Capacity]);
            return (list, _next, dropped);
        }
    }

    public void Dispose()
    {
    }

    private static string LevelName(LogLevel level)
    {
        if ((level & LogLevel.Fatal) != 0) return "fatal";
        if ((level & LogLevel.Error) != 0) return "error";
        if ((level & LogLevel.Warning) != 0) return "warn";
        if ((level & LogLevel.Message) != 0) return "message";
        if ((level & LogLevel.Info) != 0) return "info";
        return "debug";
    }
}
