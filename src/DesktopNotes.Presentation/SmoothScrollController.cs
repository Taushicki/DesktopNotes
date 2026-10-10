using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;

namespace DesktopNotes.Presentation;

// Subscribe to rendering only while the wheel moves the viewport. Editing or
// dragging the scrollbar cancels motion, leaving caret scrolling in control.
internal sealed class SmoothScrollController : IDisposable
{
    private readonly TextEditor _editor;
    private readonly Stopwatch _clock = new();
    private double _from, _target;
    internal bool IsAnimating { get; private set; }
    private double Maximum => Math.Max(0, _editor.ExtentHeight - _editor.ViewportHeight);

    internal SmoothScrollController(TextEditor editor)
    {
        _editor = editor;
        editor.PreviewMouseWheel += OnWheel;
        editor.PreviewKeyDown += OnKey;
        editor.PreviewMouseDown += OnMouse;
        editor.PreviewTextInput += OnTextInput;
        editor.TextChanged += OnTextChanged;
        editor.Unloaded += OnUnloaded;
        editor.SizeChanged += OnSizeChanged;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || Maximum <= 0) { Cancel(); return; }
        var lines = SystemParameters.WheelScrollLines;
        var distance = lines < 0 ? _editor.ViewportHeight : _editor.TextArea.TextView.DefaultLineHeight * lines * .75;
        _from = _editor.VerticalOffset;
        _target = Math.Clamp((IsAnimating ? _target : _from) - e.Delta / 120d * distance, 0, Maximum);
        e.Handled = true;
        if (Math.Abs(_target - _from) < .1) { Cancel(); return; }
        _clock.Restart();
        if (!IsAnimating)
        {
            IsAnimating = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e) => Advance(_clock.Elapsed);
    internal void Advance(TimeSpan elapsed)
    {
        if (!IsAnimating) return;
        var progress = Math.Clamp(elapsed.TotalMilliseconds / 180, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        _editor.ScrollToVerticalOffset(Math.Clamp(_from + (_target - _from) * eased, 0, Maximum));
        if (progress >= 1) Cancel();
    }

    internal void Cancel()
    {
        if (!IsAnimating) return;
        CompositionTarget.Rendering -= OnRendering;
        IsAnimating = false;
        _clock.Stop();
    }
    private void OnKey(object sender, KeyEventArgs e) => Cancel();
    private void OnMouse(object sender, MouseButtonEventArgs e) => Cancel();
    private void OnTextInput(object sender, TextCompositionEventArgs e) => Cancel();
    private void OnTextChanged(object? sender, EventArgs e) => Cancel();
    private void OnUnloaded(object sender, RoutedEventArgs e) => Cancel();
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Cancel();

    public void Dispose()
    {
        Cancel();
        _editor.PreviewMouseWheel -= OnWheel;
        _editor.PreviewKeyDown -= OnKey;
        _editor.PreviewMouseDown -= OnMouse;
        _editor.PreviewTextInput -= OnTextInput;
        _editor.TextChanged -= OnTextChanged;
        _editor.Unloaded -= OnUnloaded;
        _editor.SizeChanged -= OnSizeChanged;
    }
}
