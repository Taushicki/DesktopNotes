using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure;
using DesktopNotes.Presentation;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class WindowsInteractionTests
{
    private sealed class Repository : INoteRepository
    {
        public Task<LoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LoadResult(Array.Empty<Note>()));
        public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Layers : IWindowLayerController { public void Apply(nint handle, WindowLayer layer) { } }

    [Fact]
    public Task ToolbarBoldPreservesSelectionAndUndoWithoutChangingWholeNoteStyle() => OnStaThread(() =>
    {
        var notes = new NoteService(new Repository()); var note = notes.Create(100, 100);
        using var vm = new NoteViewModel(note, notes, () => { }, () => { }); vm.Markdown = "before word after";
        var host = new NoteWindow(vm, new Layers(), new MarkdownRenderer());
        try
        {
            var editor = host.MarkdownEditor;
            editor.ApplyTemplate(); editor.Measure(new Size(280, 200)); editor.Arrange(new Rect(0, 0, 280, 200));
            Assert.Equal("before word after", editor.Text);
            editor.Select(7, 4);
            var row = Assert.IsType<StackPanel>(Assert.IsType<Border>(host.FormattingToolbar.Content).Child);
            var button = Assert.IsType<Button>(row.Children[2]);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("before **word** after", notes.Get(note.Id).Markdown);
            Assert.False(notes.Get(note.Id).IsBold); Assert.Equal("**word**", editor.SelectedText);
            editor.Undo();
            Assert.Equal("before word after", notes.Get(note.Id).Markdown);
            editor.Select(7, 4); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("before word after", notes.Get(note.Id).Markdown);
            Assert.Equal("word", editor.SelectedText);
            editor.Select(0, 0); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(notes.Get(note.Id).IsBold);
            void NoTooltips(DependencyObject element)
            {
                if (element is FrameworkElement control) Assert.Null(control.ToolTip);
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) NoTooltips(VisualTreeHelper.GetChild(element, i));
            }
            NoTooltips(host); NoTooltips(host.FormattingToolbar);
        }
        finally { host.ClosePermanently(); }
    });

    [Fact]
    public Task ToolbarFocusKeepsSelectionAndCommandsWorkUntilAnotherWindowIsSelected() => OnStaThread(() =>
    {
        var notes = new NoteService(new Repository()); var note = notes.Create(100, 100);
        using var vm = new NoteViewModel(note, notes, () => { }, () => { });
        var host = new NoteWindow(vm, new Layers(), new MarkdownRenderer());
        var external = new Window { ShowInTaskbar = false };
        using var clicks = new DesktopClickObserver();
        using var board = new BoardInteractionController(notes, new GroupSelectionSession(notes), clicks, _ => host, Dispatcher.CurrentDispatcher);
        board.Attach(host);
        try
        {
            host.ConnectionClicked!(host);
            var toolbar = host.FormattingToolbar;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(toolbar).EnsureHandle();
            Assert.True(host.OwnsInteractionWindow(hwnd));
            board.ReconcileInteractionFocus(host, hwnd);
            Assert.True(host.IsConnectionSelected);
            var row = Assert.IsType<StackPanel>(Assert.IsType<Border>(toolbar.Content).Child);
            Assert.IsType<Button>(row.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsType<ComboBox>(row.Children[1]).SelectedItem = 20d;
            Assert.IsType<Button>(row.Children[4]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(notes.Get(note.Id).IsBold); Assert.True(notes.Get(note.Id).IsPinned); Assert.Equal(20, notes.Get(note.Id).FontSize);
            board.ReconcileInteractionFocus(host, hwnd);
            Assert.True(host.IsConnectionSelected);
            board.ReconcileInteractionFocus(host, new System.Windows.Interop.WindowInteropHelper(external).EnsureHandle());
            Assert.False(host.IsConnectionSelected);
        }
        finally { host.ClosePermanently(); external.Close(); }
    });

    [Theory]
    [InlineData(320, 1)]
    [InlineData(220, 1)]
    [InlineData(640, 2)]
    [InlineData(480, 1.5)]
    public void ToolbarIsCenteredAtDifferentNoteSizesAndDpi(int noteWidth, double scale)
    {
        var note = new ScreenRect { Left = 500, Top = 500, Right = 500 + noteWidth, Bottom = 800 };
        var bounds = NoteFormattingToolbar.CalculateBounds(note, new ScreenRect { Right = 2000, Bottom = 1200 }, scale);
        Assert.InRange(Math.Abs((bounds.Left + bounds.Right) / 2.0 - (note.Left + note.Right) / 2.0), 0, 0.5);
        Assert.True(bounds.Bottom < note.Top);
    }

    [Fact]
    public void ToolbarRemainsVisibleAtScreenEdges()
    {
        var area = new ScreenRect { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };
        var bounds = NoteFormattingToolbar.CalculateBounds(new ScreenRect { Left = -220, Top = 0, Right = 0, Bottom = 280 }, area, 1);
        Assert.True(bounds.Left >= area.Left); Assert.True(bounds.Right <= area.Right);
        Assert.True(bounds.Top >= 280); Assert.True(bounds.Bottom <= area.Bottom);
    }

    [Fact]
    public Task FormattingToolbarChangesNoteSettingsWithoutChangingMarkdown() => OnStaThread(() =>
    {
        var notes = new NoteService(new Repository()); var note = notes.Create(100, 100);
        using var vm = new NoteViewModel(note, notes, () => { }, () => { }); vm.Markdown = "# Heading\n\n**bold** text";
        var host = new NoteWindow(vm, new Layers(), new MarkdownRenderer());
        using var toolbar = new NoteFormattingToolbar(host, vm);
        try
        {
            var surface = Assert.IsType<Border>(toolbar.Content);
            var row = Assert.IsType<StackPanel>(surface.Child);
            Assert.Equal(5, row.Children.Count);
            Assert.IsType<ComboBox>(row.Children[1]).SelectedItem = 24d;
            Assert.IsType<Button>(row.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsType<Button>(row.Children[4]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = notes.Get(note.Id);
            Assert.Equal(24, saved.FontSize); Assert.True(saved.IsBold); Assert.True(saved.IsPinned);
            Assert.Equal("# Heading\n\n**bold** text", saved.Markdown);
            surface.Measure(new Size(272, 56)); surface.Arrange(new Rect(0, 0, 272, 56));
            var bitmap = new RenderTargetBitmap(272, 56, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
            if (Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR") is { } directory)
            {
                Directory.CreateDirectory(directory); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, "note-formatting-toolbar.png")); encoder.Save(file);
            }
        }
        finally { host.ClosePermanently(); }
    });

    [Fact]
    public Task PreviewFormattingScalesHeadingsCodeAndPreservesInlineMarkdown() => OnStaThread(() =>
    {
        var document = new MarkdownRenderer().Render("# Title\n\n**bold** normal\n\n```\ncode\n```");
        NoteWindow.ApplyDocumentFormatting(document, new Note { FontSize = 28, IsBold = true, TextAlignment = NoteTextAlignment.Center });
        Assert.Equal(28, document.FontSize); Assert.Equal(TextAlignment.Center, document.TextAlignment);
        Assert.Equal(52, document.Blocks.FirstBlock.FontSize);
        Assert.Equal(24, document.Blocks.LastBlock.FontSize);
        Assert.Equal(FontWeights.Bold, document.FontWeight);
        Assert.Equal(TextAlignment.Right, NoteWindow.TextAlignmentFor(NoteTextAlignment.Right));
        Assert.Equal(TextAlignment.Justify, NoteWindow.TextAlignmentFor(NoteTextAlignment.Justify));
    });

    [Fact]
    public Task ClickingANoteSelectsOnlyItsFourConnectionPortsAndCtrlStillSelectsGroups() => OnStaThread(() =>
    {
        var notes = new NoteService(new Repository());
        var a = notes.Create(100, 100); var b = notes.Create(450, 100);
        var windows = new Dictionary<Guid, NoteWindow>();
        using var clicks = new DesktopClickObserver();
        var selection = new GroupSelectionSession(notes);
        using var board = new BoardInteractionController(notes, selection, clicks, id => windows[id], Dispatcher.CurrentDispatcher);
        try
        {
            foreach (var note in new[] { a, b })
            {
                var window = new NoteWindow(new NoteViewModel(note, notes, () => { }, () => { }), new Layers(), new MarkdownRenderer());
                windows.Add(note.Id, window); board.Attach(window);
            }
            windows[a.Id].ConnectionClicked!(windows[a.Id]);
            Assert.True(windows[a.Id].IsConnectionSelected);
            windows[b.Id].ConnectionClicked!(windows[b.Id]);
            Assert.False(windows[a.Id].IsConnectionSelected);
            Assert.True(windows[b.Id].IsConnectionSelected);
            windows[b.Id].CancelInteraction!();
            Assert.False(windows[b.Id].IsConnectionSelected);
            windows[a.Id].ConnectionClicked!(windows[a.Id]);
            windows[b.Id].ControlClicked!(windows[b.Id]);
            Assert.False(windows[a.Id].IsConnectionSelected);
            Assert.Contains(b.Id, selection.Members);
        }
        finally { foreach (var window in windows.Values) window.ClosePermanently(); }
    });

    [Fact]
    public Task FourOwnedPortWindowsAppearAfterSelectionWithoutAltAndHideTogether() => OnStaThread(() =>
    {
        var host = new Window { Left = -10000, Top = -10000, Width = 320, Height = 280, ShowActivated = false, ShowInTaskbar = false };
        EdgeLinkHandles? handles = null;
        try
        {
            host.Show();
            handles = new EdgeLinkHandles(host, _ => { }, () => { }, () => { }, () => { });
            Assert.Equal(0, handles.VisiblePortCount);
            handles.SetState(true, false);
            Assert.Equal(4, handles.VisiblePortCount);
            var sourceBounds = NativeWindows.Rect(new System.Windows.Interop.WindowInteropHelper(host).Handle);
            foreach (Window port in host.OwnedWindows)
            {
                port.UpdateLayout();
                var bounds = NativeWindows.Rect(new System.Windows.Interop.WindowInteropHelper(port).Handle);
                Assert.True(bounds.Right < sourceBounds.Left || bounds.Left > sourceBounds.Right || bounds.Bottom < sourceBounds.Top || bounds.Top > sourceBounds.Bottom);
                var glyph = Assert.IsType<EdgeLinkHandles.PortVisual>(Assert.IsType<Button>(port.Content).Content);
                Assert.Equal(28, glyph.ActualWidth); Assert.Equal(28, glyph.ActualHeight);
            }
            handles.SetState(false, false);
            Assert.Equal(0, handles.VisiblePortCount);
            handles.SetState(true, false);
            host.Hide();
            Assert.Equal(0, handles.VisiblePortCount);
        }
        finally { handles?.Dispose(); host.Close(); }
    });

    [Fact]
    public Task DragPreviewMovesItsArrowheadAndDoesNotCreateEndpointWindows() => OnStaThread(() =>
    {
        var host = new Window { Left = -10000, Top = -10000, Width = 320, Height = 280, ShowActivated = false, ShowInTaskbar = false };
        var preview = new ArrowOverlay();
        try
        {
            host.Show();
            var owner = new System.Windows.Interop.WindowInteropHelper(host).Handle;
            var start = new DiagramPoint(-9600, -9800);
            var first = new DiagramPoint(-9500, -9800);
            var moved = new DiagramPoint(-9400, -9750);
            preview.Draw([start, first], new LinkStyle(), owner, owner);
            preview.UpdateLayout();
            var visual = Assert.IsType<ArrowOverlay.ArrowVisual>(preview.Content);
            Assert.True(preview.IsVisible);
            Assert.True(preview.Topmost);
            Assert.False(preview.IsHitTestVisible);
            Assert.False(visual.Interactive);
            Assert.Equal(first, visual.Points[^1]);
            preview.Draw([start, new(-9500, start.Y), new(-9500, moved.Y), moved], new LinkStyle(), owner, owner);
            preview.UpdateLayout();
            Assert.Equal(moved, visual.Points[^1]);
            Assert.Empty(host.OwnedWindows.Cast<Window>());
            var tip = new Point((moved.X - visual.PhysicalOrigin.Left) / visual.Scale - 3,
                (moved.Y - visual.PhysicalOrigin.Top) / visual.Scale);
            var image = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(visual);
            var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(pixels, image.PixelWidth * 4, 0);
            var index = ((int)tip.Y * image.PixelWidth + (int)tip.X) * 4;
            Assert.InRange(pixels[index], (byte)110, (byte)150); // Grey arrowhead, no white endpoint ring.
            preview.Hide();
            Assert.False(preview.IsVisible);
        }
        finally { preview.Close(); host.Close(); }
    });

    [Fact]
    public Task RoundedCornerHitAreaFollowsTheRoundedPath() => OnStaThread(() =>
    {
        var visual = new ArrowOverlay.ArrowVisual
        {
            Points = [new(20, 80), new(100, 80), new(100, 20), new(180, 20)],
            PhysicalOrigin = new ScreenRect { Right = 200, Bottom = 100 }, Interactive = true
        };
        Assert.True(visual.ContainsPoint(new Point(94, 74)));
        Assert.False(visual.ContainsPoint(new Point(100, 80)));
        Assert.True(visual.ContainsPoint(new Point(100, 50)));
    });

    [Fact]
    public Task ConnectionDesignUsesGreyLinesBlueEndpointRingsAndAHoverArrow() => OnStaThread(() =>
    {
        RenderTargetBitmap Capture(FrameworkElement content, int width, int height)
        {
            var surface = new Grid { Background = Brushes.White };
            surface.Children.Add(content);
            surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
            return bitmap;
        }
        void Save(RenderTargetBitmap bitmap, string name)
        {
            var directory = Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR");
            if (directory is null) return;
            Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
        }
        var arrow = new ArrowOverlay.ArrowVisual
        {
            Points = [new(30, 160), new(150, 160), new(150, 60), new(270, 60)], IsSelected = true,
            PhysicalOrigin = new ScreenRect { Right = 300, Bottom = 200 }
        };
        var image = Capture(arrow, 300, 200);
        var pixels = new byte[300 * 200 * 4]; image.CopyPixels(pixels, 1200, 0);
        var line = (160 * 300 + 75) * 4;
        Assert.Equal(pixels[line], pixels[line + 1]); Assert.Equal(pixels[line + 1], pixels[line + 2]);
        Assert.InRange(pixels[line], (byte)110, (byte)150);
        Assert.Equal(255, pixels[(160 * 300 + 30) * 4]); // White fill in the selected endpoint.
        var ring = (154 * 300 + 30) * 4;
        Assert.True(pixels[ring] > pixels[ring + 2]); // Blue ring, not a grey endpoint.
        Save(image, "rounded-connection-selected.png");
        var port = new EdgeLinkHandles.PortVisual { Side = NoteSide.Right, IsHovered = true };
        var portImage = Capture(port, 28, 28);
        var portPixels = new byte[28 * 28 * 4]; portImage.CopyPixels(portPixels, 112, 0);
        var shaft = (14 * 28 + 8) * 4;
        Assert.True(portPixels[shaft] > portPixels[shaft + 2]);
        Save(portImage, "connection-port-hover.png");
    });

    [Theory]
    [InlineData(NoteSide.Left)]
    [InlineData(NoteSide.Top)]
    [InlineData(NoteSide.Right)]
    [InlineData(NoteSide.Bottom)]
    public void AllFourMarkersAreOutsideNoteAndCenteredOnItsSides(NoteSide side)
    {
        var note = new NoteBounds(100, 200, 320, 280);
        var marker = EdgeLinkHandles.MarkerBounds(note, side, 1.5);
        Assert.Equal(42, marker.Width);
        Assert.True(side switch
        {
            NoteSide.Left => marker.Right < note.X,
            NoteSide.Top => marker.Bottom < note.Y,
            NoteSide.Right => marker.X > note.Right,
            _ => marker.Y > note.Bottom
        });
        var anchor = ArrowRouter.Anchor(note, side);
        var center = new DiagramPoint(marker.X + marker.Width / 2, marker.Y + marker.Height / 2);
        Assert.Equal(36, Math.Sqrt(Math.Pow(anchor.X - center.X, 2) + Math.Pow(anchor.Y - center.Y, 2)));
    }

    [Fact]
    public Task ArrowHitAreaFollowsCurveAndDoesNotCoverEmptyWindowPixels() => OnStaThread(() =>
    {
        var visual = new ArrowOverlay.ArrowVisual
        {
            Points = [new(20, 50), new(180, 50)], LinkStyle = new LinkStyle { Route = LinkRoute.Curved },
            PhysicalOrigin = new ScreenRect { Right = 200, Bottom = 100 }, Interactive = true
        };
        Assert.True(visual.ContainsPoint(new Point(100, 32)));
        Assert.False(visual.ContainsPoint(new Point(100, 50)));
        Assert.False(visual.ContainsPoint(new Point(5, 5)));
        visual.Measure(new Size(200, 100)); visual.Arrange(new Rect(0, 0, 200, 100));
        var bitmap = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[200 * 100 * 4]; bitmap.CopyPixels(pixels, 800, 0);
        Assert.Equal(0, pixels[(5 * 200 + 5) * 4 + 3]);
        Assert.True(pixels[(32 * 200 + 100) * 4 + 3] > 0);
        Assert.True(pixels[(37 * 200 + 100) * 4 + 3] > 0);
    });

    [Fact]
    public Task DeleteKeyOnlyDeletesWhenArrowIsSelected() => OnStaThread(() =>
    {
        var arrow = new ArrowOverlay(interactive: true);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(arrow).EnsureHandle();
            Assert.True(NativeWindows.IsOwnWindow(hwnd));
            Assert.False(NativeWindows.IsOwnWindow(0));
            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd)!;
            var deleted = 0;
            arrow.DeleteRequested += () => deleted++;
            void Delete()
            {
                var e = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Delete)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                arrow.RaiseEvent(e);
            }
            Delete(); Assert.Equal(0, deleted);
            arrow.SetSelected(true); Delete(); Assert.Equal(1, deleted);
            arrow.SetSelected(false); Delete(); Assert.Equal(1, deleted);
        }
        finally { arrow.Close(); }
    });

    [Fact]
    public Task GroupMovementUpdatesNativeWindowsAndHiddenMemberCoordinatesWithoutDrift() => OnStaThread(() =>
    {
        var notes = new NoteService(new Repository());
        var first = notes.Create(100, 100);
        var second = notes.Create(450, 100);
        notes.SetHidden(second.Id, true);
        notes.CreateGroup(new[] { first.Id, second.Id });
        var windows = new Dictionary<Guid, NoteWindow>();
        BoardInteractionController? board = null;
        NoteWindow Get(Guid id)
        {
            if (windows.TryGetValue(id, out var found)) return found;
            var vm = new NoteViewModel(notes.Get(id), notes, () => { }, () => { });
            var window = new NoteWindow(vm, new Layers(), new MarkdownRenderer());
            windows.Add(id, window);
            board!.Attach(window);
            return window;
        }
        using var clicks = new DesktopClickObserver();
        using var controller = new BoardInteractionController(notes, new GroupSelectionSession(notes), clicks, Get, Dispatcher.CurrentDispatcher);
        board = controller;
        try
        {
            var leader = Get(first.Id);
            var member = Get(second.Id);
            var before = NativeWindows.Rect(leader.Handle);
            var memberBefore = NativeWindows.Rect(member.Handle);
            Assert.True(leader.BeginDrag!(leader));
            var moved = leader.Moving!(leader, before.Offset(48, 24));
            leader.MovePhysical(moved.Left, moved.Top);
            leader.EndDrag!(leader);
            var memberAfter = NativeWindows.Rect(member.Handle);
            Assert.Equal(memberBefore.Left + 48, memberAfter.Left);
            Assert.Equal(memberBefore.Top + 24, memberAfter.Top);
            Assert.True(notes.Get(second.Id).IsHidden);
            Assert.True(notes.Get(first.Id).X > first.X);
            Assert.True(notes.Get(second.Id).X > second.X);
            notes.Update(notes.Get(second.Id) with { IsPinned = true });
            Assert.False(leader.BeginDrag!(leader));
            var blocked = leader.Moving!(leader, moved.Offset(10, 10));
            Assert.Equal(NativeWindows.Rect(leader.Handle).Left, blocked.Left);
        }
        finally { foreach (var window in windows.Values) window.ClosePermanently(); }
    });

    [Fact]
    public Task CtrlSelectionPinAndGroupActionsAreWiredIntoTheNoteWindow() => OnStaThread(() =>
    {
        var notes = new NoteService(new Repository());
        var a = notes.Create(100, 100); var b = notes.Create(450, 100);
        var selection = new GroupSelectionSession(notes);
        using var clicks = new DesktopClickObserver();
        using var window = new WindowLifetime(new NoteWindow(new NoteViewModel(a, notes, () => { }, () => { }), new Layers(), new MarkdownRenderer()));
        using var board = new BoardInteractionController(notes, selection, clicks, _ => window.Window, Dispatcher.CurrentDispatcher);
        board.Attach(window.Window);
        window.Window.ControlClicked!(window.Window);
        selection.Toggle(b.Id);
        window.Window.CommitSelection!();
        Assert.Single(notes.Groups);
        var menu = new ContextMenu();
        window.Window.PopulateBoardMenu!(window.Window, menu);
        Assert.Contains(menu.Items.Cast<MenuItem>(), item => (string)item.Header == "Цвет рамки группы…");
        Assert.Contains(menu.Items.Cast<MenuItem>(), item => (string)item.Header == "Распустить группу");
    });

    private sealed class WindowLifetime(NoteWindow window) : IDisposable
    {
        public NoteWindow Window => window;
        public void Dispose() => window.ClosePermanently();
    }

    [Fact]
    public Task ArrowRendererPaintsLineAndArrowheadWithoutAnInteractiveWindow() => OnStaThread(() =>
    {
        var visual = new ArrowOverlay.ArrowVisual
        {
            Points = new[] { new DiagramPoint(20, 50), new DiagramPoint(180, 50) },
            LinkStyle = new LinkStyle { Color = "#112233" },
            PhysicalOrigin = new ScreenRect { Left = 0, Top = 0, Right = 200, Bottom = 100 },
            Scale = 1
        };
        visual.Measure(new Size(200, 100));
        visual.Arrange(new Rect(0, 0, 200, 100));
        var bitmap = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[200 * 100 * 4];
        bitmap.CopyPixels(pixels, 200 * 4, 0);
        Assert.True(pixels[(50 * 200 + 100) * 4 + 3] > 0);
        Assert.True(pixels[(50 * 200 + 176) * 4 + 3] > 0);
        Assert.Equal(0, pixels[(5 * 200 + 5) * 4 + 3]);
    });
    private static Task OnStaThread(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
