using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure;
using Forms = System.Windows.Forms;

namespace DesktopNotes.Presentation;

internal sealed class BoardInteractionController : IDisposable
{
    private readonly NoteService _notes;
    private readonly GroupSelectionSession _selection;
    private readonly DesktopClickObserver _clicks;
    private readonly Func<Guid, NoteWindow> _getWindow;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<Guid, NoteWindow> _windows = [];
    private readonly Dictionary<Guid, ArrowOverlay> _arrows = [];
    private readonly LinkSelectionSession _linkSelection;
    private Dictionary<Guid, ScreenRect>? _dragOrigins;
    private Guid? _leader;
    private double _dragScale;
    private bool _moving;
    private bool _refreshPending;
    private bool _disposed;
    private NoteWindow? _linkSource;
    private Guid? _connectionNote;
    private NoteWindow? _linkTarget;
    private ArrowOverlay? _linkPreview;
    private readonly DispatcherTimer _visibilityTimer;

    public BoardInteractionController(NoteService notes, GroupSelectionSession selection, DesktopClickObserver clicks,
        Func<Guid, NoteWindow> getWindow, Dispatcher dispatcher)
    {
        _notes = notes; _selection = selection; _clicks = clicks; _getWindow = getWindow; _dispatcher = dispatcher;
        _linkSelection = new LinkSelectionSession(notes);
        _linkSelection.Changed += RefreshArrowSelection;
        notes.Changed += ScheduleRefresh;
        selection.StateChanged += OnSelectionChanged;
        clicks.UnmodifiedClick += OnDesktopClick;
        // Win+D and minimizing a note can change native visibility without a WPF visibility event.
        _visibilityTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => { if (_arrows.Count > 0) RefreshArrows(); }, dispatcher);
        _visibilityTimer.Start();
    }

    public void Attach(NoteWindow window)
    {
        _windows.Add(window.NoteId, window);
        window.BeginDrag = BeginDrag;
        window.Moving = OnMoving;
        window.EndDrag = EndDrag;
        window.ControlClicked = OnControlClick;
        window.ConnectionClicked = SelectConnectionNote;
        window.InteractionDeactivated = OnInteractionDeactivated;
        window.CommitSelection = _selection.Commit;
        window.PopulateBoardMenu = PopulateMenu;
        window.LinkDragStarted = BeginLink;
        window.LinkDragUpdated = UpdateLinkPreview;
        window.LinkDragCompleted = CompleteLink;
        window.CancelInteraction = CancelInteraction;
        window.GeometryChanged += ScheduleRefresh;
        ScheduleRefresh();
    }

    public void Detach(NoteWindow window)
    {
        window.GeometryChanged -= ScheduleRefresh;
        _windows.Remove(window.NoteId);
        if (_connectionNote == window.NoteId) SelectConnectionNote(null);
        if (_linkSource == window || _linkTarget == window) CancelLink();
        ScheduleRefresh();
    }

    private void OnControlClick(NoteWindow window)
    {
        SelectConnectionNote(null);
        var warning = _selection.Toggle(window.NoteId);
        if (warning is not null) MessageBox.Show(window, warning, "DesktopNotes — группы");
    }

    private void SelectConnectionNote(NoteWindow? window)
    {
        _connectionNote = window?.NoteId;
        if (window is not null) _linkSelection.Clear();
        RefreshFrames();
    }

    private void OnInteractionDeactivated(NoteWindow window) => _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        if (!_disposed && _linkSource is null && _connectionNote == window.NoteId && NativeWindows.ForegroundOwner() != window.Handle)
            SelectConnectionNote(null);
    });

    private void OnDesktopClick() => _dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
    {
        if (!_disposed) _selection.Commit();
    });

    private void OnSelectionChanged()
    {
        if (_selection.IsActive)
        {
            try { _clicks.Start(); }
            catch (Win32Exception error)
            {
                MessageBox.Show("Не удалось отслеживать завершение выбора вне стикеров. Кликните по стикеру без Ctrl для завершения.\n" + error.Message, "DesktopNotes");
            }
        }
        else _clicks.Stop();
        ScheduleRefresh();
    }

    private bool CanMove(Guid id)
    {
        var group = _notes.GroupOf(id);
        return group is null ? !_notes.Get(id).IsPinned : group.NoteIds.All(member => !_notes.Get(member).IsPinned);
    }

    private bool BeginDrag(NoteWindow source)
    {
        if (!CanMove(source.NoteId)) return false;
        var ids = _notes.GroupOf(source.NoteId)?.NoteIds ?? [source.NoteId];
        _dragOrigins = ids.ToDictionary(id => id, id => NativeWindows.Rect(_getWindow(id).Handle));
        _leader = source.NoteId;
        _dragScale = NativeWindows.Scale(source.Handle);
        return true;
    }

    private ScreenRect OnMoving(NoteWindow source, ScreenRect proposed)
    {
        if (_moving) return proposed;
        if (!CanMove(source.NoteId)) return NativeWindows.Rect(source.Handle);
        if (_leader != source.NoteId && !BeginDrag(source)) return NativeWindows.Rect(source.Handle);
        var origins = _dragOrigins!;
        var original = origins[source.NoteId];
        var dx = proposed.Left - original.Left;
        var dy = proposed.Top - original.Top;
        var moving = origins.Where(p => !_notes.Get(p.Key).IsHidden)
            .Select(p => p.Key == source.NoteId ? proposed.Bounds : p.Value.Bounds.Offset(dx, dy)).ToArray();
        var targets = _windows.Where(p => !origins.ContainsKey(p.Key) && p.Value.IsVisible && NativeWindows.IsShown(p.Value.Handle))
            .Select(p => NativeWindows.Rect(p.Value.Handle).Bounds).ToArray();
        var snap = SnapCalculator.GetDragOffset(moving, targets, _dragScale);
        var adjusted = proposed.Offset((int)Math.Round(snap.X), (int)Math.Round(snap.Y));
        dx = adjusted.Left - original.Left;
        dy = adjusted.Top - original.Top;
        _moving = true;
        try
        {
            foreach (var (id, rect) in origins)
                if (id != source.NoteId) _windows[id].MovePhysical(rect.Left + dx, rect.Top + dy);
        }
        finally { _moving = false; }
        ScheduleRefresh();
        return adjusted;
    }

    private void EndDrag(NoteWindow source)
    {
        source.SaveBounds();
        if (_dragOrigins is not null)
        {
            var positions = _dragOrigins.Keys.ToDictionary(id => id, id => (_notes.Get(id).X, _notes.Get(id).Y));
            _notes.MoveNotes(positions);
        }
        _dragOrigins = null;
        _leader = null;
        ScheduleRefresh();
    }

    private void PopulateMenu(NoteWindow window, ContextMenu menu)
    {
        var group = _notes.GroupOf(window.NoteId);
        if (group is not null)
        {
            var color = new MenuItem { Header = "Цвет рамки группы…" };
            color.Click += (_, _) =>
            {
                using var picker = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.ColorTranslator.FromHtml(group.BorderColor) };
                if (picker.ShowDialog() == Forms.DialogResult.OK)
                    _notes.SetGroupColor(group.Id, $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}");
            };
            menu.Items.Add(color);
            var dissolve = new MenuItem { Header = "Распустить группу" };
            dissolve.Click += (_, _) => _notes.SetGroupMembers(group.Id, []);
            menu.Items.Add(dissolve);
            if (!CanMove(window.NoteId)) menu.Items.Add(new MenuItem { Header = "Группа не двигается: участник закреплён", IsEnabled = false });
        }
        var links = _notes.Links.Where(link => link.SourceId == window.NoteId || link.TargetId == window.NoteId).ToArray();
        if (links.Length > 0)
        {
            var submenu = new MenuItem { Header = "Удалить связь" };
            foreach (var link in links)
            {
                var outgoing = link.SourceId == window.NoteId;
                var target = _notes.Get(outgoing ? link.TargetId : link.SourceId);
                var title = string.IsNullOrWhiteSpace(target.Title) ? "Без названия" : target.Title;
                var item = new MenuItem { Header = (outgoing ? "→ " : "← ") + title };
                item.Click += (_, _) => _notes.RemoveLink(link.Id);
                submenu.Items.Add(item);
            }
            menu.Items.Add(submenu);
        }
    }

    private void BeginLink(NoteWindow source, NoteSide side)
    {
        CancelLink();
        _linkSelection.Clear();
        SelectConnectionNote(source);
        _linkSource = source;
        _linkPreview = new ArrowOverlay();
        Mouse.OverrideCursor = Cursors.Cross;
        UpdateLinkPreview();
    }

    private NoteWindow? DropTarget()
    {
        var root = NativeWindows.RootOwnerUnderCursor();
        var visible = _windows.Values.Where(window => window != _linkSource && window.IsVisible && NativeWindows.IsShown(window.Handle)).ToArray();
        var direct = visible.FirstOrDefault(window => window.Handle == root);
        if (direct is not null) return direct;
        if (!NativeWindows.IsOwnWindow(root) && !NativeWindows.IsDesktopSurface(root)) return null;
        var cursor = NativeWindows.Cursor();
        if (root == _linkSource!.Handle && ConnectionTargeting.Distance(NativeWindows.Rect(_linkSource.Handle).Bounds, new(cursor.X, cursor.Y)) == 0) return null;
        var id = ConnectionTargeting.FindClosest(visible.Select(window => (window.NoteId, NativeWindows.Rect(window.Handle).Bounds)),
            new(cursor.X, cursor.Y), 18 * NativeWindows.Scale(_linkSource!.Handle));
        return id is { } targetId ? _windows[targetId] : null;
    }

    private void UpdateLinkPreview()
    {
        if (_linkSource is null || _linkPreview is null) return;
        _linkTarget = DropTarget();
        var source = NativeWindows.Rect(_linkSource.Handle).Bounds;
        IReadOnlyList<DiagramPoint> points;
        if (_linkTarget is not null) points = ArrowRouter.Route(source, NativeWindows.Rect(_linkTarget.Handle).Bounds, clearance: 32 * NativeWindows.Scale(_linkSource.Handle));
        else
        {
            var cursor = NativeWindows.Cursor();
            var end = new DiagramPoint(cursor.X, cursor.Y);
            points = ArrowRouter.FromSide(source, end, ArrowRouter.NearestSide(source, end), 32 * NativeWindows.Scale(_linkSource.Handle));
        }
        _linkPreview.Draw(points, new LinkStyle(), _linkSource.Handle, _linkTarget?.Handle ?? _linkSource.Handle);
        RefreshFrames();
    }

    private void CompleteLink()
    {
        if (_linkSource is null) return;
        var target = DropTarget();
        if (target is not null) _notes.AddLink(_linkSource.NoteId, target.NoteId);
        CancelLink();
    }

    private void CancelLink()
    {
        _linkPreview?.Close();
        _linkPreview = null;
        _linkSource = null;
        _linkTarget = null;
        Mouse.OverrideCursor = null;
        RefreshFrames();
        ScheduleRefresh();
    }

    private bool CancelInteraction()
    {
        var active = _linkSource is not null || _selection.IsActive || _connectionNote is not null || _linkSelection.SelectedId is not null;
        if (_linkSource is not null) CancelLink();
        _selection.Cancel();
        _linkSelection.Clear();
        SelectConnectionNote(null);
        return active;
    }

    private void ScheduleRefresh()
    {
        if (_refreshPending || _disposed) return;
        _refreshPending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _refreshPending = false;
            if (_disposed) return;
            RefreshFrames();
            RefreshArrows();
        });
    }

    private void RefreshFrames()
    {
        foreach (var (id, window) in _windows)
        {
            if (!_notes.Contains(id)) continue;
            var group = _notes.GroupOf(id);
            var selected = _selection.IsActive && _selection.Members.Contains(id);
            var color = group?.BorderColor;
            if (_selection.IsActive && group?.Id == _selection.TargetGroupId && !selected) color = null;
            if (selected) color = _selection.TargetGroupId is { } target
                ? _notes.Groups.FirstOrDefault(g => g.Id == target)?.BorderColor ?? "#F59E0B" : "#F59E0B";
            window.SetGroupAppearance(color, selected, CanMove(id));
            window.SetConnectionState(_connectionNote == id, window == _linkTarget);
        }
    }

    private void RefreshArrows()
    {
        if (_disposed) return;
        var links = _notes.Links;
        if (_linkSelection.SelectedId is { } selected && !links.Any(link => link.Id == selected)) _linkSelection.Clear();
        foreach (var id in _arrows.Keys.Except(links.Select(l => l.Id)).ToArray()) { _arrows[id].Close(); _arrows.Remove(id); }
        foreach (var link in links)
        {
            if (!_windows.TryGetValue(link.SourceId, out var source) || !_windows.TryGetValue(link.TargetId, out var target) ||
                !source.IsVisible || !target.IsVisible || !NativeWindows.IsShown(source.Handle) || !NativeWindows.IsShown(target.Handle))
            {
                if (_arrows.TryGetValue(link.Id, out var hidden)) hidden.Hide();
                if (_linkSelection.SelectedId == link.Id) _linkSelection.Clear();
                continue;
            }
            if (!_arrows.TryGetValue(link.Id, out var arrow))
            {
                arrow = new ArrowOverlay(interactive: true);
                arrow.SelectRequested += () => { SelectConnectionNote(null); _linkSelection.Select(link.Id); };
                arrow.DeleteRequested += () => { if (_linkSelection.SelectedId == link.Id) _linkSelection.DeleteSelected(); };
                arrow.DeselectRequested += () => { if (_linkSelection.SelectedId == link.Id) _linkSelection.Clear(); };
                _arrows.Add(link.Id, arrow);
            }
            arrow.Draw(ArrowRouter.Route(NativeWindows.Rect(source.Handle).Bounds, NativeWindows.Rect(target.Handle).Bounds, clearance: 32 * NativeWindows.Scale(source.Handle)),
                link.Style, source.Handle, target.Handle);
            arrow.SetSelected(_linkSelection.SelectedId == link.Id);
        }
    }

    private void RefreshArrowSelection()
    {
        foreach (var (id, arrow) in _arrows) arrow.SetSelected(_linkSelection.SelectedId == id);
    }

    public void CommitSelection() => _selection.Commit();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _visibilityTimer.Stop();
        _linkSelection.Changed -= RefreshArrowSelection;
        _notes.Changed -= ScheduleRefresh;
        _selection.StateChanged -= OnSelectionChanged;
        _clicks.UnmodifiedClick -= OnDesktopClick;
        _clicks.Stop();
        CancelLink();
        foreach (var arrow in _arrows.Values) arrow.Close();
        _arrows.Clear();
    }
}
