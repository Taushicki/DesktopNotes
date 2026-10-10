namespace DesktopNotes.Application;

public enum AppLanguage { Russian, English }

public sealed record AppSettings
{
    public bool LoggingEnabled { get; init; } = true;
    public AppLogLevel MinimumLogLevel { get; init; } = AppLogLevel.Information;
    public AppLanguage Language { get; init; } = AppLanguage.Russian;
}

public interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public interface IConfigurableAppLogger : IAppLogger
{
    void Configure(bool enabled, AppLogLevel minimumLevel);
}

public sealed class AppSettingsService(IAppSettingsStore store)
{
    public AppSettings Current { get; private set; } = new();
    public event Action<AppSettings>? Changed;
    public void Load() => Current = store.Load();
    public void Update(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Language) || !Enum.IsDefined(settings.MinimumLogLevel))
            throw new ArgumentOutOfRangeException(nameof(settings));
        if (settings == Current) return;
        // Commit preferences before applying them. Failed writes keep the active settings.
        store.Save(settings);
        Current = settings;
        Changed?.Invoke(settings);
    }
}
