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
    public static void Main(string[] args) => new NotesApp(args).Run();
}

public sealed class NotesApp(string[] arguments) : System.Windows.Application
{
    private readonly Dictionary<Guid, NoteWindow> _windows = [];
    private readonly string _dataPath = Path.GetFullPath(Environment.GetEnvironmentVariable("DESKTOPNOTES_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopNotes"));
    private ServiceProvider? _services;
    private SingleInstance? _instance;
    private BoardInteractionController? _board;
    private NoteService _notes = null!;
    private AutosaveCoordinator _save = null!;
    private Forms.NotifyIcon? _tray;
    private Window? _settings;
    private bool _exiting;
    private bool _saveError;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var isolatedDirectory = Environment.GetEnvironmentVariable("DESKTOPNOTES_DATA_DIR");
            var isolatedName = isolatedDirectory is null ? null : "DesktopNotes-dev-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_dataPath.ToUpperInvariant())))[..16];
            _instance = new SingleInstance(isolatedName);
            if (!_instance.IsPrimary)
            {
                await _instance.SendAsync(arguments.Contains("--new-note") ? "--new-note" : "--show");
                Shutdown(); return;
            }
            var collection = new ServiceCollection();
            collection.AddSingleton<INoteRepository>(new JsonNoteRepository(_dataPath));
            collection.AddSingleton<NoteService>();
            collection.AddSingleton<AutosaveCoordinator>();
            collection.AddSingleton<IWindowLayerController, WindowLayerController>();
            collection.AddSingleton<IDesktopMenuRegistration>(new DesktopMenuRegistration(Environment.ProcessPath!));
            collection.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
            collection.AddSingleton<GroupSelectionSession>();
            collection.AddSingleton<DesktopClickObserver>();
            _services = collection.BuildServiceProvider();
            _notes = _services.GetRequiredService<NoteService>();
            var warning = await _notes.LoadAsync();
            _save = _services.GetRequiredService<AutosaveCoordinator>();
            _board = new BoardInteractionController(_notes, _services.GetRequiredService<GroupSelectionSession>(),
                _services.GetRequiredService<DesktopClickObserver>(), GetWindow, Dispatcher);
            _save.SaveFailed += OnSaveFailed;
            _save.Saved += () => { _saveError = false; if (_tray is not null) _tray.Text = "DesktopNotes"; };
            CreateTray();
            foreach (var note in _notes.Notes.Where(n => !n.IsHidden)) GetWindow(note.Id).Show();
            _instance.StartListening(command => Dispatcher.InvokeAsync(() =>
            {
                if (_exiting) return;
                if (command == "--new-note") CreateNote(); else ShowNotes();
            }).Task);
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            if (warning is not null) System.Windows.MessageBox.Show(warning, "DesktopNotes — восстановление");
            if (arguments.Contains("--new-note")) CreateNote();
            else if (_notes.Notes.Count == 0) { CreateNote(); OpenSettings(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnsupportedStorageVersionException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show("Не удалось запустить DesktopNotes.\n\n" + ex.Message, "DesktopNotes", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var window = new NoteWindow(vm, services.GetRequiredService<IWindowLayerController>(), services.GetRequiredService<IMarkdownRenderer>());
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
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "DesktopNotes", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => CreateNote();
    }

    private void OnSaveFailed(Exception error)
    {
        if (_tray is null) return;
        _tray.Text = "DesktopNotes — изменения не сохранены";
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
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _board?.Dispose();
        _tray?.ContextMenuStrip?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }
}
