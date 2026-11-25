using System.Drawing;
using System.Runtime.InteropServices;
using static RonicMirror.Native;

namespace RonicMirror;

/// <summary>
/// Transforma os toques do aparelho em eventos de mouse no Windows (SendInput):
///   toque com 1 dedo → clique esquerdo (dois toques rápidos = clique duplo, o Windows detecta sozinho)
///   toque com 2 dedos → clique direito
///   arrastar → rolagem
///   segurar e arrastar → clicar e arrastar
/// </summary>
internal sealed class InputInjector
{
    private bool _dragging;
    private double _wheelRestX, _wheelRestY;

    // ---------------- Roteamento dos toques + arrastar para outro monitor ----------------
    // O dedo não consegue sair da tela do aparelho. Então, enquanto você arrasta algo e mantém o dedo
    // PARADO NA BORDA, o cursor continua andando sozinho naquela direção (acelerando), atravessa para o
    // monitor vizinho e para um pouco depois da borda. Ao soltar o dedo, o item é solto lá.

    private const double EdgeMargin = 0.03;
    private const double LandingDistance = 260;
    private readonly object _lock = new();
    private Rectangle? _dragBounds;
    private PointF _dragPoint;
    private bool _escaped, _pushFinished;
    private PointF? _pushDir;
    private readonly System.Diagnostics.Stopwatch _pushClock = new();
    private Thread? _pushThread;

    /// <summary>Um toque vindo do aparelho. x/y de 0 a 1 na tela dele; dx/dy em pixels.</summary>
    public void Handle(string kind, Rectangle b, double x, double y, double dx, double dy)
    {
        lock (_lock)
        {
            x = Math.Clamp(x, 0, 1);
            y = Math.Clamp(y, 0, 1);
            var p = new Point(b.X + (int)Math.Round(x * (b.Width - 1)), b.Y + (int)Math.Round(y * (b.Height - 1)));
            switch (kind)
            {
                case "tap": Click(p, right: false); break;
                case "rtap": Click(p, right: true); break;
                case "scroll": Scroll(p, dx, dy); break;
                case "down":
                    StopPush();
                    _dragBounds = b;
                    _dragPoint = p;
                    _escaped = _pushFinished = false;
                    DragBegin(p);
                    break;
                case "move":
                    if (_dragBounds == null) return;
                    if (!_escaped) { _dragPoint = p; DragMove(p); }
                    var dir = EdgeDirection(x, y);
                    if (dir != null) StartPush(dir.Value); else StopPush();
                    break;
                case "up":
                    StopPush();
                    if (_dragBounds != null) DragEnd(Point.Round(_dragPoint));
                    _dragBounds = null;
                    break;
            }
        }
    }

    private static PointF? EdgeDirection(double x, double y)
    {
        float dx = x >= 1 - EdgeMargin ? 1 : x <= EdgeMargin ? -1 : 0;
        float dy = y >= 1 - EdgeMargin ? 1 : y <= EdgeMargin ? -1 : 0;
        if (dx == 0 && dy == 0) return null;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        return new PointF(dx / len, dy / len);
    }

    private void StartPush(PointF dir)
    {
        if (_pushFinished || _pushDir == dir) return;
        _pushDir = dir;
        _pushClock.Restart();
        if (_pushThread != null) return;
        _pushThread = new Thread(PushLoop) { IsBackground = true, Name = "Arrastar para outra tela" };
        _pushThread.Start();
    }

    private void StopPush() => _pushDir = null;

    private void PushLoop()
    {
        double last = 0;
        while (true)
        {
            Thread.Sleep(8);
            lock (_lock)
            {
                if (_pushDir is not { } dir || _dragBounds is not { } b) { _pushThread = null; return; }
                double now = _pushClock.Elapsed.TotalSeconds;
                if (now < last) last = 0; // o empurrão recomeçou (relógio zerado)
                double dt = Math.Clamp(now - last, 0.001, 0.05);
                last = now;
                double speed = Math.Min(700 + now * 2200, 2600); // acelera de ~700 até ~2600 px/s
                var next = new PointF(_dragPoint.X + dir.X * (float)(speed * dt), _dragPoint.Y + dir.Y * (float)(speed * dt));
                var np = Point.Round(next);
                if (MonitorFromPoint(new POINT { X = np.X, Y = np.Y }, MONITOR_DEFAULTTONULL) == IntPtr.Zero)
                {
                    StopPush(); // não há monitor naquela direção
                    continue;
                }
                _dragPoint = next;
                DragMove(np);
                if (!b.Contains(np))
                {
                    _escaped = true;
                    double outside = Math.Max(Math.Max(b.Left - next.X, next.X - b.Right), Math.Max(b.Top - next.Y, next.Y - b.Bottom));
                    if (outside >= LandingDistance) { _pushFinished = true; StopPush(); }
                }
            }
        }
    }

    public void Click(Point p, bool right)
    {
        SetCursorPos(p.X, p.Y);
        if (right) Send(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP);
        else Send(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
    }

    public void DragBegin(Point p)
    {
        SetCursorPos(p.X, p.Y);
        _dragging = true;
        Send(MOUSEEVENTF_LEFTDOWN);
    }

    public void DragMove(Point p)
    {
        if (_dragging) SetCursorPos(p.X, p.Y);
    }

    public void DragEnd(Point p)
    {
        if (!_dragging) return;
        _dragging = false;
        SetCursorPos(p.X, p.Y);
        Send(MOUSEEVENTF_LEFTUP);
    }

    /// <summary>dx/dy em pixels: o conteúdo acompanha o dedo, como numa tela de toque.</summary>
    public void Scroll(Point p, double dx, double dy)
    {
        SetCursorPos(p.X, p.Y);
        // ~2 unidades de roda por pixel (120 = um "dente" da roda); acumula as frações.
        _wheelRestY += dy * 2;
        _wheelRestX += -dx * 2;
        int wy = (int)_wheelRestY, wx = (int)_wheelRestX;
        _wheelRestY -= wy;
        _wheelRestX -= wx;
        var inputs = new List<INPUT>();
        if (wy != 0) inputs.Add(Mouse(MOUSEEVENTF_WHEEL, wy));
        if (wx != 0) inputs.Add(Mouse(MOUSEEVENTF_HWHEEL, wx));
        if (inputs.Count > 0) SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static void Send(params uint[] flags)
    {
        var inputs = flags.Select(f => Mouse(f, 0)).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Mouse(uint flags, int data) =>
        new() { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } };
}
