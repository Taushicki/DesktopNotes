using DesktopNotes.Application;
using DesktopNotes.Infrastructure;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void PreferencesRoundTripAndRecoverFromBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DesktopNotes-settings-" + Guid.NewGuid());
        try
        {
            var store = new JsonAppSettingsStore(directory);
            Assert.Equal(new AppSettings(), store.Load());
            var preferences = new AppSettings { Language = AppLanguage.English, LoggingEnabled = false, MinimumLogLevel = AppLogLevel.Warning };
            store.Save(preferences); Assert.Equal(preferences, new JsonAppSettingsStore(directory).Load());
            store.Save(preferences with { LoggingEnabled = true });
            File.WriteAllText(Path.Combine(directory, "settings.json"), "broken");
            Assert.Equal(preferences, store.Load());
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void FailedPreferenceWriteDoesNotApplyChanges()
    {
        var service = new AppSettingsService(new FailingStore());
        var changes = 0; service.Changed += _ => changes++;
        Assert.Throws<IOException>(() => service.Update(new AppSettings { LoggingEnabled = false }));
        Assert.True(service.Current.LoggingEnabled); Assert.Equal(0, changes);
    }

    [Fact]
    public void LoggerCanBeDisabledAndFiltersByMinimumLevelImmediately()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DesktopNotes-filter-" + Guid.NewGuid());
        try
        {
            var logger = new FileAppLogger(directory);
            logger.Configure(false, AppLogLevel.Information);
            logger.Write(AppLogLevel.Error, "Disabled", "not written");
            Assert.False(Directory.Exists(directory));
            logger.Configure(true, AppLogLevel.Warning);
            logger.Write(AppLogLevel.Information, "Filtered", "not written");
            logger.Write(AppLogLevel.Warning, "Warning", "written");
            logger.Write(AppLogLevel.Error, "Error", "written");
            var text = File.ReadAllText(logger.FilePath);
            Assert.DoesNotContain("Filtered", text); Assert.Contains("Warning", text); Assert.Contains("Error", text);
            logger.Configure(false, AppLogLevel.Error);
            logger.Write(AppLogLevel.Error, "DisabledAgain", "not written");
            Assert.Equal(text, File.ReadAllText(logger.FilePath));
            logger.Configure(true, AppLogLevel.Information);
            logger.Write(AppLogLevel.Information, "EnabledAgain", "written");
            Assert.Contains("EnabledAgain", File.ReadAllText(logger.FilePath));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class FailingStore : IAppSettingsStore
    {
        public AppSettings Load() => new();
        public void Save(AppSettings settings) => throw new IOException("blocked");
    }
}
