using System.Drawing;
using System.Drawing.Drawing2D;

namespace RonicMirror;

/// <summary>
/// Editor visual de posição (como Configurações › Sistema › Tela do Windows):
/// desenha os monitores em escala e permite arrastar a tela do Ronic Mirror.
/// Ao soltar, dispara Moved com a origem desejada em coordenadas da área de trabalho.
/// </summary>
internal sealed class ArrangementControl : Control
{
    /// <summary>(nome do monitor arrastado, origem desejada)</summary>
    public event Action<string, Point>? Moved;
    /// <summary>Tela do Ronic Mirror clicada/arrastada (para os botões de posição rápida).</summary>
    public event Action<string>? Selected;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? SelectedDevice { get; set; }

    private List<DisplayTileInfo> _displays = new();
    private float _scale;
    private PointF _origin;
    private bool _dragging;
    private DisplayTileInfo? _dragTile;
    private Point _dragStart;
    private Size _dragOffset;

    public ArrangementControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Color.FromArgb(244, 246, 248);
    }

    public void SetDisplays(List<DisplayTileInfo> displays)
    {
        if (_dragging) return;
        _displays = displays;
        Invalidate();
    }

    private RectangleF ToScreen(Rectangle r) =>
        new(r.X * _scale + _origin.X, r.Y * _scale + _origin.Y, r.Width * _scale, r.Height * _scale);

    private void ComputeTransform()
    {
        if (_displays.Count == 0) { _scale = 0; return; }
        var union = _displays.Select(d => d.Frame).Aggregate(Rectangle.Union);
        const float pad = 28;
        _scale = Math.Min((Width - pad * 2) / union.Width, (Height - pad * 2) / union.Height);
        _origin = new PointF((Width - union.Width * _scale) / 2 - union.X * _scale,
                             (Height - union.Height * _scale) / 2 - union.Y * _scale);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (!_dragging) ComputeTransform();
        if (_scale <= 0) return;

        foreach (var d in _displays.OrderBy(d => d == _dragTile ? 2 : d.IsVirtual ? 1 : 0))
        {
            var r = ToScreen(d.Frame);
            if (_dragging && d == _dragTile) r.Offset(_dragOffset.Width, _dragOffset.Height);
            r.Inflate(-1, -1);
            using var path = Rounded(r, 6);

            if (d.IsVirtual)
            {
                using var brush = new LinearGradientBrush(r, Color.FromArgb(34, 228, 245), Color.FromArgb(0, 131, 176), 45f);
                g.FillPath(brush, path);
                bool selected = d.DeviceName == SelectedDevice;
                using var pen = new Pen(selected ? Color.FromArgb(0, 90, 200) : Color.FromArgb(200, 255, 255, 255), selected ? 3f : 1.5f);
                g.DrawPath(pen, path);
            }
            else
            {
                g.FillPath(Brushes.White, path);
                using var pen = new Pen(Color.FromArgb(150, 150, 160), 1.5f);
                g.DrawPath(pen, path);
                if (d.IsPrimary)
                {
                    using var bar = new SolidBrush(Color.FromArgb(70, 120, 120, 130)); // barra de tarefas
                    g.FillRectangle(bar, r.X + 2, r.Bottom - 6, r.Width - 4, 4);
                }
            }

            var color = d.IsVirtual ? Color.White : Color.FromArgb(40, 40, 45);
            using var titleFont = new Font("Segoe UI Semibold", 9f);
            using var subFont = new Font("Segoe UI", 8f);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            using var b = new SolidBrush(color);
            var top = new RectangleF(r.X, r.Y, r.Width, r.Height / 2 + 4);
            var bottom = new RectangleF(r.X, r.Y + r.Height / 2, r.Width, r.Height / 2 - 4);
            g.DrawString(d.Name, titleFont, b, top, fmt);
            g.DrawString($"{d.Frame.Width}×{d.Frame.Height}{(d.IsPrimary ? " · principal" : "")}", subFont, b, bottom, fmt);
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private DisplayTileInfo? VirtualAt(Point p) =>
        _displays.LastOrDefault(d => d.IsVirtual && ToScreen(d.Frame).Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            _dragOffset = new Size(e.X - _dragStart.X, e.Y - _dragStart.Y);
            Invalidate();
        }
        else
        {
            Cursor = VirtualAt(e.Location) != null ? Cursors.SizeAll : Cursors.Default;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var tile = e.Button == MouseButtons.Left ? VirtualAt(e.Location) : null;
        if (tile == null) return;
        _dragTile = tile;
        SelectedDevice = tile.DeviceName;
        Selected?.Invoke(tile.DeviceName);
        _dragging = true;
        _dragStart = e.Location;
        _dragOffset = Size.Empty;
        Capture = true;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        var v = _dragTile;
        _dragTile = null;
        if (v != null && _scale > 0 && (_dragOffset.Width != 0 || _dragOffset.Height != 0))
        {
            var target = new Point(v.Frame.X + (int)Math.Round(_dragOffset.Width / _scale),
                                   v.Frame.Y + (int)Math.Round(_dragOffset.Height / _scale));
            Moved?.Invoke(v.DeviceName, target);
        }
        _dragOffset = Size.Empty;
        Invalidate();
    }
}
