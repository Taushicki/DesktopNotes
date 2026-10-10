using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DesktopNotes.Application;
using DesktopNotes.Domain;

namespace DesktopNotes.Presentation;

public sealed class RelayCommand(Action execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

public sealed class NoteViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly NoteService _service;
    private Note _note;
    public event PropertyChangedEventHandler? PropertyChanged;
    public Note Snapshot => _note;
    public string Title => _note.Title;
    public string Markdown { get => _note.Markdown; set => Change(_note with { Markdown = value }); }
    public NoteColor Color { get => _note.Color; set => Change(_note with { Color = value }); }
    public WindowLayer Layer { get => _note.Layer; set => Change(_note with { Layer = value }); }
    public bool IsCollapsed => _note.IsCollapsed;
    public bool IsPinned { get => _note.IsPinned; set => Change(_note with { IsPinned = value }); }
    public double FontSize { get => _note.FontSize; set => Change(_note with { FontSize = value }); }
    public bool IsBold { get => _note.IsBold; set => Change(_note with { IsBold = value }); }
    public NoteTextAlignment TextAlignment { get => _note.TextAlignment; set => Change(_note with { TextAlignment = value }); }
    public NoteVerticalAlignment VerticalAlignment { get => _note.VerticalAlignment; set => Change(_note with { VerticalAlignment = value }); }
    public ICommand ToggleCollapsed { get; }
    public ICommand Hide { get; }
    public ICommand Delete { get; }

    public NoteViewModel(Note note, NoteService service, Action hide, Action delete)
    {
        _note = note;
        _service = service;
        _service.NoteUpdated += OnNoteUpdated;
        ToggleCollapsed = new RelayCommand(() => Change(_note with { IsCollapsed = !_note.IsCollapsed }, nameof(IsCollapsed)));
        Hide = new RelayCommand(hide);
        Delete = new RelayCommand(delete);
    }

    private void Change(Note note, [CallerMemberName] string? property = null)
    {
        if (note == _note) return;
        _service.Update(note);
        OnChanged(property);
    }

    public void SetBounds(double x, double y, double width, double height) =>
        Change(_note with { X = x, Y = y, Width = Math.Max(220, width), Height = Math.Max(140, height) }, nameof(Snapshot));

    public void SetHidden(bool hidden) => Change(_note with { IsHidden = hidden }, nameof(Snapshot));
    private void OnNoteUpdated(Note note)
    {
        if (note.Id != _note.Id || note == _note) return;
        var previous = _note;
        _note = note;
        OnChanged(nameof(Snapshot));
        if (previous.Title != note.Title) OnChanged(nameof(Title));
        if (previous.Markdown != note.Markdown) OnChanged(nameof(Markdown));
        if (previous.Color != note.Color) OnChanged(nameof(Color));
        if (previous.Layer != note.Layer) OnChanged(nameof(Layer));
        if (previous.IsPinned != note.IsPinned) OnChanged(nameof(IsPinned));
        if (previous.FontSize != note.FontSize) OnChanged(nameof(FontSize));
        if (previous.IsBold != note.IsBold) OnChanged(nameof(IsBold));
        if (previous.TextAlignment != note.TextAlignment) OnChanged(nameof(TextAlignment));
        if (previous.VerticalAlignment != note.VerticalAlignment) OnChanged(nameof(VerticalAlignment));
        if (previous.IsCollapsed != note.IsCollapsed) OnChanged(nameof(IsCollapsed));
    }
    public void Dispose() => _service.NoteUpdated -= OnNoteUpdated;
    private void OnChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
