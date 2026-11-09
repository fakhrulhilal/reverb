namespace Reverb.Server;

/// <summary>
/// Calls back when a file may have changed. Editors save in different ways (in place, via a temp
/// file and rename, delete and recreate), and watcher events can be dropped, so a cheap poll of
/// the file's size and timestamp backs up the watcher. Callers compare content to decide whether
/// anything really changed.
/// </summary>
sealed class FileWatcher : IDisposable
{
    readonly string path;
    readonly Action changed;
    readonly FileSystemWatcher? watcher;
    readonly Timer debounce;
    readonly Timer poll;
    readonly Lock gate = new();
    (DateTime, long) seen;

    public FileWatcher(string path, Action changed)
    {
        this.path = path;
        this.changed = changed;
        seen = Stamp();
        debounce = new Timer(_ => Fire());
        poll = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        try
        {
            watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            watcher.Changed += (_, _) => Soon();
            watcher.Created += (_, _) => Soon();
            watcher.Renamed += (_, e) => { if (string.Equals(e.FullPath, path, StringComparison.OrdinalIgnoreCase)) Soon(); };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
        {
            watcher = null; // polling still works
        }
    }

    /// <summary>The file was just written by us; don't report it.</summary>
    public void Saw()
    {
        lock (gate) seen = Stamp();
    }

    void Soon() => debounce.Change(TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);

    void Poll()
    {
        var now = Stamp();
        lock (gate)
        {
            if (now == seen) return;
        }
        Fire();
    }

    void Fire()
    {
        lock (gate) seen = Stamp();
        changed();
    }

    (DateTime, long) Stamp()
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1);
        }
        catch (IOException)
        {
            return (DateTime.MinValue, -1);
        }
    }

    public void Dispose()
    {
        watcher?.Dispose();
        debounce.Dispose();
        poll.Dispose();
    }
}
