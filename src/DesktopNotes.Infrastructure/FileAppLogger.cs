using System.Globalization;
using System.Text;
using DesktopNotes.Application;

namespace DesktopNotes.Infrastructure;

/// <summary>Immediate, serialized local writes; logging failures never break the application.</summary>
public sealed class FileAppLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly int _retainedFiles;
    private readonly string _directory;
    public string FilePath { get; }
    public string? LastWriteError { get; private set; }

    public FileAppLogger(string directory, long maxBytes = 2 * 1024 * 1024, int retainedFiles = 10)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedFiles);
        _directory = Path.GetFullPath(directory); _maxBytes = maxBytes; _retainedFiles = retainedFiles;
        FilePath = Path.Combine(_directory, $"DesktopNotes-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
    }

    public void Write(AppLogLevel level, string eventName, string message, Exception? exception = null)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var entry = $"{DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)} [{level}] pid={Environment.ProcessId} thread={Environment.CurrentManagedThreadId} {SingleLine(eventName)}: {SingleLine(message)}\n";
                if (exception is not null) entry += exception + "\n";
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length + Encoding.UTF8.GetByteCount(entry) > _maxBytes)
                    File.Move(FilePath, Path.ChangeExtension(FilePath, $"{Guid.NewGuid():N}.log"));
                File.AppendAllText(FilePath, entry, new UTF8Encoding(false));
                var files = Directory.EnumerateFiles(_directory, "DesktopNotes-*.log").Select(path => new FileInfo(path))
                    .Where(file => !string.Equals(file.FullName, FilePath, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).ToArray();
                foreach (var file in files.Skip(_retainedFiles - 1)) file.Delete();
                LastWriteError = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { LastWriteError = error.Message; System.Diagnostics.Debug.WriteLine("DesktopNotes logging failed: " + error.Message); }
        }
    }

    private static string SingleLine(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n");
}
