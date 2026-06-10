using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace FileSearch.Core.Logging;

/// <summary>
/// Minimal daily-file logger so errors that used to be silently swallowed
/// land somewhere inspectable. One file per day per process kind (the GUI
/// and CLI pass different prefixes so they never share a file); files older
/// than a week are removed. Writes are best-effort — logging must never
/// take the app down.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetainedDays = 7;

    private readonly string _directory;
    private readonly string _filePrefix;
    private readonly LogLevel _minimumLevel;
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private DateTime _writerDate;

    public FileLoggerProvider(string directory, string filePrefix, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _filePrefix = filePrefix;
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        lock (_sync)
        {
            try
            {
                var today = DateTime.UtcNow.Date;
                if (_writer is null || _writerDate != today)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(_directory);
                    _writerDate = today;
                    _writer = new StreamWriter(
                        new FileStream(
                            Path.Combine(_directory, $"{_filePrefix}-{today:yyyyMMdd}.log"),
                            FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        AutoFlush = true,
                    };
                    CleanupOldLogs(today);
                }

                var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [{level}] {category}: {message}";
                _writer.WriteLine(exception is null ? line : line + Environment.NewLine + exception);
            }
            catch
            {
            }
        }
    }

    private void CleanupOldLogs(DateTime today)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < today.AddDays(-RetainedDays))
                    File.Delete(file);
            }
        }
        catch
        {
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _provider._minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            _provider.Write(_category, logLevel, formatter(state, exception), exception);
        }
    }
}
