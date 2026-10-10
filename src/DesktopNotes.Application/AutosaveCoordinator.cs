using DesktopNotes.Domain;

namespace DesktopNotes.Application;

public sealed class AutosaveCoordinator : IDisposable
{
    private readonly NoteService _notes;
    private readonly INoteRepository _repository;
    private readonly TimeSpan _delay;
    private readonly SemaphoreSlim _writer = new(1);
    private CancellationTokenSource? _pending;
    private long _revision;
    private long _savedRevision;
    private bool _disposed;
    private readonly SynchronizationContext? _context;
    public event Action<Exception>? SaveFailed;
    public event Action? Saved;

    public AutosaveCoordinator(NoteService notes, INoteRepository repository, TimeSpan? delay = null)
    {
        _context = SynchronizationContext.Current;
        _notes = notes;
        _repository = repository;
        _delay = delay ?? TimeSpan.FromMilliseconds(500);
        _notes.Changed += Schedule;
    }

    private void Schedule()
    {
        Interlocked.Increment(ref _revision);
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(_pending.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(_delay, cancellationToken); await FlushAsync(); }
        catch (OperationCanceledException) { }
    }

    public async Task<bool> FlushAsync()
    {
        // Capture domain state on its owning thread before yielding. The writer and
        // file transaction must not depend on the dispatcher during session shutdown.
        var revision = Interlocked.Read(ref _revision);
        if (Interlocked.Read(ref _savedRevision) >= revision) return true;
        WorkspaceSnapshot snapshot = _notes.Snapshot;
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _savedRevision) >= revision) return true;
            await _repository.SaveAsync(snapshot).ConfigureAwait(false);
            Interlocked.Exchange(ref _savedRevision, revision);
            Notify(() => Saved?.Invoke());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(() => SaveFailed?.Invoke(ex));
            return false;
        }
        finally { _writer.Release(); }
    }

    private void Notify(Action action)
    {
        void Invoke() { if (!_disposed) action(); }
        if (_context is null || SynchronizationContext.Current == _context) Invoke();
        else _context.Post(_ => Invoke(), null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notes.Changed -= Schedule;
        _pending?.Cancel();
        _pending?.Dispose();
    }
}
