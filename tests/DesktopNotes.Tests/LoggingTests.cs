using DesktopNotes.Application;
using DesktopNotes.Infrastructure;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class LoggingTests
{
    [Fact]
    public async Task ConcurrentWritesAreCompleteAndExceptionsIncludeStackTrace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DesktopNotes-log-test-" + Guid.NewGuid());
        try
        {
            var logger = new FileAppLogger(directory);
            await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(() => logger.Write(AppLogLevel.Information, "Test.Write", $"entry={index}"))));
            try { ThrowTestError(); }
            catch (InvalidOperationException error) { logger.Write(AppLogLevel.Error, "Test.Error", "write failed", error); }
            var lines = File.ReadAllLines(logger.FilePath);
            Assert.Equal(40, lines.Count(line => line.Contains("Test.Write: entry=", StringComparison.Ordinal)));
            Assert.Contains(lines, line => line.Contains("ThrowTestError", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("InvalidOperationException: test error", StringComparison.Ordinal));
            Assert.Null(logger.LastWriteError);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RotationRetainsOnlyOwnLogsAndKeepsCurrentSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DesktopNotes-log-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "notes.json"), "untouched");
            var logger = new FileAppLogger(directory, maxBytes: 256, retainedFiles: 3);
            for (var index = 0; index < 15; index++) logger.Write(AppLogLevel.Information, "Test.Rotation", new string('a', 100) + index);
            Assert.Equal(3, Directory.GetFiles(directory, "DesktopNotes-*.log").Length);
            Assert.Contains("14", File.ReadAllText(logger.FilePath));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(directory, "notes.json")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnwritableLogDirectoryDoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), "DesktopNotes-log-blocker-" + Guid.NewGuid());
        File.WriteAllText(path, "occupied");
        try
        {
            var logger = new FileAppLogger(path);
            logger.Write(AppLogLevel.Error, "Test.Failure", "cannot write", new IOException("write failure"));
            Assert.NotNull(logger.LastWriteError);
            Assert.Equal("occupied", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    private static void ThrowTestError() => throw new InvalidOperationException("test error");
}
