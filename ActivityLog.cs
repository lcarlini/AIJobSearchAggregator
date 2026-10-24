namespace AIJobSearchAggregator;

public sealed record LogEntry(string Time, string Level, string Message);

public sealed class ActivityLog
{
    private readonly Lock _gate = new();
    private readonly List<LogEntry> _entries = [];

    public void Info(string message) => Write("info", message);

    public void Warn(string message) => Write("warn", message);

    public void Error(string message) => Write("error", message);

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
            return _entries.ToArray();
    }

    private void Write(string level, string message)
    {
        var entry = new LogEntry(DateTime.Now.ToString("HH:mm:ss"), level, message);
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > 300)
                _entries.RemoveAt(0);
        }

        var line = $"[{entry.Time}] {message}";
        if (level == "error")
            Console.Error.WriteLine(line);
        else
            Console.WriteLine(line);
    }
}
