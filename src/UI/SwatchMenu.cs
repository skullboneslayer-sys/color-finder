using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ColorFinder;

internal sealed class SwatchMenu : ContextMenuStrip
{
    private const int LogicalWidth = 158;
    private const int RowHeight = 36;
    private const int Gutter = 5;
    private int dpi = 96;
    private Font? menuFont;

    public SwatchMenu()
    {
        AutoSize = false;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Renderer = new MenuRenderer(this);
        Size = new Size(LogicalWidth, RowHeight + 2 * Gutter);
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        var sourceDpi = Theme.WindowDpi(SourceControl ?? this);
        if (menuFont is null || dpi != sourceDpi)
        {
            dpi = sourceDpi;
            var oldFont = menuFont;
            menuFont = new Font("Segoe UI", 13f * dpi / 96, FontStyle.Bold, GraphicsUnit.Pixel);
            Font = menuFont;
            oldFont?.Dispose();
        }

        SuspendLayout();
        try
        {
            int gutter = Scale(Gutter);
            int width = Scale(LogicalWidth);
            int rowHeight = Scale(RowHeight);
            foreach (ToolStripItem item in Items)
            {
                item.AutoSize = false;
                item.Margin = Padding.Empty;
                item.Size = new Size(width - 2 * gutter, rowHeight);
            }
            Padding = new Padding(gutter);
            Size = new Size(width, rowHeight * Math.Max(1, Items.Count) + 2 * gutter);
        }
        finally { ResumeLayout(true); }

        SetRoundedRegion();
        base.OnOpening(e);
    }

    private int Scale(int logical) =>
        (int)Math.Round(logical * dpi / 96d, MidpointRounding.AwayFromZero);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int top = Scale(Gutter);
        foreach (ToolStripItem item in Items)
        {
            SetItemLocation(item, new Point((ClientSize.Width - item.Width) / 2, top));
            top += item.Height;
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        SetRoundedRegion();
    }

    private void SetRoundedRegion()
    {
        if (IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var shape = Theme.RoundedRectangle(ClientRectangle, Scale(10));
        var oldRegion = Region;
        Region = new Region(shape);
        oldRegion?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) menuFont?.Dispose();
    }

    private sealed class MenuRenderer(SwatchMenu owner) : ToolStripRenderer
    {
        private static readonly Color RemoveRed = Color.FromArgb(169, 42, 63);
        private static readonly Color RemoveHover = Color.FromArgb(189, 52, 74);
        private static readonly Color RemovePressed = Color.FromArgb(139, 33, 51);
        private static readonly Color RemoveOutline = Color.FromArgb(217, 91, 108);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var fill = new SolidBrush(Theme.Surface);
            e.Graphics.FillRectangle(fill, e.ToolStrip.ClientRectangle);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var size = e.ToolStrip.ClientSize;
            if (size.Width < 2 || size.Height < 2) return;
            var mode = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Theme.RoundedRectangle(
                new RectangleF(.5f, .5f, size.Width - 1, size.Height - 1), owner.Scale(10));
            using var outline = new Pen(Theme.Border);
            e.Graphics.DrawPath(outline, shape);
            e.Graphics.SmoothingMode = mode;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var bounds = ButtonBounds(e.Item);
            if (bounds.Width <= 2 || bounds.Height <= 2) return;
            var mode = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Theme.RoundedRectangle(bounds, owner.Scale(7));
            var color = !e.Item.Enabled ? Theme.Raised :
                e.Item.Pressed ? RemovePressed : e.Item.Selected ? RemoveHover : RemoveRed;
            using var fill = new SolidBrush(color);
            e.Graphics.FillPath(fill, shape);
            using var outline = new Pen(e.Item.Enabled ? RemoveOutline : Theme.Border);
            e.Graphics.DrawPath(outline, shape);
            e.Graphics.SmoothingMode = mode;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            int inset = owner.Scale(11);
            var bounds = Rectangle.Inflate(ButtonBounds(e.Item), -inset, 0);
            if (bounds.Width <= 0) return;
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, bounds,
                e.Item.Enabled ? Color.White : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.HidePrefix);
        }

        private Rectangle ButtonBounds(ToolStripItem item)
        {
            // ContextMenuStrip can reserve space on one side even with its image margin off.
            // Center the button within the space the item can actually paint.
            var left = item.Bounds.Left;
            var width = owner.ClientSize.Width;
            var inset = Math.Max(owner.Scale(Gutter), Math.Max(left + 1, width - item.Bounds.Right + 1));
            return new Rectangle(inset - left, 1, width - 2 * inset, item.Height - 2);
        }
    }
}
