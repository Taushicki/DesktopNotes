using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DesktopNotes.Application;
using DesktopNotes.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DesktopNotes.Presentation;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        IAppLogger? logger = null;
        UnhandledExceptionEventHandler? unhandled = null;
        EventHandler<UnobservedTaskExceptionEventArgs>? unobserved = null;
        try
        {
            var dataPath = Path.GetFullPath(Environment.GetEnvironmentVariable("DESKTOPNOTES_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopNotes"));
            var preferences = new AppSettingsService(new JsonAppSettingsStore(dataPath));
            var fileLogger = new FileAppLogger(Path.Combine(dataPath, "logs"));
            logger = fileLogger;
            try { preferences.Load(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
            { logger.Write(AppLogLevel.Warning, "Settings.LoadFailed", "Using default preferences", error); }
            fileLogger.Configure(preferences.Current.LoggingEnabled, preferences.Current.MinimumLogLevel);
            UiText.SetLanguage(preferences.Current.Language);
            preferences.Changed += settings =>
            {
                fileLogger.Configure(settings.LoggingEnabled, settings.MinimumLogLevel);
                UiText.SetLanguage(settings.Language);
            };
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            logger.Write(AppLogLevel.Information, "Process.Start", $"version={version}; runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; OS={Environment.OSVersion}; executable={Environment.ProcessPath}");
            unhandled = (_, e) => logger.Write(AppLogLevel.Error, "Process.UnhandledException", $"terminating={e.IsTerminating}", e.ExceptionObject as Exception);
            unobserved = (_, e) => { logger.Write(AppLogLevel.Error, "Task.UnobservedException", "Background task failed", e.Exception); e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += unhandled;
            TaskScheduler.UnobservedTaskException += unobserved;
            Environment.ExitCode = new NotesApp(args, dataPath, logger, preferences).Run();
        }
        catch (Exception error)
        {
            logger?.Write(AppLogLevel.Error, "Process.Fatal", "Application could not run", error);
            System.Windows.MessageBox.Show(UiText.Get("DesktopNotes завершилось с ошибкой.\n\n") + error.Message + UiText.Get("\n\nЖурнал: ") + (logger as FileAppLogger)?.FilePath, "DesktopNotes", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.ExitCode = 1;
        }
        finally
        {
            logger?.Write(AppLogLevel.Information, "Process.Exit", $"exitCode={Environment.ExitCode}");
            if (unhandled is not null) AppDomain.CurrentDomain.UnhandledException -= unhandled;
            if (unobserved is not null) TaskScheduler.UnobservedTaskException -= unobserved;
        }
    }
}

public sealed class NotesApp(string[] arguments, string dataPath, IAppLogger logger, AppSettingsService preferences) : System.Windows.Application
{
    static NotesApp() => EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent,
        new ToolTipEventHandler((_, e) => e.Handled = true));
    private readonly Dictionary<Guid, NoteWindow> _windows = [];
    private readonly string _dataPath = dataPath;
    private ServiceProvider? _services;
    private SingleInstance? _instance;
    private BoardInteractionController? _board;
    private NoteService _notes = null!;
    private AutosaveCoordinator _save = null!;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private (bool Dark, int Size)? _trayIconKey;
    private TrayMenu? _trayMenu;
    private Window? _settings;
    private bool _exiting;
    private bool _saveError;
    private bool _handlingFatalError;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var isolatedDirectory = Environment.GetEnvironmentVariable("DESKTOPNOTES_DATA_DIR");
            var isolatedName = isolatedDirectory is null ? null : "DesktopNotes-dev-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_dataPath.ToUpperInvariant())))[..16];
            _instance = new SingleInstance(isolatedName);
            if (!_instance.IsPrimary)
            {
                logger.Write(AppLogLevel.Information, "Instance.Forward", arguments.Contains("--new-note") ? "new-note" : "show");
                await _instance.SendAsync(arguments.Contains("--new-note") ? "--new-note" : "--show");
                Shutdown(); return;
            }
            var collection = new ServiceCollection();
            collection.AddSingleton<IAppLogger>(logger);
            collection.AddSingleton<INoteRepository>(new JsonNoteRepository(_dataPath));
            collection.AddSingleton<NoteService>();
            collection.AddSingleton<AutosaveCoordinator>();
            collection.AddSingleton<IWindowLayerController, WindowLayerController>();
            collection.AddSingleton<IDesktopMenuRegistration>(new DesktopMenuRegistration(Environment.ProcessPath!, () => UiText.Get("Создать заметку")));
            collection.AddSingleton<GroupSelectionSession>();
            collection.AddSingleton<DesktopClickObserver>();
            _services = collection.BuildServiceProvider();
            _notes = _services.GetRequiredService<NoteService>();
            var warning = await _notes.LoadAsync();
            logger.Write(AppLogLevel.Information, "Storage.Loaded", $"notes={_notes.Notes.Count}; groups={_notes.Snapshot.Groups.Count}; links={_notes.Snapshot.Links.Count}");
            _save = _services.GetRequiredService<AutosaveCoordinator>();
            _board = new BoardInteractionController(_notes, _services.GetRequiredService<GroupSelectionSession>(),
                _services.GetRequiredService<DesktopClickObserver>(), GetWindow, Dispatcher);
            _save.SaveFailed += OnSaveFailed;
            _save.Saved += () => { _saveError = false; logger.Write(AppLogLevel.Information, "Storage.Saved", $"notes={_notes.Notes.Count}"); };
            CreateTray();
            foreach (var note in _notes.Notes.Where(n => !n.IsHidden)) GetWindow(note.Id).Show();
            _instance.StartListening(command => Dispatcher.InvokeAsync(() =>
            {
                if (_exiting) return;
                if (command == "--new-note") CreateNote(); else ShowNotes();
            }).Task);
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            if (warning is not null) { logger.Write(AppLogLevel.Warning, "Storage.Recovery", warning); System.Windows.MessageBox.Show(UiText.Get(warning), UiText.Get("DesktopNotes — восстановление")); }
            if (arguments.Contains("--new-note")) CreateNote();
            else if (_notes.Notes.Count == 0) { CreateNote(); OpenSettings(); }
            logger.Write(AppLogLevel.Information, "Startup.Ready", $"visibleWindows={_windows.Count}");
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Error, "Startup.Failed", "Application initialization failed", ex);
            System.Windows.MessageBox.Show(UiText.Get("Не удалось запустить DesktopNotes.\n\n") + ex.Message + UiText.Get("\n\nЖурнал: ") + Path.Combine(_dataPath, "logs"), "DesktopNotes", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private NoteWindow GetWindow(Guid id)
    {
        if (_windows.TryGetValue(id, out var existing)) return existing;
        var vm = new NoteViewModel(_notes.Get(id), _notes,
            () => _windows[id].HideNote(),
            () => DeleteNote(id));
        var services = _services!;
        var window = new NoteWindow(vm, services.GetRequiredService<IWindowLayerController>());
        window.NewNoteRequested = CreateNote;
        window.SaveRequested = () =>
        {
            logger.Write(AppLogLevel.Information, "Storage.SaveRequested", "Keyboard shortcut Ctrl+S");
            _ = _save.FlushAsync();
        };
        logger.Write(AppLogLevel.Information, "Window.Created", $"noteId={id}");
        _windows.Add(id, window);
        _board?.Attach(window);
        return window;
    }

    private void CreateNote()
    {
        if (_exiting) return;
        var offset = (_notes.Notes.Count % 8) * 24;
        var note = _notes.Create(80 + offset, 80 + offset);
        if (preferences.Current.Language == AppLanguage.English)
            _notes.Update(note with { Markdown = "# " + UiText.Get("Новая заметка") + "\n\n" });
        GetWindow(note.Id).Show();
    }

    private void DeleteNote(Guid id)
    {
        if (NoteDeletionPolicy.RequiresConfirmation(_notes.Get(id).Markdown) &&
            new DeleteConfirmationWindow(NoteThemes.Get(_notes.Get(id).Color)) { Owner = _windows[id] }.ShowDialog() != true) return;
        _notes.Delete(id);
        _board?.Detach(_windows[id]);
        _windows[id].ClosePermanently();
        _windows.Remove(id);
    }

    private void ShowNotes()
    {
        if (_exiting) return;
        if (_notes.Notes.Count == 0) { CreateNote(); return; }
        foreach (var note in _notes.Notes) GetWindow(note.Id).Reveal();
    }

    private void CreateTray()
    {
        _trayMenu = new TrayMenu(CreateNote, ShowNotes, OpenSettings, () => _save.FlushAsync(), ExitAsync);
        _tray = new Forms.NotifyIcon();
        UpdateTrayIcon();
        _tray.Visible = true;
        SystemEvents.UserPreferenceChanged += OnTrayPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnTrayDisplayChanged;
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right && !_exiting)
                _trayMenu.Open(WindowsAppTheme.IsDark, _saveError);
        };
        _tray.DoubleClick += (_, _) => CreateNote();
    }

    private void OnTrayPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueueTrayIconUpdate();
    private void OnTrayDisplayChanged(object? sender, EventArgs e) => QueueTrayIconUpdate();
    private void QueueTrayIconUpdate()
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!_exiting && _tray is not null) UpdateTrayIcon(); }));
    }
    private void UpdateTrayIcon()
    {
        var key = (WindowsAppTheme.IsTaskbarDark, Forms.SystemInformation.SmallIconSize.Width);
        if (_tray is null || _trayIconKey == key) return;
        var next = AppIcons.LoadTray(key.Item1, key.Item2);
        _tray.Icon = next;
        var previous = _trayIcon; _trayIcon = next; _trayIconKey = key; previous?.Dispose();
    }

    private void OnSaveFailed(Exception error)
    {
        logger.Write(AppLogLevel.Error, "Storage.SaveFailed", "Changes remain in memory", error);
        if (_tray is null) return;
        if (!_saveError)
        {
            _tray.BalloonTipTitle = UiText.Get("Не удалось сохранить заметки");
            _tray.BalloonTipText = UiText.Get("Изменения остаются в памяти. В меню трея доступно повторное сохранение. ") + error.Message;
            _tray.ShowBalloonTip(5000);
        }
        _saveError = true;
    }

    private void OpenSettings()
    {
        if (_settings is not null) { _settings.Activate(); return; }
        var viewModel = new SettingsViewModel(preferences, _services!.GetRequiredService<IDesktopMenuRegistration>(), _dataPath, logger);
        _settings = new SettingsWindow(viewModel);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        foreach (var window in _windows.Values.Where(w => w.IsVisible)) { window.EnsureVisibleOnScreen(); window.SaveBounds(); }
    });

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        logger.Write(AppLogLevel.Information, "Shutdown.Requested", "Flushing pending changes");
        _board?.CommitSelection();
        foreach (var window in _windows.Values) window.SaveBounds();
        foreach (var window in _windows.Values) window.IsEnabled = false;
        _trayMenu?.Close();
        if (!await _save.FlushAsync())
        {
            System.Windows.MessageBox.Show(UiText.Get("Выход отменён: изменения не сохранены. Освободите место или восстановите доступ к папке, затем повторите выход."), "DesktopNotes", MessageBoxButton.OK, MessageBoxImage.Warning);
            foreach (var window in _windows.Values) window.IsEnabled = true;
            _exiting = false; return;
        }
        foreach (var window in _windows.Values) window.ClosePermanently();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel || _save is null) return;
        logger.Write(AppLogLevel.Information, "Shutdown.SessionEnding", e.ReasonSessionEnding.ToString());
        _board?.CommitSelection();
        foreach (var window in _windows.Values) window.SaveBounds();
        // FlushAsync captures the snapshot now, then completes entirely without the
        // dispatcher. Notifications are posted, so this synchronous OS callback
        // cannot deadlock with a previously running autosave.
        if (!_save.FlushAsync().GetAwaiter().GetResult())
        {
            e.Cancel = true;
            System.Windows.MessageBox.Show(UiText.Get("Завершение сеанса отменено: заметки не удалось сохранить. Повторите сохранение через меню трея."), "DesktopNotes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        logger.Write(AppLogLevel.Information, "Shutdown.Exit", $"exitCode={e.ApplicationExitCode}");
        DispatcherUnhandledException -= OnDispatcherException;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _board?.Dispose();
        _trayMenu?.Close();
        SystemEvents.UserPreferenceChanged -= OnTrayPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnTrayDisplayChanged;
        _tray?.Dispose(); _trayIcon?.Dispose();
        _instance?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        logger.Write(AppLogLevel.Error, "UI.UnhandledException", "UI operation failed", e.Exception);
        if (_handlingFatalError) return;
        if (e.Exception is InvalidOperationException && e.Exception.Message.Contains("VisualLineElementGenerator", StringComparison.Ordinal) && _windows.Count > 0)
        {
            try
            {
                foreach (var window in _windows.Values) window.MarkdownEditor.DisablePreview();
                e.Handled = true;
                logger.Write(AppLogLevel.Warning, "Editor.Recovery", "Live Preview disabled for this session; Markdown preserved");
                Dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show(UiText.Get("Ошибка отображения Markdown. Заметки доступны в исходном виде; текст сохранён без преобразования.\n\nЖурнал: ") + Path.Combine(_dataPath, "logs"), "DesktopNotes", MessageBoxButton.OK, MessageBoxImage.Warning));
                return;
            }
            catch (Exception recoveryError) { logger.Write(AppLogLevel.Error, "Editor.RecoveryFailed", "Could not restore source editor", recoveryError); }
        }
        _handlingFatalError = true;
        e.Handled = true;
        System.Windows.MessageBox.Show(UiText.Get("DesktopNotes завершится из-за ошибки.\n\n") + e.Exception.Message + UiText.Get("\n\nЖурнал: ") + Path.Combine(_dataPath, "logs"), "DesktopNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }
}
