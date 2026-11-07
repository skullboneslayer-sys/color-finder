using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace ColorFinder;

internal sealed class PaletteDrawer : Panel
{
    public const string SwatchFormat = "ColorFinder.Swatch";
    internal const string MoveFormat = "ColorFinder.Swatch.Move";

    private readonly SavedColorStore store;
    private readonly Action<ColorValue> recall;
    private readonly Action<string> reportError;
    private readonly List<RecentColorButton> swatches = new();
    private readonly Panel viewport = new() { AllowDrop = true, BackColor = Theme.Surface };
    private readonly SwatchMenu swatchMenu = new();
    private readonly System.Windows.Forms.Timer dragScroll = new() { Interval = 150 };
    private readonly System.Windows.Forms.Timer savedRefresh = new() { Interval = 500 };
    private bool applyingColors;
    private bool acceptingDrop;
    private RecentColorButton? movingSwatch;
    private RecentColorButton? moveTarget;
    private int scrollDirection;
    private Rectangle well;
    private int columns = 2;
    private int visibleRows = 1;
    private int firstRow;
    private int side = 75;
    private int gap = 10;

    public PaletteDrawer(SavedColorStore store, Action<ColorValue> recall, Action<string> reportError)
    {
        this.store = store;
        this.recall = recall;
        this.reportError = reportError;

        AccessibleName = "Saved swatches";
        BackColor = Theme.Background;
        DoubleBuffered = true;
        ResizeRedraw = true;
        AllowDrop = true;
        DragEnter += SetDropEffect;
        DragOver += SetDropEffect;
        DragLeave += (_, _) => ClearDropPreview();
        DragDrop += DropSwatch;
        viewport.DragEnter += SetDropEffect;
        viewport.DragOver += SetDropEffect;
        viewport.DragLeave += (_, _) => ClearDropPreview();
        viewport.DragDrop += DropSwatch;
        viewport.MouseWheel += (_, e) => ScrollRows(e);
        viewport.Paint += DrawMoveTarget;
        dragScroll.Tick += (_, _) => ScrollDuringMove();
        savedRefresh.Tick += (_, _) => RefreshSavedColors();
        VisibleChanged += (_, _) => savedRefresh.Enabled = Visible;
        Controls.Add(viewport);
        Resize += (_, _) => UpdateScale();

        var removeColor = swatchMenu.Items.Add("Remove color");
        swatchMenu.Opening += (_, _) => removeColor.Tag = swatchMenu.SourceControl;
        removeColor.Click += (_, _) =>
        {
            if (removeColor.Tag is RecentColorButton swatch) RemoveSwatch(swatch);
        };
    }

    public void LoadPinnedColors()
    {
        if (LoadColors()) SetFirstRow(firstRow);
    }

    private bool LoadColors()
    {
        try
        {
            ApplyColors(store.Load());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reportError("Could not load saved colors. Reopen the drawer to retry.");
            return false;
        }
    }

    private void RefreshSavedColors()
    {
        // Never rearrange a swatch underneath a click, drag, or open removal menu.
        if (!Visible || applyingColors || movingSwatch is not null || acceptingDrop ||
            swatchMenu.Visible || MouseButtons != MouseButtons.None) return;
        try
        {
            if (store.TryLoad(out var colors)) ApplyColors(colors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the displayed colors and try again on the next timer tick.
        }
    }

    private bool ApplyColors(IReadOnlyList<ColorValue> snapshot)
    {
        var unchanged = snapshot.Count == swatches.Count;
        for (var i = 0; unchanged && i < snapshot.Count; i++)
            unchanged = snapshot[i] == swatches[i].Value;
        if (unchanged) return false;

        applyingColors = true;
        viewport.SuspendLayout();
        try
        {
            ClearMovePreview();
            var existing = new Dictionary<ColorValue, RecentColorButton>();
            foreach (var swatch in swatches) existing.Add(swatch.Value, swatch);
            var ordered = new List<RecentColorButton>(snapshot.Count);
            foreach (var color in snapshot)
            {
                if (existing.TryGetValue(color, out var swatch)) existing.Remove(color);
                else swatch = AddSwatch(color);
                ordered.Add(swatch);
            }
            swatches.Clear();
            swatches.AddRange(ordered);
            foreach (var removed in existing.Values)
            {
                // Keep an OLE drag source alive until DoDragDrop has returned.
                if (removed == movingSwatch) removed.Visible = false;
                else removed.Dispose();
            }
            for (var i = 0; i < swatches.Count; i++) viewport.Controls.SetChildIndex(swatches[i], i);
            SetFirstRow(firstRow);
        }
        finally
        {
            viewport.ResumeLayout();
            applyingColors = false;
        }
        return true;
    }

    private void SetDropEffect(object? sender, DragEventArgs e)
    {
        if (IsLocalMove(e))
        {
            e.Effect = DragDropEffects.Move;
            ShowMovePreview(viewport.PointToClient(new Point(e.X, e.Y)));
            return;
        }

        ClearMovePreview();
        var accepts = TryReadSwatch(e, out _);
        e.Effect = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(accepts);
    }

    private void SetDropHighlight(bool active)
    {
        if (acceptingDrop == active) return;
        acceptingDrop = active;
        Invalidate();
    }

    private static bool TryReadSwatch(DragEventArgs e, out ColorValue color)
    {
        color = default;
        return (e.AllowedEffect & DragDropEffects.Copy) != 0 &&
               e.Data?.GetDataPresent(SwatchFormat, autoConvert: false) == true &&
               e.Data.GetData(SwatchFormat, autoConvert: false) is string hex &&
               hex.Length == 7 && hex[0] == '#' && ColorValue.TryParse(hex, out color) &&
               hex == color.Hex;
    }

    private bool IsLocalMove(DragEventArgs e) =>
        movingSwatch is { } source &&
        (e.AllowedEffect & DragDropEffects.Move) != 0 &&
        e.Data?.GetDataPresent(MoveFormat, autoConvert: false) == true &&
        e.Data.GetData(MoveFormat, autoConvert: false) is string hex && hex == source.Value.Hex;

    private RecentColorButton? FindMoveTarget(Point point)
    {
        if (swatches.Count == 0) return null;
        var step = side + gap;
        var column = Math.Max(0, Math.Min(columns - 1, (point.X - GridLeft + gap / 2) / step));
        // A clipped last row is still a visible drop target.
        var intersectingRows = Math.Max(1, (viewport.ClientSize.Height + step - 1) / step);
        var row = Math.Max(0, Math.Min(intersectingRows - 1, (point.Y + gap / 2) / step));
        return swatches[Math.Min(swatches.Count - 1, (firstRow + row) * columns + column)];
    }

    private void ShowMovePreview(Point point)
    {
        var target = FindMoveTarget(point);
        if (target == movingSwatch) target = null;
        if (target != moveTarget)
        {
            moveTarget = target;
            viewport.Invalidate();
        }
        SetDropHighlight(true);

        var edge = Math.Min(Theme.Dip(this, 24), viewport.Height / 4);
        scrollDirection = point.Y < edge && firstRow > 0 ? -1 :
            point.Y >= viewport.Height - edge && firstRow + visibleRows < RowCount ? 1 : 0;
        dragScroll.Enabled = scrollDirection != 0;
    }

    private void ScrollDuringMove()
    {
        var pointer = Cursor.Position;
        if (movingSwatch is null || !Visible || !ClientRectangle.Contains(PointToClient(pointer)))
        {
            ClearDropPreview();
            return;
        }

        SetFirstRow(firstRow + scrollDirection);
        ShowMovePreview(viewport.PointToClient(pointer));
    }

    private void DrawMoveTarget(object? sender, PaintEventArgs e)
    {
        if (moveTarget is not { } target) return;
        var bounds = Rectangle.Inflate(target.Bounds, Theme.Dip(this, 2), Theme.Dip(this, 2));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Theme.RoundedRectangle(bounds, Theme.Dip(this, 11));
        using var border = new Pen(Theme.Accent, Theme.Dip(this, 2));
        e.Graphics.DrawPath(border, shape);
    }

    private void ClearMovePreview()
    {
        dragScroll.Stop();
        scrollDirection = 0;
        if (moveTarget is null) return;
        moveTarget = null;
        viewport.Invalidate();
    }

    private void ClearDropPreview()
    {
        ClearMovePreview();
        SetDropHighlight(false);
    }

    private void DropSwatch(object? sender, DragEventArgs e)
    {
        ClearDropPreview();
        e.Effect = DragDropEffects.None;
        if (IsLocalMove(e) && movingSwatch is { } source)
        {
            var target = FindMoveTarget(viewport.PointToClient(new Point(e.X, e.Y)));
            if (target is not null && MoveSwatch(source, target)) e.Effect = DragDropEffects.Move;
            return;
        }

        if (!TryReadSwatch(e, out var color)) return;
        try
        {
            if (ApplyColors(store.Save(color))) SetFirstRow(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reportError("Could not pin this color. Drop it again to retry.");
            return;
        }
        e.Effect = DragDropEffects.Copy;
    }

    private bool MoveSwatch(RecentColorButton source, RecentColorButton target)
    {
        var sourceIndex = swatches.IndexOf(source);
        var targetIndex = swatches.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0) return false;
        try
        {
            var snapshot = store.Move(source.Value, target.Value, out var moved);
            ApplyColors(snapshot);
            if (!moved)
            {
                reportError("Saved colors changed. Try the move again.");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reportError("Could not move this color. Drop it again to retry.");
            return false;
        }

        return true;
    }

    private void StartMove(RecentColorButton swatch)
    {
        var data = new DataObject();
        data.SetData(MoveFormat, false, swatch.Value.Hex);
        movingSwatch = swatch;
        try { swatch.DoDragDrop(data, DragDropEffects.Move); }
        finally
        {
            movingSwatch = null;
            ClearDropPreview();
            if (!swatches.Contains(swatch)) swatch.Dispose();
        }
    }

    private RecentColorButton AddSwatch(ColorValue color)
    {
        var swatch = new RecentColorButton(color, cornerRadius: 9) { AllowDrop = true, ContextMenuStrip = swatchMenu };
        swatch.Click += (_, _) => recall(color);
        swatch.Enter += (_, _) => RevealSwatch(swatch);
        swatch.MouseWheel += (_, e) => ScrollRows(e);
        swatch.DragEnter += SetDropEffect;
        swatch.DragOver += SetDropEffect;
        swatch.DragLeave += (_, _) => ClearDropPreview();
        swatch.DragDrop += DropSwatch;

        Point? dragStart = null;
        swatch.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) dragStart = swatch.PointToScreen(e.Location);
        };
        swatch.MouseUp += (_, _) => dragStart = null;
        swatch.MouseCaptureChanged += (_, _) => { if (!swatch.Capture) dragStart = null; };
        swatch.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || dragStart is not Point start || movingSwatch is not null) return;
            var size = SystemInformation.DragSize;
            var threshold = new Rectangle(start.X - size.Width / 2, start.Y - size.Height / 2, size.Width, size.Height);
            if (threshold.Contains(swatch.PointToScreen(e.Location))) return;

            dragStart = null;
            swatch.Capture = false;
            StartMove(swatch);
        };

        viewport.Controls.Add(swatch);
        return swatch;
    }

    private void RemoveSwatch(RecentColorButton swatch)
    {
        if (!swatches.Contains(swatch)) return;
        try { ApplyColors(store.Remove(swatch.Value)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reportError("Could not remove this color. Try again.");
            return;
        }

    }

    private int RowCount => (swatches.Count + columns - 1) / columns;
    private int GridLeft => Math.Max(0, (viewport.ClientSize.Width - columns * (side + gap) + gap) / 2);

    public void UpdateScale()
    {
        int edge = Math.Min(Theme.Dip(this, 12), ClientSize.Width / 4);
        int top = Math.Min(Theme.Dip(this, 16), ClientSize.Height / 4);
        well = new Rectangle(edge, top, ClientSize.Width - 2 * edge, ClientSize.Height - 2 * top);
        int inset = Math.Min(Theme.Dip(this, 10), Math.Min(well.Width, well.Height) / 4);
        viewport.Bounds = Rectangle.Inflate(well, -inset, -inset);

        gap = Math.Max(1, Theme.Dip(this, 10));
        int width = viewport.ClientSize.Width;
        int height = viewport.ClientSize.Height;
        columns = width >= 2 * Theme.Dip(this, 54) + gap ? 2 : 1;
        var availableSide = (width - (columns - 1) * gap) / columns;
        side = Math.Max(1, Math.Min(Theme.Dip(this, 75), Math.Min(availableSide, height)));
        var step = side + gap;
        visibleRows = Math.Max(1, (height + gap) / step);
        SetFirstRow(firstRow);
    }

    private void LayoutSwatches()
    {
        var step = side + gap;
        int left = GridLeft;
        viewport.SuspendLayout();
        for (int i = 0; i < swatches.Count; i++)
        {
            var chip = swatches[i];
            chip.TabIndex = i;
            chip.Bounds = new Rectangle(left + i % columns * step, (i / columns - firstRow) * step, side, side);
        }
        viewport.ResumeLayout();
    }

    private void SetFirstRow(int row)
    {
        firstRow = Math.Max(0, Math.Min(row, RowCount - visibleRows));
        LayoutSwatches();
    }

    private void RevealSwatch(RecentColorButton swatch)
    {
        if (applyingColors || movingSwatch is not null || MouseButtons != MouseButtons.None) return;
        int row = swatches.IndexOf(swatch) / columns;
        if (row < firstRow) SetFirstRow(row);
        else if (row >= firstRow + visibleRows) SetFirstRow(row - visibleRows + 1);
    }

    private void ScrollRows(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs { Handled: true } || e.Delta == 0 || RowCount <= visibleRows) return;
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        SetFirstRow(firstRow - Math.Sign(e.Delta) * Math.Max(1, Math.Abs(e.Delta) / 120));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollRows(e);
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (well.Width < 2 || well.Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Theme.RoundedRectangle(new RectangleF(well.X, well.Y, well.Width - 1, well.Height - 1), Theme.Dip(this, 12));
        using var fill = new SolidBrush(Theme.Surface);
        using var border = new Pen(acceptingDrop ? Theme.Accent : Theme.Border, Math.Max(1, Theme.Dip(this, 1)));
        e.Graphics.FillPath(fill, shape);
        e.Graphics.DrawPath(border, shape);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.PageDown) { SetFirstRow(firstRow + visibleRows); return true; }
        if (keyData == Keys.PageUp) { SetFirstRow(firstRow - visibleRows); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dragScroll.Dispose();
            savedRefresh.Dispose();
            swatchMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}
