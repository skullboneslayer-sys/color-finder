using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ColorFinder;

public sealed record PickedPixel(Color Color, Point Position, string Source);

/// <summary>Captures a frozen desktop and selects a single physical screen pixel.</summary>
internal static class ScreenPicker
{
    public static Task<PickedPixel?> PickAsync(Form owner)
    {
        if (owner is null) throw new ArgumentNullException(nameof(owner));
        if (owner.InvokeRequired)
            throw new InvalidOperationException("The screen picker must run on the UI thread.");

        return new PickerSession(owner).Start();
    }

    private static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(value, maximum));

    internal static Rectangle PlaceInstructions(Rectangle top, Rectangle bottom, Point? pointer,
        Rectangle magnifier, int clearance, bool wasAtBottom)
    {
        bool IsClear(Rectangle bounds, int pointerClearance) =>
            (!pointer.HasValue || !Rectangle.Inflate(bounds, pointerClearance, pointerClearance).Contains(pointer.Value)) &&
            (magnifier.IsEmpty || !Rectangle.Inflate(bounds, clearance, clearance).IntersectsWith(magnifier));

        // Keep the threshold tied to the top position. The card's movement must not
        // immediately clear the same collision and send it back under the pointer.
        if (IsClear(top, wasAtBottom ? clearance + clearance / 2 : clearance)) return top;
        if (IsClear(bottom, clearance)) return bottom;
        return IsClear(top, clearance) ? top : Rectangle.Empty;
    }

    private sealed class CapturedMonitor(Rectangle bounds, Bitmap bitmap, string displayName)
    {
        public Rectangle Bounds { get; } = bounds;
        public Bitmap Bitmap { get; } = bitmap;
        public string DisplayName { get; } = displayName;
        public PickerOverlay? Overlay { get; set; }
    }

    private sealed class PickerSession(Form owner)
    {
        private readonly List<CapturedMonitor> _monitors = new();
        private readonly TaskCompletionSource<PickedPixel?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
        private bool _finished;
        private Point _pointer;
        private CapturedMonitor? _active;

        public Point Pointer => _pointer;

        public Task<PickedPixel?> Start()
        {
            try
            {
                // The application manifest declares PerMonitorV2. In that context,
                // Screen.Bounds and Cursor.Position use physical pixels, including negative
                // monitor origins. Never apply UI DPI scale factors to these coordinates.
                foreach (var screen in Screen.AllScreens)
                {
                    var bounds = screen.Bounds;
                    if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                    var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                    try
                    {
                        using var graphics = Graphics.FromImage(bitmap);
                        // Graphics.CopyFromScreen accepts single raster-operation constants,
                        // not OR'ed flags. SourceCopy reads the composited desktop.
                        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size,
                            CopyPixelOperation.SourceCopy);
                        _monitors.Add(new CapturedMonitor(bounds, bitmap, screen.DeviceName));
                    }
                    catch
                    {
                        bitmap.Dispose();
                        throw;
                    }
                }

                if (_monitors.Count == 0)
                    throw new InvalidOperationException("No display is available to sample.");

                // All captures must precede every overlay, so another monitor's picker can
                // never be included in the sampled desktop. Each bitmap stays unmodified.
                foreach (var monitor in _monitors)
                    monitor.Overlay = new PickerOverlay(this, monitor);
                owner.FormClosed += OwnerClosed;
                _timer.Tick += PollPointer;
                foreach (var monitor in _monitors)
                {
                    // Independent windows use their own monitor DPI; owned windows inherit the
                    // owner DPI. OwnerClosed and Cleanup still control the session lifetime.
                    monitor.Overlay!.Show();
                }

                UpdatePointer();
                _active?.Overlay?.Activate();
                _timer.Start();
                return _completion.Task;
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        private void OwnerClosed(object? sender, FormClosedEventArgs e) => Finish(null);
        private void PollPointer(object? sender, EventArgs e) => UpdatePointer();

        public void UpdatePointer()
        {
            if (_finished) return;
            var pointer = Cursor.Position;
            var active = _monitors.FirstOrDefault(m => m.Bounds.Contains(pointer));
            if (pointer == _pointer && active == _active) return;

            var previous = _active;
            _pointer = pointer;
            _active = active;
            previous?.Overlay?.Invalidate();
            _active?.Overlay?.Invalidate();
        }

        public bool IsActive(CapturedMonitor monitor) => _active == monitor;

        public Color ReadPixel(CapturedMonitor monitor, Point position)
        {
            var x = Clamp(position.X - monitor.Bounds.X, 0, monitor.Bitmap.Width - 1);
            var y = Clamp(position.Y - monitor.Bounds.Y, 0, monitor.Bitmap.Height - 1);
            var pixel = monitor.Bitmap.GetPixel(x, y);
            // A desktop screenshot is the composited result; it has no source-layer alpha.
            return Color.FromArgb(255, pixel.R, pixel.G, pixel.B);
        }

        public void Select(Point position)
        {
            if (_finished) return;
            var monitor = _monitors.FirstOrDefault(m => m.Bounds.Contains(position));
            if (monitor is null) return;
            var color = ReadPixel(monitor, position);
            Finish(new PickedPixel(color, position, $"Screen · {position.X}, {position.Y}"));
        }

        public void Cancel() => Finish(null);

        public bool HandleKey(Keys keyData)
        {
            if (_finished) return true;
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Escape) { Cancel(); return true; }
            if (key == Keys.Enter) { Select(Cursor.Position); return true; }
            if (key is not (Keys.Left or Keys.Right or Keys.Up or Keys.Down)) return false;

            var step = (keyData & Keys.Shift) != 0 ? 10 : 1;
            var position = Cursor.Position;
            var delta = key switch
            {
                Keys.Left => new Point(-step, 0),
                Keys.Right => new Point(step, 0),
                Keys.Up => new Point(0, -step),
                _ => new Point(0, step)
            };
            var target = new Point(position.X + delta.X, position.Y + delta.Y);
            // Let adjacent physical monitor bounds work naturally. Do not select pixels
            // inside empty gaps in an irregular monitor arrangement.
            if (_monitors.Any(m => m.Bounds.Contains(target)))
                Cursor.Position = target;
            UpdatePointer();
            return true;
        }

        private void Finish(PickedPixel? pixel)
        {
            if (_finished) return;
            Cleanup();
            _completion.TrySetResult(pixel);
        }

        private void Cleanup()
        {
            if (_finished) return;
            _finished = true;
            owner.FormClosed -= OwnerClosed;
            _timer.Stop();
            _timer.Tick -= PollPointer;
            _timer.Dispose();
            foreach (var monitor in _monitors)
            {
                monitor.Overlay?.Close();
                monitor.Overlay?.Dispose();
                monitor.Bitmap.Dispose();
            }
            _monitors.Clear();
        }
    }

    private sealed class PickerOverlay : Form
    {
        private readonly PickerSession _session;
        private readonly CapturedMonitor _monitor;
        private Font _headingFont = PickerFont("Segoe UI", 11, FontStyle.Bold, 96);
        private Font _smallFont = PickerFont("Segoe UI", 9, FontStyle.Regular, 96);
        private Font _monoFont = PickerFont("Consolas", 11, FontStyle.Bold, 96);
        private int _dpi = 96;
        private bool _instructionsAtBottom;
        private static readonly Color Mint = Color.FromArgb(137, 239, 203);
        private static readonly Color Muted = Color.FromArgb(183, 195, 207);

        public PickerOverlay(PickerSession session, CapturedMonitor monitor)
        {
            _session = session;
            _monitor = monitor;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = monitor.Bounds;
            TopMost = true;
            ShowInTaskbar = true;
            KeyPreview = true;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            var display = monitor.DisplayName.Replace(@"\\.\", "");
            Text = $"Color Finder — find a color · {display}";
        }

        protected override void OnShown(EventArgs e)
        {
            // Reassert physical bounds after handle creation on the destination monitor.
            Bounds = _monitor.Bounds;
            base.OnShown(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var dpi = Theme.WindowDpi(this);
            if (_dpi != dpi)
            {
                _dpi = dpi;
                _headingFont.Dispose();
                _smallFont.Dispose();
                _monoFont.Dispose();
                _headingFont = PickerFont("Segoe UI", 11, FontStyle.Bold, dpi);
                _smallFont = PickerFont("Segoe UI", 9, FontStyle.Regular, dpi);
                _monoFont = PickerFont("Consolas", 11, FontStyle.Bold, dpi);
            }
            e.Graphics.DrawImageUnscaled(_monitor.Bitmap, Point.Empty);
            Point? point = _session.IsActive(_monitor)
                ? new Point(_session.Pointer.X - _monitor.Bounds.X, _session.Pointer.Y - _monitor.Bounds.Y)
                : null;
            var magnifier = point.HasValue ? GetMagnifierBounds(point.Value) : Rectangle.Empty;
            DrawInstructions(e.Graphics, point, magnifier);
            if (point.HasValue && !magnifier.IsEmpty) DrawMagnifier(e.Graphics, point.Value, magnifier);
        }

        // TextRenderer resolves point fonts against the system DPI. Pixel fonts keep
        // the existing point sizes tied to this overlay's actual monitor instead.
        private static Font PickerFont(string family, float points, FontStyle style, int dpi) =>
            new(family, points * dpi / 72f, style, GraphicsUnit.Pixel);
        // Only picker UI dimensions scale with monitor DPI. Screenshot coordinates and
        // source-pixel indices stay physical, and every grid cell remains one source pixel.
        private int Dip(int value) => Math.Max(1, (int)Math.Round(value * _dpi / 96d));

        private void DrawInstructions(Graphics graphics, Point? pointer, Rectangle magnifier)
        {
            var margin = Dip(16);
            var width = Math.Max(1, Math.Min(Dip(490), ClientSize.Width - 2 * margin));
            var top = new Rectangle((ClientSize.Width - width) / 2, Dip(18), width, Dip(106));
            var bottom = new Rectangle(top.X, Math.Max(top.Y, ClientSize.Height - top.Bottom), width, top.Height);
            var box = PlaceInstructions(top, bottom, pointer, magnifier, Dip(20), _instructionsAtBottom);
            if (box.IsEmpty) return;
            _instructionsAtBottom = box == bottom && box != top;
            DrawCard(graphics, box);
            var contentX = box.X + Dip(22);
            var contentWidth = box.Width - Dip(44);
            TextRenderer.DrawText(graphics, "Find a color", _headingFont,
                new Rectangle(contentX, box.Y + Dip(15), contentWidth, Dip(24)), Mint,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(graphics, "Click or Enter to select  ·  Esc or right-click to cancel", _smallFont,
                new Rectangle(contentX, box.Y + Dip(47), contentWidth, Dip(21)), Color.White,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(graphics, "Arrow keys move 1 px  ·  Hold Shift to move 10 px", _smallFont,
                new Rectangle(contentX, box.Y + Dip(73), contentWidth, Dip(21)), Muted,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        private Rectangle GetMagnifierBounds(Point point)
        {
            var cardWidth = Dip(228);
            var cardHeight = Dip(286);
            var gap = Dip(28);
            var edge = Dip(8);
            var x = point.X + gap;
            var y = point.Y + gap;
            if (x + cardWidth > ClientSize.Width - edge) x = point.X - cardWidth - gap;
            if (y + cardHeight > ClientSize.Height - edge) y = point.Y - cardHeight - gap;
            x = Clamp(x, edge, Math.Max(edge, ClientSize.Width - cardWidth - edge));
            y = Clamp(y, edge, Math.Max(edge, ClientSize.Height - cardHeight - edge));

            var bounds = new Rectangle(x, y, cardWidth, cardHeight);
            return Rectangle.Inflate(bounds, Dip(4), Dip(4)).Contains(point) ? Rectangle.Empty : bounds;
        }

        private void DrawMagnifier(Graphics graphics, Point point, Rectangle bounds)
        {
            const int cells = 13;
            var cellSize = Dip(12);
            var gridSize = cells * cellSize;
            var x = bounds.X;
            var y = bounds.Y;
            var cardWidth = bounds.Width;
            DrawCard(graphics, bounds);
            var gridX = x + (cardWidth - gridSize) / 2;
            var gridY = y + Dip(16);

            // Individual source pixels become solid squares. This is nearest-neighbor
            // magnification with no interpolation and no averaging of the chosen pixel.
            for (var row = 0; row < cells; row++)
            for (var column = 0; column < cells; column++)
            {
                var sourceX = point.X + column - cells / 2;
                var sourceY = point.Y + row - cells / 2;
                var valid = sourceX >= 0 && sourceY >= 0 && sourceX < _monitor.Bitmap.Width && sourceY < _monitor.Bitmap.Height;
                using var brush = new SolidBrush(valid ? _monitor.Bitmap.GetPixel(sourceX, sourceY) : Color.FromArgb(45, 49, 58));
                graphics.FillRectangle(brush, gridX + column * cellSize, gridY + row * cellSize, cellSize, cellSize);
            }

            using var grid = new Pen(Color.FromArgb(65, 0, 0, 0));
            for (var i = 0; i <= cells; i++)
            {
                graphics.DrawLine(grid, gridX + i * cellSize, gridY, gridX + i * cellSize, gridY + gridSize);
                graphics.DrawLine(grid, gridX, gridY + i * cellSize, gridX + gridSize, gridY + i * cellSize);
            }

            var selected = new Rectangle(gridX + cells / 2 * cellSize, gridY + cells / 2 * cellSize, cellSize, cellSize);
            using var darkOutline = new Pen(Color.FromArgb(14, 18, 22), Dip(4));
            using var lightOutline = new Pen(Mint, Dip(2));
            graphics.DrawRectangle(darkOutline, selected);
            graphics.DrawRectangle(lightOutline, selected);

            var color = _session.ReadPixel(_monitor, _session.Pointer);
            using var swatch = new SolidBrush(color);
            graphics.FillRectangle(swatch, x + Dip(22), y + Dip(190), Dip(30), Dip(30));
            TextRenderer.DrawText(graphics, $"#{color.R:X2}{color.G:X2}{color.B:X2}", _monoFont,
                new Rectangle(x + Dip(66), y + Dip(196), cardWidth - Dip(88), Dip(24)), Color.White,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(graphics, $"RGB  {color.R}, {color.G}, {color.B}", _smallFont,
                new Rectangle(x + Dip(22), y + Dip(237), cardWidth - Dip(44), Dip(21)), Muted,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(graphics, $"Pixel  {_session.Pointer.X}, {_session.Pointer.Y}", _smallFont,
                new Rectangle(x + Dip(22), y + Dip(260), cardWidth - Dip(44), Dip(21)), Muted,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        private void DrawCard(Graphics graphics, Rectangle bounds)
        {
            var previousSmoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var radius = Dip(10);
            var shadowBounds = bounds;
            shadowBounds.Offset(Dip(2), Dip(4));
            using var shadowPath = RoundedRectangle(shadowBounds, radius);
            using var cardPath = RoundedRectangle(bounds, radius);
            using var shadow = new SolidBrush(Color.FromArgb(65, 0, 0, 0));
            using var background = new SolidBrush(Color.FromArgb(21, 27, 33));
            using var border = new Pen(Color.FromArgb(66, 82, 91), Dip(1));
            graphics.FillPath(shadow, shadowPath);
            graphics.FillPath(background, cardPath);
            graphics.DrawPath(border, cardPath);
            graphics.SmoothingMode = previousSmoothing;
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _session.UpdatePointer();
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) _session.Select(PointToScreen(e.Location));
            else if (e.Button == MouseButtons.Right) _session.Cancel();
            base.OnMouseDown(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
            _session.HandleKey(keyData) || base.ProcessCmdKey(ref msg, keyData);

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _session.Cancel();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _headingFont.Dispose();
                _smallFont.Dispose();
                _monoFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
