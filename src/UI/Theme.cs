using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ColorFinder;

internal static class Theme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    internal static int WindowDpi(Control control)
    {
        var window = control.TopLevelControl ?? control;
        var dpi = window.IsHandleCreated ? GetDpiForWindow(window.Handle) : 0;
        return dpi == 0 ? 96 : (int)dpi;
    }

    internal static int Dip(Control control, int pixels) =>
        (int)Math.Round(pixels * WindowDpi(control) / 96d, MidpointRounding.AwayFromZero);

    public static void ApplyDarkTitleBar(Form form)
    {
        try
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    public static readonly Color Background = Color.FromArgb(16, 20, 28);
    public static readonly Color Surface = Color.FromArgb(24, 30, 41);
    public static readonly Color Raised = Color.FromArgb(34, 43, 57);
    public static readonly Color Input = Color.FromArgb(15, 21, 31);
    public static readonly Color Border = Color.FromArgb(48, 60, 77);
    public static readonly Color Text = Color.FromArgb(235, 242, 249);
    public static readonly Color Muted = Color.FromArgb(151, 169, 191);
    public static readonly Color Accent = Color.FromArgb(134, 231, 190);

    public static Label Label(string text, float size = 10, bool bold = false, Color? color = null) => new()
    {
        Text = text, ForeColor = color ?? Text, BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty, AutoEllipsis = true
    };

    public static Button MakeButton(string text, EventHandler action, bool primary = false)
    {
        var button = new RoundedButton
        {
            Text = text, AccessibleName = text.Replace("…", ""),
            BackColor = primary ? Accent : Raised, ForeColor = primary ? Background : Text,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Cursor = Cursors.Hand,
            Size = new Size(116, 34), Margin = Padding.Empty,
        };
        button.Click += action;
        return button;
    }

    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color ParentBackground(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent.BackColor.A == 255) return parent.BackColor;
        return Background;
    }

    public static Color DrawingColor(this ColorValue color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}

internal class RoundedButton : Button
{
    private bool hover;
    private bool pressed;
    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.ParentBackground(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var color = BackColor;
        if (Enabled && (hover || pressed))
        {
            var blend = pressed ? .12 : .07;
            color = Color.FromArgb((int)(color.R + (255 - color.R) * blend),
                (int)(color.G + (255 - color.G) * blend), (int)(color.B + (255 - color.B) * blend));
        }
        using var path = Theme.RoundedRectangle(new RectangleF(0, 0, Width - 1, Height - 1), Theme.Dip(this, 7));
        using var fill = new SolidBrush(color);
        g.FillPath(fill, path);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Theme.Accent, Math.Max(1, Theme.Dip(this, 1)));
            g.DrawPath(focus, path);
        }
        DrawButtonContent(g);
    }
    protected virtual void DrawButtonContent(Graphics graphics)
    {
        TextRenderer.DrawText(graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class SwatchCard : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ColorValue Value { get; set; } = new(131, 231, 189);
    public SwatchCard() { DoubleBuffered = true; ResizeRedraw = true; AccessibleName = "Current color preview"; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.ParentBackground(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedRectangle(new RectangleF(0, 0, Width - 1, Height - 1), Theme.Dip(this, 8));
        var saved = g.Save();
        g.SetClip(path);
        int cell = Theme.Dip(this, 10);
        using var light = new SolidBrush(Color.FromArgb(205, 211, 220));
        using var dark = new SolidBrush(Color.FromArgb(156, 164, 178));
        for (int y = 0; y < Height; y += cell)
            for (int x = 0; x < Width; x += cell)
                g.FillRectangle(((x / cell + y / cell) % 2 == 0) ? light : dark, x, y, cell, cell);
        using var color = new SolidBrush(Value.DrawingColor());
        g.FillRectangle(color, ClientRectangle);
        g.Restore(saved);
        using var border = new Pen(Color.FromArgb(30, 255, 255, 255));
        g.DrawPath(border, path);
    }
}
