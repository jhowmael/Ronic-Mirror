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
