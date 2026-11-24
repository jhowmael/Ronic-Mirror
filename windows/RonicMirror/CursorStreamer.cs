using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static RonicMirror.Native;

namespace RonicMirror;

/// <summary>
/// Envia o cursor FORA do vídeo para o aparelho desenhar sozinho, sem o atraso de
/// captura → compressão → rede → decodificação (mesmo formato do macOS, ver docs/PROTOCOL.md):
///   0x20 + x, y (float32 LE, 0…1 na tela virtual) + visível (u8)     até 120×/s, só quando muda
///   0x21 + largura, altura, hotspotX, hotspotY (float32 LE, pixels) + PNG   quando o formato muda
/// </summary>
internal sealed class CursorStreamer : IDisposable
{
    private readonly SourceClient _source;
    private readonly Func<Rectangle?> _bounds;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _resend;

    public CursorStreamer(SourceClient source, Func<Rectangle?> bounds)
    {
        _source = source;
        _bounds = bounds;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Cursor", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Reenvia posição e formato (ex.: depois de reconectar ao servidor).</summary>
    public void ResendAll() => _resend = true;

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
    }

    private void Loop()
    {
        float lastX = -1, lastY = -1;
        bool lastVisible = false;
        IntPtr lastCursor = IntPtr.Zero;

        while (_running)
        {
            try
            {
                if (_resend)
                {
                    _resend = false;
                    lastX = lastY = -1;
                    lastCursor = IntPtr.Zero;
                }

                var b = _bounds();
                var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (b is { Width: > 0, Height: > 0 } r && GetCursorInfo(ref ci))
                {
                    bool visible = (ci.flags & CURSOR_SHOWING) != 0 && r.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y);
                    float x = (ci.ptScreenPos.X - r.X) / (float)r.Width;
                    float y = (ci.ptScreenPos.Y - r.Y) / (float)r.Height;

                    if (visible != lastVisible || (visible && (x != lastX || y != lastY)))
                    {
                        lastVisible = visible; lastX = x; lastY = y;
                        var msg = new byte[10];
                        msg[0] = 0x20;
                        BitConverter.TryWriteBytes(msg.AsSpan(1), x);
                        BitConverter.TryWriteBytes(msg.AsSpan(5), y);
                        msg[9] = (byte)(visible ? 1 : 0);
                        _source.TrySendImmediate(msg);
                    }

                    if (ci.hCursor != IntPtr.Zero && ci.hCursor != lastCursor)
                    {
                        lastCursor = ci.hCursor;
                        var shape = EncodeShape(ci.hCursor);
                        if (shape != null) _ = _source.SendBinaryAsync(shape);
                    }
                }
            }
            catch { /* tela bloqueada etc. */ }
            Thread.Sleep(8); // ~120 Hz
        }
    }

    private static byte[]? EncodeShape(IntPtr hCursor)
    {
        if (!GetIconInfo(hCursor, out var info)) return null;
        try
        {
            using var icon = Icon.FromHandle(hCursor);
            using var bmp = new Bitmap(icon.Width, icon.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                IntPtr hdc = g.GetHdc();
                try { DrawIconEx(hdc, 0, 0, hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL); }
                finally { g.ReleaseHdc(hdc); }
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            var png = ms.ToArray();

            var msg = new byte[17 + png.Length];
            msg[0] = 0x21;
            BitConverter.TryWriteBytes(msg.AsSpan(1), (float)bmp.Width);
            BitConverter.TryWriteBytes(msg.AsSpan(5), (float)bmp.Height);
            BitConverter.TryWriteBytes(msg.AsSpan(9), (float)info.xHotspot);
            BitConverter.TryWriteBytes(msg.AsSpan(13), (float)info.yHotspot);
            png.CopyTo(msg, 17);
            return msg;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }
    }
}
