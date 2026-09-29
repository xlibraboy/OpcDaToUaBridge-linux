using System.Text;
using Microsoft.Extensions.Logging;

namespace OpcBridge.App;

/// <summary>
/// Bounded, rolling log file beside the bridge's runtime state (<c>logs/bridge.log</c> under
/// <see cref="DataDirectory"/>).
///
/// The bridge had no durable log at all: <see cref="DashboardLogStore"/> keeps the last 500
/// entries in memory, and the console goes nowhere when the app runs as a Windows service. So a
/// service that died took its own evidence with it — on 2026-09-29 WORKSTAT02's bridge stopped
/// unexpectedly twice (Service Control Manager event 7034) and neither stop could be explained,
/// because the only record of what it was doing was in the process that vanished. This is that
/// record.
///
/// Every append opens, writes and closes the file (FileShare.ReadWrite), so the tail is on disk
/// the moment a line is written — a crash cannot lose the buffered part — and an operator can read
/// the file while the service runs. The volume is tiny (the bridge logs a handful of lines a
/// minute at Information), which is what makes the cheap-and-durable trade worth taking here.
/// Rotation is by size; the file is capped so a chatty failure mode cannot fill the disk of an
/// industrial PC.
///
/// Logging is best effort by design: a diagnostics path must never be the reason the bridge goes
/// down, so every failure is swallowed.
/// </summary>
internal sealed class FileLogStore
{
    /// <summary>Bytes per file before it rolls. Three files, so ~6 MB of history at worst.</summary>
    public const long MaxBytes = 2L * 1024 * 1024;

    /// <summary>Number of files kept (bridge.log plus bridge.1.log, bridge.2.log).</summary>
    public const int MaxFiles = 3;

    /// <summary>Longest single entry, so one enormous exception cannot rotate the file alone.</summary>
    private const int MaxEntryChars = 8 * 1024;

    private readonly object sync_ = new();
    private readonly string directory_;
    private readonly string path_;
    private long size_;

    public FileLogStore()
    {
        directory_ = Path.Combine(DataDirectory.Value, "logs");
        path_ = Path.Combine(directory_, "bridge.log");
        try
        {
            Directory.CreateDirectory(directory_);
            if (File.Exists(path_))
            {
                size_ = new FileInfo(path_).Length;
            }
        }
        catch
        {
            // Leave the store inert; Append will keep swallowing.
        }
    }

    /// <summary>The file written to, for the startup line that tells an operator where to look.</summary>
    public string FilePath => path_;

    public void Append(string line)
    {
        if (line.Length > MaxEntryChars)
        {
            line = string.Concat(line.AsSpan(0, MaxEntryChars), "… [truncated]");
        }

        lock (sync_)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                if (size_ + bytes.Length > MaxBytes)
                {
                    Roll();
                }

                using FileStream stream = new(path_, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes, 0, bytes.Length);
                size_ += bytes.Length;
            }
            catch
            {
            }
        }
    }

    private void Roll()
    {
        try
        {
            string oldest = Path.Combine(directory_, $"bridge.{MaxFiles - 1}.log");
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (int index = MaxFiles - 2; index >= 1; index--)
            {
                string from = Path.Combine(directory_, $"bridge.{index}.log");
                if (File.Exists(from))
                {
                    File.Move(from, Path.Combine(directory_, $"bridge.{index + 1}.log"), overwrite: true);
                }
            }

            if (File.Exists(path_))
            {
                File.Move(path_, Path.Combine(directory_, "bridge.1.log"), overwrite: true);
            }
        }
        catch
        {
        }

        size_ = 0;
    }
}

/// <summary>
/// Feeds <see cref="FileLogStore"/> from <c>ILogger</c>. The level rule mirrors
/// <see cref="DashboardLogProvider"/> — Information for the bridge's own categories, Warning for
/// everything else — so the file and the dashboard's Logs panel show the same events, and the
/// framework's per-request chatter cannot flood either.
/// </summary>
internal sealed class FileLogProvider : ILoggerProvider
{
    private readonly FileLogStore store_;

    public FileLogProvider(FileLogStore store)
    {
        store_ = store;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(store_, categoryName);
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLogStore store_;
        private readonly string category_name_;

        public FileLogger(FileLogStore store, string categoryName)
        {
            store_ = store;
            category_name_ = categoryName;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            if (logLevel == LogLevel.None)
            {
                return false;
            }

            if (category_name_.StartsWith("OpcBridge.", StringComparison.Ordinal))
            {
                return logLevel >= LogLevel.Information;
            }

            return logLevel >= LogLevel.Warning;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            StringBuilder builder = new();
            builder.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("Z [").Append(logLevel).Append("] ");
            if (!string.IsNullOrEmpty(category_name_))
            {
                builder.Append(category_name_).Append(": ");
            }

            builder.Append(message);
            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            store_.Append(builder.ToString());
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
