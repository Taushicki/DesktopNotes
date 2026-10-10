using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Presentation;
using Xunit;

namespace DesktopNotes.Tests;

[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageCollection;

[Collection("Language")]
public sealed class SettingsWindowTests
{
    private sealed class Store : IAppSettingsStore
    {
        public AppSettings Saved { get; private set; } = new();
        public AppSettings Load() => Saved;
        public void Save(AppSettings settings) => Saved = settings;
    }
    private sealed class Registration : IDesktopMenuRegistration
    {
        public bool IsRegistered { get; private set; }
        public void Register() => IsRegistered = true;
        public void Unregister() => IsRegistered = false;
    }
    private sealed class Logger : IAppLogger { public void Write(AppLogLevel level, string eventName, string message, Exception? exception = null) { } }

    [Fact]
    public Task SettingsControlsPersistAndTranslateWithoutRestart() => OnSta(() =>
    {
        var store = new Store(); var service = new AppSettingsService(store);
        service.Changed += settings => UiText.SetLanguage(settings.Language);
        var vm = new SettingsViewModel(service, new Registration(), @"C:\Notes", new Logger());
        var host = new SettingsWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        try
        {
            host.Show(); host.UpdateLayout();
            var language = (ComboBox)host.FindName("LanguageBox");
            var level = (ComboBox)host.FindName("LogLevelBox");
            var logging = (ToggleButton)host.FindName("LoggingSwitch");
            language.SelectedValue = AppLanguage.English;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Equal(AppLanguage.English, store.Saved.Language);
            Assert.Equal("Settings", host.Title);
            Assert.Equal("Information", ((ComboBoxItem)level.Items[0]).Content);
            level.SelectedValue = AppLogLevel.Error;
            Assert.Equal(AppLogLevel.Error, store.Saved.MinimumLogLevel);
            logging.IsChecked = false;
            Assert.False(store.Saved.LoggingEnabled); Assert.False(level.IsEnabled);
            logging.IsChecked = true;
            Assert.True(level.IsEnabled); Assert.Equal(AppLogLevel.Error, level.SelectedValue);
            vm.InstallMenu.Execute(null); Assert.Equal("The menu command is installed.", vm.RegistrationStatus);
            vm.RemoveMenu.Execute(null); Assert.Equal("The menu command is not installed.", vm.RegistrationStatus);
            var tray = new TrayMenu(() => { }, () => { }, () => { }, () => Task.CompletedTask, () => Task.CompletedTask);
            Assert.Equal("New note", ((MenuItem)tray.Menu.Items[0]).Header);
            var dialog = new DeleteConfirmationWindow(NoteThemes.Get(NoteColor.Yellow));
            try { Assert.Equal("Cancel", ((Button)dialog.FindName("CancelButton")).Content); }
            finally { dialog.Close(); }
            foreach (var color in new[] { NoteColor.Yellow, NoteColor.GitHubDark })
            {
                NoteMenuAppearance.ApplyResources(host.Resources, NoteThemes.Get(color));
                host.Resources["DesktopNotes.EditorScrollThumb"] = NoteThemes.Get(color).ScrollThumb; host.UpdateLayout();
                var surface = (Border)host.FindName("Surface");
                Assert.Same(color == NoteColor.Yellow ? Brushes.White : NoteThemes.Get(color).CodeFill, surface.Background);
                language.IsDropDownOpen = true;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                var popup = (Popup)language.Template.FindName("PART_Popup", language);
                Assert.True(popup.Child.IsVisible);
                language.IsDropDownOpen = false;
                if (Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR") is { } directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"settings-{color}.png")); encoder.Save(file);
                }
            }
            language.SelectedValue = AppLanguage.Russian;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Equal("Настройки", host.Title);
        }
        finally { host.Close(); UiText.SetLanguage(AppLanguage.Russian); }
    });

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
