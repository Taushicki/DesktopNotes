using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Presentation;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class OptimizationTests
{
    private sealed class Repository : INoteRepository
    {
        public readonly TaskCompletionSource FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkspaceSnapshot Stored = new();
        private int _writes;
        public Task<LoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LoadResult(Stored));
        public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default) => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref _writes) == 1) { FirstStarted.SetResult(); await ReleaseFirst.Task; }
            Stored = snapshot;
        }, cancellationToken);
    }
    private sealed class Context : SynchronizationContext
    {
        public readonly ConcurrentQueue<Action> Posted = new();
        public override void Post(SendOrPostCallback callback, object? state) => Posted.Enqueue(() => callback(state));
    }
    private sealed class Layers : IWindowLayerController { public void Apply(nint handle, WindowLayer layer) { } }

    [Fact]
    public Task ShutdownFlushCompletesWhileDispatcherIsBlockedAndPreservesNewestSnapshot() => Task.Run(VerifyShutdownFlush);

    // Simulate the synchronous Windows callback on an isolated thread with a non-pumping context.
    private static void VerifyShutdownFlush()
    {
        var previous = SynchronizationContext.Current; var context = new Context();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var repository = new Repository(); var notes = new NoteService(repository);
            using var save = new AutosaveCoordinator(notes, repository, TimeSpan.FromHours(1));
            var notified = 0; save.Saved += () => notified++;
            var note = notes.Create(0, 0); var first = save.FlushAsync();
            Assert.True(repository.FirstStarted.Task.Wait(TimeSpan.FromSeconds(5)));
            notes.Update(notes.Get(note.Id) with { Markdown = "# Последнее изменение" });
            var shutdown = save.FlushAsync(); repository.ReleaseFirst.SetResult();
            Assert.True(Task.WhenAll(first, shutdown).Wait(TimeSpan.FromSeconds(10)));
            Assert.True(shutdown.Result); Assert.Equal("# Последнее изменение", Assert.Single(repository.Stored.Notes).Markdown);
            Assert.Equal(0, notified);
            while (context.Posted.TryDequeue(out var action)) action();
            Assert.Equal(2, notified);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public Task TableMetricsAreReusedUntilTextOrAppearanceChanges() => OnSta(() =>
    {
        var source = "| A | B |\n| --- | --- |\n" + string.Join('\n', Enumerable.Range(0, 300).Select(i => $"| **Строка {i}** | `value` |"));
        using var editor = new LiveMarkdownEditor { Text = source };
        var column = editor.Layout.Decorations.First(part => part.Kind == InlineMarkdownLayout.DecorationKind.TableGap).Column!;
        var first = InlineMarkdownMetrics.MeasureColumn(editor, column);
        var count = InlineMarkdownMetrics.MeasurementCount(editor);
        Assert.True(count > 250);
        for (var i = 0; i < 100; i++) Assert.Equal(first, InlineMarkdownMetrics.MeasureColumn(editor, column));
        Assert.Equal(count, InlineMarkdownMetrics.MeasurementCount(editor));
        editor.SetAppearance(new Note { FontSize = 28 });
        Assert.InRange(InlineMarkdownMetrics.MeasureColumn(editor, column), first * 1.99, first * 2.01);
        editor.SetAppearance(new Note { FontSize = 14 });
        editor.Text = "| A | B |\n| --- | --- |\n| x | y |";
        var smaller = editor.Layout.Decorations.First(part => part.Kind == InlineMarkdownLayout.DecorationKind.TableGap).Column!;
        Assert.True(InlineMarkdownMetrics.MeasureColumn(editor, smaller) < first);
        Assert.True(InlineMarkdownMetrics.MeasurementCount(editor) < count);
    });

    [Fact]
    public Task TypingAndMovementDoNotRebuildToolbarOrResetPendingSizeInput() => OnSta(() =>
    {
        var notes = new NoteService(new Repository()); var note = notes.Create(0, 0);
        using var vm = new NoteViewModel(note, notes, () => { }, () => { });
        var window = new NoteWindow(vm, new Layers());
        try
        {
            var row = Assert.IsType<StackPanel>(Assert.IsType<Border>(window.FormattingToolbar.Content).Child);
            var color = Assert.IsType<Button>(row.Children[0]); var originalColor = color.Content;
            var size = Assert.IsType<ComboBox>(row.Children[1]); size.Text = "2";
            var events = new List<string?>(); vm.PropertyChanged += (_, e) => events.Add(e.PropertyName);
            vm.Markdown = "# Заголовок\n\nТекст";
            Assert.Equal(1, events.Count(name => name == nameof(NoteViewModel.Markdown)));
            Assert.Equal(1, events.Count(name => name == nameof(NoteViewModel.Snapshot)));
            events.Clear(); vm.SetBounds(50, 60, 320, 280);
            Assert.Single(events); Assert.Equal(nameof(NoteViewModel.Snapshot), events[0]);
            vm.IsPinned = true;
            Assert.Same(originalColor, color.Content); Assert.Equal("2", size.Text);
            vm.FontSize = 18; Assert.Equal("18", size.Text);
        }
        finally { window.ClosePermanently(); }
    });

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
