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
            logger = new FileAppLogger(Path.Combine(dataPath, "logs"));
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            logger.Write(AppLogLevel.Information, "Process.Start", $"version={version}; runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; OS={Environment.OSVersion}; executable={Environment.ProcessPath}");
            unhandled = (_, e) => logger.Write(AppLogLevel.Error, "Process.UnhandledException", $"terminating={e.IsTerminating}", e.ExceptionObject as Exception);
            unobserved = (_, e) => { logger.Write(AppLogLevel.Error, "Task.UnobservedException", "Background task failed", e.Exception); e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += unhandled;
            TaskScheduler.UnobservedTaskException += unobserved;
            Environment.ExitCode = new NotesApp(args, dataPath, logger).Run();
        }
        catch (Exception error)
        {
            logger?.Write(AppLogLevel.Error, "Process.Fatal", "Application could not run", error);
            System.Windows.MessageBox.Show("DesktopNotes завершилось с ошибкой.\n\n" + error.Message + "\n\nЖурнал: " + (logger as FileAppLogger)?.FilePath, "DesktopNotes", MessageBoxButton.OK, MessageBoxImage.Error);
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

public sealed class NotesApp(string[] arguments, string dataPath, IAppLogger logger) : System.Windows.Application
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
            collection.AddSingleton<IDesktopMenuRegistration>(new DesktopMenuRegistration(Environment.ProcessPath!));
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
            if (warning is not null) { logger.Write(AppLogLevel.Warning, "Storage.Recovery", warning); System.Windows.MessageBox.Show(warning, "DesktopNotes — восстановление"); }
            if (arguments.Contains("--new-note")) CreateNote();
            else if (_notes.Notes.Count == 0) { CreateNote(); OpenSettings(); }
            logger.Write(AppLogLevel.Information, "Startup.Ready", $"visibleWindows={_windows.Count}");
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Error, "Startup.Failed", "Application initialization failed", ex);
            System.Windows.MessageBox.Show("Не удалось запустить DesktopNotes.\n\n" + ex.Message + "\n\nЖурнал: " + Path.Combine(_dataPath, "logs"), "DesktopNotes", MessageBoxButton.OK, MessageBoxImage.Error);
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
        GetWindow(note.Id).Show();
    }

    private void DeleteNote(Guid id)
    {
        if (System.Windows.MessageBox.Show(_windows[id], "Удалить заметку без возможности восстановления?", "DesktopNotes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            foreach (Forms.ToolStripItem item in menu.Items.Cast<Forms.ToolStripItem>().ToArray()) item.Dispose();
            menu.Items.Clear();
            menu.Items.Add("Создать заметку", null, (_, _) => CreateNote());
            menu.Items.Add("Показать все", null, (_, _) => ShowNotes());
            menu.Items.Add("Настройки", null, (_, _) => OpenSettings());
            if (_saveError) menu.Items.Add("Повторить сохранение", null, async (_, _) => await _save.FlushAsync());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        };
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => CreateNote();
    }

    private void OnSaveFailed(Exception error)
    {
        logger.Write(AppLogLevel.Error, "Storage.SaveFailed", "Changes remain in memory", error);
        if (_tray is null) return;
        if (!_saveError)
        {
            _tray.BalloonTipTitle = "Не удалось сохранить заметки";
            _tray.BalloonTipText = "Изменения остаются в памяти. В меню трея доступно повторное сохранение. " + error.Message;
            _tray.ShowBalloonTip(5000);
        }
        _saveError = true;
    }

    private void OpenSettings()
    {
        if (_settings is not null) { _settings.Activate(); return; }
        var registration = _services!.GetRequiredService<IDesktopMenuRegistration>();
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock { Text = "DesktopNotes", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        content.Children.Add(new TextBlock { Text = "Создание заметки из меню рабочего стола.\nВ Windows 11 откройте «Показать дополнительные параметры».", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var status = new TextBlock { Margin = new Thickness(0, 0, 0, 12) };
        void Refresh() => status.Text = registration.IsRegistered ? "Пункт меню установлен." : "Пункт меню не установлен.";
        Refresh(); content.Children.Add(status);
        foreach (var (label, action) in new (string, Action)[] { ("Добавить пункт меню", registration.Register), ("Удалить пункт меню", registration.Unregister) })
        {
            var button = new Button { Content = label, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8) };
            button.Click += (_, _) =>
            {
                try { action(); Refresh(); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                { System.Windows.MessageBox.Show("Не удалось изменить меню: " + ex.Message, "DesktopNotes"); }
            };
            content.Children.Add(button);
        }
        content.Children.Add(new TextBlock { Text = "Автозапуск выключен. Заметки хранятся локально:\n" + _dataPath, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 10) });
        var folder = new Button { Content = "Открыть папку заметок", Padding = new Thickness(12, 8, 12, 8) };
        folder.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(_dataPath); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { _dataPath } }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { System.Windows.MessageBox.Show(ex.Message, "DesktopNotes"); }
        };
        content.Children.Add(folder);
        var logFolder = new Button { Content = "Открыть папку журналов", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 0, 0) };
        logFolder.Click += (_, _) =>
        {
            try { var path = Path.Combine(_dataPath, "logs"); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path } }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { logger.Write(AppLogLevel.Error, "Logs.OpenFailed", "Could not open log directory", error); System.Windows.MessageBox.Show(error.Message, "DesktopNotes"); }
        };
        content.Children.Add(logFolder);
        _settings = new Window { Title = "DesktopNotes — настройки", Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = content };
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
        if (_tray?.ContextMenuStrip is not null) _tray.ContextMenuStrip.Enabled = false;
        if (!await _save.FlushAsync())
        {
            System.Windows.MessageBox.Show("Выход отменён: изменения не сохранены. Освободите место или восстановите доступ к папке, затем повторите выход.", "DesktopNotes", MessageBoxButton.OK, MessageBoxImage.Warning);
            foreach (var window in _windows.Values) window.IsEnabled = true;
            if (_tray?.ContextMenuStrip is not null) _tray.ContextMenuStrip.Enabled = true;
            _exiting = false; return;
        }
        foreach (var window in _windows.Values) window.ClosePermanently();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        logger.Write(AppLogLevel.Information, "Shutdown.Exit", $"exitCode={e.ApplicationExitCode}");
        DispatcherUnhandledException -= OnDispatcherException;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _board?.Dispose();
        _tray?.ContextMenuStrip?.Dispose();
        _tray?.Dispose();
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
                Dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show("Ошибка отображения Markdown. Заметки доступны в исходном виде; текст сохранён без преобразования.\n\nЖурнал: " + Path.Combine(_dataPath, "logs"), "DesktopNotes", MessageBoxButton.OK, MessageBoxImage.Warning));
                return;
            }
            catch (Exception recoveryError) { logger.Write(AppLogLevel.Error, "Editor.RecoveryFailed", "Could not restore source editor", recoveryError); }
        }
        _handlingFatalError = true;
        e.Handled = true;
        System.Windows.MessageBox.Show("DesktopNotes завершится из-за ошибки.\n\n" + e.Exception.Message + "\n\nЖурнал: " + Path.Combine(_dataPath, "logs"), "DesktopNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }
}
