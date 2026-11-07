using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ColorFinder;

internal sealed class MainForm : Form
{
    private const int HotkeyId = 0xCF1;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmDpiChanged = 0x02E0;
    private const int ChipSize = 24;
    private const int ChipSpacing = 6;
    private const int RecentLimit = 12;
    private const int PaletteWidth = 204;
    internal static readonly string[] DisplayFormatKeys = ["hex", "rgb", "hsl", "hsv", "cmyk", "normalized", "linear"];
    private readonly SwatchCard swatch = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label source = Theme.Label("Choose a pixel anywhere on your displays.", 9, color: Theme.Muted);
    private readonly Label shortcut = Theme.Label("", 8.5f, color: Theme.Muted);
    private readonly Button pick;
    private readonly Dictionary<string, ValueButton> values = new();
    private readonly PaletteDrawer palette;
    private bool paletteOpen;
    private Action<bool>? setPaletteOpen;
    private readonly List<ColorValue> recentColors = [];
    private readonly FlowLayoutPanel recentStrip = new()
    {
        Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty,
        WrapContents = false, AutoScroll = true, AccessibleName = "Recent colors"
    };
    private readonly ToolTip tips = new() { AutoPopDelay = 8000 };
    private bool picking;
    private bool registered;
    private Action<int, Rectangle?>? resizeForDpi;
    private Action? checkWorkArea;
    private bool inSizeMove;
    internal ColorValue CurrentColor { get; private set; } = new(131, 231, 189);
    internal IReadOnlyList<ColorValue> RecentColors => recentColors;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);

    internal MainForm(SavedColorStore paletteColors)
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Text = "Color Finder";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        ClientSize = new Size(460, 655);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        var viewport = new Panel { Dock = DockStyle.Fill };
        Controls.Add(viewport);
        palette = new PaletteDrawer(paletteColors, RecallColor, message => ReportError(message))
        {
            Dock = DockStyle.Left,
            Width = PaletteWidth,
            Visible = false
        };
        Controls.Add(palette);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = ClientSize.Height, ColumnCount = 1, RowCount = 9,
            Padding = new Padding(20, 16, 20, 14), Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        int[] logicalRows = [44, 44, 26, 0, 29, 8, 294, 54, 22];
        foreach (var height in logicalRows)
            root.RowStyles.Add(height == 0 ? new(SizeType.Percent, 100) : new(SizeType.Absolute, height));
        viewport.Controls.Add(root);
        var heading = Theme.Label("Color Finder", 22, true);
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1
        };
        header.ColumnStyles.Add(new(SizeType.Absolute, 34));
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.RowStyles.Add(new(SizeType.Percent, 100));
        var paletteToggle = new DrawerToggle { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 8, 8) };
        paletteToggle.Click += (_, _) => setPaletteOpen?.Invoke(!paletteOpen);
        header.Controls.Add(paletteToggle, 0, 0);
        header.Controls.Add(heading, 1, 0);
        root.Controls.Add(header, 0, 0);
        pick = Theme.MakeButton("Find color", async (_, _) => await PickColor(), true);
        pick.BackColor = Color.Purple;
        pick.ForeColor = Color.White;
        pick.Dock = DockStyle.Fill;
        pick.AccessibleName = "Find color";
        pick.AccessibleDescription = "Select one pixel anywhere on your displays.";
        root.Controls.Add(pick, 0, 1);
        shortcut.TextAlign = ContentAlignment.MiddleCenter;
        root.Controls.Add(shortcut, 0, 2);
        root.Controls.Add(swatch, 0, 3);
        source.AccessibleName = "Pixel location";
        root.Controls.Add(source, 0, 4);
        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = DisplayFormatKeys.Length,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        rows.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var key in DisplayFormatKeys)
        {
            rows.RowStyles.Add(new(SizeType.Percent, 100f / DisplayFormatKeys.Length));
            var button = new ValueButton { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 5) };
            button.Click += (_, _) => CopyValue(key);
            values.Add(key, button);
            rows.Controls.Add(button, 0, rows.Controls.Count);
        }
        root.Controls.Add(rows, 0, 6);
        root.Controls.Add(recentStrip, 0, 7);
        var credit = Theme.Label("Made by Skullboneslayer", 8.5f, color: Theme.Muted);
        credit.TextAlign = ContentAlignment.MiddleCenter;
        root.Controls.Add(credit, 0, 8);
        recentStrip.AccessibleDescription = "Drag a swatch into the drawer on the left to save it.";
        recentStrip.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) OpenPalette(); };
        tips.SetToolTip(paletteToggle, "Saved colors. Drag a recent swatch into the drawer to save it.");
        tips.SetToolTip(recentStrip, recentStrip.AccessibleDescription);
        tips.SetToolTip(swatch, "The exact captured screen pixel. Screens show composited RGB, so source transparency is unknown.");
        ResumeLayout(true);
        var fontPoints = new Control[] { heading, source, shortcut, pick, credit }
            .Concat(values.Values)
            .ToDictionary(control => control, control => control.Font.SizeInPoints);
        var layoutDpi = 0;
        var resizing = false;
        Rectangle? fittedWorkArea = null;
        var workAreaFitQueued = false;
        var fitNeededAfterDrag = false;

        Size MinimumContentSize()
        {
            var fixedRows = root.RowStyles.Cast<RowStyle>()
                .Where(row => row.SizeType == SizeType.Absolute)
                .Sum(row => (int)Math.Ceiling(row.Height));
            var width = Theme.Dip(this, 410);
            var height = root.Padding.Vertical + fixedRows + Theme.Dip(this, 64);
            return new Size(width, height);
        }

        void SizeContent()
        {
            var minimum = MinimumContentSize();
            if (viewport.AutoScroll) viewport.AutoScrollMinSize = minimum;
            root.Height = Math.Max(viewport.ClientSize.Height, minimum.Height);
        }

        int DrawerWidth(Rectangle area) => Math.Min(Theme.Dip(this, PaletteWidth), Math.Max(1, area.Width / 3));

        void FitWindowToWorkArea(bool center, Rectangle? workArea = null)
        {
            var area = workArea ?? Screen.FromControl(this).WorkingArea;
            palette.Width = DrawerWidth(area);
            var originalBounds = Bounds;
            var maximumWidth = Math.Max(1, area.Width - 16);
            var maximumHeight = Math.Max(1, area.Height - 16);
            var content = MinimumContentSize();
            if (paletteOpen) content.Width += palette.Width;
            var minimum = WindowState == FormWindowState.Normal
                ? new Size(content.Width + Width - ClientSize.Width, content.Height + Height - ClientSize.Height)
                : SizeFromClientSize(content);
            var needsScroll = minimum.Width > maximumWidth || minimum.Height > maximumHeight;
            if (!needsScroll && viewport.AutoScrollMinSize != Size.Empty)
                viewport.AutoScrollMinSize = Size.Empty;
            viewport.AutoScroll = needsScroll;
            MinimumSize = new Size(Math.Min(minimum.Width, maximumWidth), Math.Min(minimum.Height, maximumHeight));

            if (WindowState == FormWindowState.Normal)
            {
                var size = new Size(Math.Min(Math.Max(originalBounds.Width, MinimumSize.Width), maximumWidth),
                    Math.Min(Math.Max(originalBounds.Height, MinimumSize.Height), maximumHeight));
                var location = originalBounds.Location;
                if (center)
                {
                    location = new Point(area.Left + (area.Width - size.Width) / 2,
                        area.Top + (area.Height - size.Height) / 2);
                }
                else if (size != originalBounds.Size || needsScroll)
                {
                    location = new Point(
                        Math.Max(area.Left, Math.Min(location.X, area.Right - size.Width)),
                        Math.Max(area.Top, Math.Min(location.Y, area.Bottom - size.Height)));
                }

                var fittedBounds = new Rectangle(location, size);
                if (Bounds != fittedBounds) Bounds = fittedBounds;
            }

            fittedWorkArea = area;
            fitNeededAfterDrag = false;
            SizeContent();
        }

        void SetPaletteOpen(bool open)
        {
            if (paletteOpen == open)
            {
                if (open) palette.LoadPinnedColors();
                return;
            }

            var area = Screen.FromControl(this).WorkingArea;
            var original = Bounds;
            var width = DrawerWidth(area);
            var delta = open ? width : -palette.Width;
            resizing = true;
            try
            {
                paletteOpen = open;
                if (!open && palette.ContainsFocus) paletteToggle.Focus();
                palette.Width = width;
                palette.Visible = open;
                paletteToggle.SetExpanded(open);
                if (WindowState == FormWindowState.Normal)
                {
                    MinimumSize = Size.Empty;
                    var windowWidth = Math.Max(1, Math.Min(original.Width + delta, area.Width - 16));
                    var left = Math.Max(area.Left, Math.Min(original.Right - windowWidth, area.Right - windowWidth));
                    Bounds = new Rectangle(left, original.Top, windowWidth, original.Height);
                }
                FitWindowToWorkArea(center: false, workArea: area);
                if (open) palette.LoadPinnedColors();
            }
            finally { resizing = false; }
        }

        bool NeedsWorkAreaFit() =>
            fittedWorkArea.HasValue && !resizing && !inSizeMove && !IsDisposed && IsHandleCreated &&
            Visible && WindowState != FormWindowState.Minimized &&
            (fitNeededAfterDrag || Screen.FromControl(this).WorkingArea != fittedWorkArea.Value);

        void QueueWorkAreaFit()
        {
            if (workAreaFitQueued || !NeedsWorkAreaFit()) return;
            workAreaFitQueued = true;
            BeginInvoke((Action)(() =>
            {
                workAreaFitQueued = false;
                if (!NeedsWorkAreaFit()) return;
                resizing = true;
                try { FitWindowToWorkArea(center: false); }
                finally { resizing = false; }
            }));
        }

        void ApplyDpiLayout(int dpi, Rectangle? suggestedBounds)
        {
            if (resizing) return;
            resizing = true;
            try
            {
                if (dpi != layoutDpi)
                {
                    layoutDpi = dpi;
                    root.SuspendLayout();
                    try
                    {
                        // Set sizes from the original metrics so rounding never accumulates.
                        root.Padding = new Padding(Theme.Dip(this, 20), Theme.Dip(this, 16),
                            Theme.Dip(this, 20), Theme.Dip(this, 14));
                        header.ColumnStyles[0].Width = Theme.Dip(this, 34);
                        var toggleGap = Theme.Dip(this, 8);
                        paletteToggle.Margin = new Padding(0, toggleGap, toggleGap, toggleGap);
                        palette.UpdateScale();
                        for (var row = 0; row < logicalRows.Length; row++)
                        {
                            if (logicalRows[row] != 0)
                                root.RowStyles[row].Height = Theme.Dip(this, logicalRows[row]);
                        }

                        foreach (var button in values.Values)
                            button.Margin = new Padding(0, 0, 0, Theme.Dip(this, 5));
                        foreach (RecentColorButton chip in recentStrip.Controls)
                            SizeRecentChip(chip);

                        foreach (var item in fontPoints)
                        {
                            var previous = item.Key.Font;
                            item.Key.Font = new Font(previous.FontFamily, item.Value * dpi / 72f,
                                previous.Style, GraphicsUnit.Pixel);
                            previous.Dispose();
                        }
                    }
                    finally
                    {
                        root.ResumeLayout(true);
                    }
                }

                MinimumSize = Size.Empty;
                if (WindowState == FormWindowState.Normal)
                {
                    if (suggestedBounds.HasValue)
                        Bounds = suggestedBounds.Value;
                    else
                        ClientSize = new Size(Theme.Dip(this, 460) + (paletteOpen ? palette.Width : 0), Theme.Dip(this, 655));
                }

                if (inSizeMove && suggestedBounds.HasValue)
                {
                    // Apply Windows' DPI bounds during the drag. Fit the work area after it ends.
                    fitNeededAfterDrag = true;
                    SizeContent();
                }
                else
                {
                    FitWindowToWorkArea(center: !suggestedBounds.HasValue);
                }
            }
            finally
            {
                resizing = false;
            }
        }

        resizeForDpi = ApplyDpiLayout;
        checkWorkArea = QueueWorkAreaFit;
        setPaletteOpen = SetPaletteOpen;
        viewport.ClientSizeChanged += (_, _) => { if (!resizing) SizeContent(); };
        SizeContent();
        UpdateValues();
        ActiveControl = pick;
        Shown += (_, _) => ApplyDpiLayout(Theme.WindowDpi(this), null);
        FormClosed += (_, _) => tips.Dispose();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
        registered = RegisterHotKey(Handle, HotkeyId, 0x4000 | 0x0002 | 0x0004, (uint)Keys.C);
        var key = "Ctrl + Shift + C";
        if (!registered)
        {
            registered = RegisterHotKey(Handle, HotkeyId, 0x4000 | 0x0002 | 0x0001, (uint)Keys.C);
            key = "Ctrl + Alt + C";
        }
        shortcut.Text = registered ? key + "  ·  or press Space here" : "Press Space here to find a color";
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (registered) UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmDpiChanged && resizeForDpi is not null && m.LParam != IntPtr.Zero)
        {
            var dpi = (int)(m.WParam.ToInt64() & 0xffff);
            var suggested = Rectangle.FromLTRB(
                Marshal.ReadInt32(m.LParam, 0), Marshal.ReadInt32(m.LParam, 4),
                Marshal.ReadInt32(m.LParam, 8), Marshal.ReadInt32(m.LParam, 12));
            resizeForDpi(dpi == 0 ? Theme.WindowDpi(this) : dpi, suggested);
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId)
        {
            if (!picking) _ = PickColor();
            return;
        }
        if (m.Msg == WmEnterSizeMove) inSizeMove = true;
        base.WndProc(ref m);
        if (m.Msg == WmExitSizeMove) inSizeMove = false;
        if (m.Msg is WmWindowPosChanged or WmExitSizeMove) checkWorkArea?.Invoke();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F8 || (keyData == Keys.Space && pick.Focused)) { _ = PickColor(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private async Task PickColor()
    {
        if (picking) return;
        picking = true;
        pick.Enabled = false;
        try
        {
            Hide();
            await Task.Delay(180);
            var pixel = await ScreenPicker.PickAsync(this);
            if (pixel is not null)
                ApplySample(new(pixel.Color.R, pixel.Color.G, pixel.Color.B), $"Pixel {pixel.Position.X}, {pixel.Position.Y}");
        }
        catch (Exception ex) { ReportError("Could not sample the screen. Try again.", ex.Message); }
        finally
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            picking = false;
            pick.Enabled = true;
            pick.Focus();
        }
    }
    internal void ApplySample(ColorValue color, string location)
    {
        CurrentColor = new(color.R, color.G, color.B);
        source.Text = location;
        tips.SetToolTip(source, "");
        recentColors.Remove(CurrentColor);
        recentColors.Insert(0, CurrentColor);
        var chip = recentStrip.Controls.OfType<RecentColorButton>().FirstOrDefault(item => item.Value == CurrentColor);
        chip ??= AddColorChip(CurrentColor);
        recentStrip.Controls.SetChildIndex(chip, 0);
        if (recentColors.Count > RecentLimit)
        {
            recentColors.RemoveAt(RecentLimit);
            recentStrip.Controls[RecentLimit].Dispose();
        }
        recentStrip.AutoScrollPosition = Point.Empty;
        UpdateValues();
    }

    private RecentColorButton AddColorChip(ColorValue color)
    {
        var chip = new RecentColorButton(color);
        SizeRecentChip(chip);
        tips.SetToolTip(chip, color.Hex + ". Drag into the saved-color drawer to save it.");
        chip.Click += (_, _) => RecallColor(color);

        Point? dragStart = null;
        chip.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) dragStart = chip.PointToScreen(e.Location);
        };
        chip.MouseCaptureChanged += (_, _) => { if (!chip.Capture) dragStart = null; };
        chip.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || dragStart is not Point start) return;
            var size = SystemInformation.DragSize;
            var threshold = new Rectangle(start.X - size.Width / 2, start.Y - size.Height / 2, size.Width, size.Height);
            if (threshold.Contains(chip.PointToScreen(e.Location))) return;

            dragStart = null;
            chip.Capture = false;
            OpenPalette();
            var data = new DataObject();
            data.SetData(PaletteDrawer.SwatchFormat, false, color.Hex);
            chip.DoDragDrop(data, DragDropEffects.Copy);
        };
        chip.MouseUp += (_, e) =>
        {
            dragStart = null;
            if (e.Button == MouseButtons.Right) OpenPalette();
        };
        recentStrip.Controls.Add(chip);
        return chip;
    }

    private void OpenPalette() => setPaletteOpen?.Invoke(true);

    private void RecallColor(ColorValue color)
    {
        CurrentColor = color;
        source.Text = "";
        tips.SetToolTip(source, "");
        UpdateValues();
    }
    private void SizeRecentChip(RecentColorButton chip)
    {
        var size = Theme.Dip(this, ChipSize);
        chip.Size = new Size(size, size);
        chip.Margin = new Padding(0, Theme.Dip(this, 4), Theme.Dip(this, ChipSpacing), 0);
    }

    private void UpdateValues()
    {
        swatch.Value = CurrentColor;
        swatch.Invalidate();
        var formats = CurrentColor.GetFormats();
        foreach (var key in DisplayFormatKeys)
        {
            var format = formats.Single(x => x.Key == key);
            var button = values[key];
            button.Caption = key switch { "normalized" => "RGB 0–1", "linear" => "Linear RGB", "cmyk" => "CMYK ≈", _ => format.Label };
            button.ValueText = DisplayValue(key, format.Value);
            button.AccessibleName = button.Caption + ": " + button.ValueText + ". Click to copy.";
            var note = key switch
            {
                "hex" => "Captured RGB in #RRGGBB.",
                "normalized" => "Encoded sRGB channels scaled to 0–1.",
                "linear" => "Linear-light RGB calculated assuming the captured channels are sRGB.",
                _ => format.Note
            };
            tips.SetToolTip(button, note + " Click to copy.");
            button.Invalidate();
        }
    }
    private static string DisplayValue(string key, string value)
    {
        if (key is "rgb" or "hsl" or "hsv" or "cmyk")
        {
            var start = value.IndexOf('(') + 1;
            return value.Substring(start, value.Length - start - 1);
        }
        if (key is "normalized" or "linear") return string.Join(",", value.Trim('(', ')').Split(',').Take(3));
        return value;
    }
    private void CopyValue(string key)
    {
        try
        {
            Clipboard.SetText(values[key].ValueText);
            source.Text = "Copied " + values[key].Caption + ".";
            tips.SetToolTip(source, "");
        }
        catch (ExternalException) { ReportError("Clipboard busy. Click the value again."); }
    }

    private void ReportError(string message, string details = "")
    {
        source.Text = message;
        tips.SetToolTip(source, details);
    }
}

internal sealed class DrawerToggle : RoundedButton
{
    private bool expanded;

    public DrawerToggle()
    {
        BackColor = Theme.Raised;
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        AccessibleName = "Show saved colors";
    }

    internal void SetExpanded(bool open)
    {
        expanded = open;
        AccessibleName = open ? "Hide saved colors" : "Show saved colors";
        Invalidate();
    }

    protected override void DrawButtonContent(Graphics graphics)
    {
        var center = new Point(Width / 2, Height / 2);
        var half = Theme.Dip(this, 4);
        var direction = expanded ? -1 : 1;
        using var pen = new Pen(ForeColor, Theme.Dip(this, 2))
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };
        graphics.DrawLines(pen, new[]
        {
            new Point(center.X + direction * half, center.Y - half),
            new Point(center.X - direction * half, center.Y),
            new Point(center.X + direction * half, center.Y + half)
        });
    }
}

internal sealed class RecentColorButton(ColorValue value, int cornerRadius = 3) : Button
{
    internal ColorValue Value => value;
    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        Text = "";
        AccessibleName = value.Hex;
        AccessibleDescription = "Show this previous color";
        Cursor = Cursors.Hand;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.ParentBackground(this));
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = Theme.RoundedRectangle(new RectangleF(1, 1, Width - 3, Height - 3), Theme.Dip(this, cornerRadius));
        using var fill = new SolidBrush(value.DrawingColor());
        e.Graphics.FillPath(fill, path);
        using var border = new Pen(Focused ? Theme.Text : Theme.Border, Focused ? 2 : 1);
        e.Graphics.DrawPath(border, path);
    }
}

internal sealed class ValueButton : RoundedButton
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Caption { get; set; } = "";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string ValueText { get; set; } = "";
    public ValueButton()
    {
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        Font = new Font("Consolas", 10);
    }
    protected override void DrawButtonContent(Graphics graphics)
    {
        var gap = Theme.Dip(this, 12);
        var labelWidth = Theme.Dip(this, 90);
        using var labelFont = new Font("Segoe UI", 9f * Theme.WindowDpi(this) / 72f,
            FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(graphics, Caption, labelFont, new Rectangle(gap, 0, labelWidth, Height), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(graphics, ValueText, Font, new Rectangle(gap + labelWidth, 0, Width - labelWidth - gap * 2, Height), ForeColor,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}
