using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using DesktopNotes.Application;

namespace DesktopNotes.Presentation;

internal sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettingsService _settings;
    private readonly IDesktopMenuRegistration _registration;
    private readonly IAppLogger _logger;
    public string DataPath { get; }
    public string Error { get; private set; } = "";
    public bool LoggingEnabled { get => _settings.Current.LoggingEnabled; set => Save(_settings.Current with { LoggingEnabled = value }); }
    public AppLogLevel MinimumLogLevel { get => _settings.Current.MinimumLogLevel; set => Save(_settings.Current with { MinimumLogLevel = value }); }
    public AppLanguage Language { get => _settings.Current.Language; set => Save(_settings.Current with { Language = value }); }
    public string RegistrationStatus => UiText.Get(_registration.IsRegistered ? "Пункт меню установлен." : "Пункт меню не установлен.");
    public ICommand InstallMenu { get; }
    public ICommand RemoveMenu { get; }
    public ICommand OpenNotesFolder { get; }
    public ICommand OpenLogsFolder { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal SettingsViewModel(AppSettingsService settings, IDesktopMenuRegistration registration, string dataPath, IAppLogger logger)
    {
        _settings = settings; _registration = registration; DataPath = dataPath; _logger = logger;
        InstallMenu = new RelayCommand(() => Run(_registration.Register, "Не удалось изменить меню: "));
        RemoveMenu = new RelayCommand(() => Run(_registration.Unregister, "Не удалось изменить меню: "));
        OpenNotesFolder = new RelayCommand(() => OpenFolder(DataPath));
        OpenLogsFolder = new RelayCommand(() => OpenFolder(Path.Combine(DataPath, "logs")));
        settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(AppSettings settings) => Notify();
    private void Save(AppSettings settings)
    {
        var previousLanguage = _settings.Current.Language;
        Run(() => _settings.Update(settings), "Не удалось сохранить настройки: ");
        if (_settings.Current.Language != previousLanguage)
            Run(() => { if (_registration.IsRegistered) _registration.Register(); }, "Не удалось изменить меню: ");
    }
    private void OpenFolder(string path) => Run(() =>
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path } });
    }, "Не удалось открыть папку: ");

    private void Run(Action action, string errorPrefix)
    {
        try { action(); Error = ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            Error = UiText.Get(errorPrefix) + error.Message;
            _logger.Write(AppLogLevel.Error, "Settings.UpdateFailed", errorPrefix, error);
        }
        Notify();
    }
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
