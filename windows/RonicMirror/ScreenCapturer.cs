using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static RonicMirror.Native;

namespace RonicMirror;

/// <summary>
/// Captura EXCLUSIVAMENTE a área do monitor virtual (GDI), desenha o cursor e codifica em JPEG.
/// Só envia quando algo mudou na tela, e só captura enquanto houver iPad conectado.
/// </summary>
internal sealed class ScreenCapturer : IDisposable
{
    public Func<Rectangle?> Bounds { get; init; } = () => null;
    public Func<bool> ShouldCapture { get; init; } = () => true;
    /// <summary>Envia o frame; bloqueia até terminar (backpressure natural).</summary>
    public Action<byte[]> OnFrame { get; init; } = _ => { };

    public int Fps { get; } = 30;
    public long Quality { get; set; } = 60;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _forceNext;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Captura", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Envia o próximo frame mesmo que nada tenha mudado (ex.: mudou a resolução).</summary>
    public void ForceNextFrame() => _forceNext = true;

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
    }

    private void Loop()
    {
        Bitmap? bmp = null;
        byte[] current = Array.Empty<byte>(), previous = Array.Empty<byte>();
        var frameTime = TimeSpan.FromSeconds(1.0 / Fps);
        var encParams = new EncoderParameters(1);
        var sw = new Stopwatch();

        while (_running)
        {
            sw.Restart();
            try
            {
                var rect = Bounds();
                if (rect is not { Width: > 0, Height: > 0 } b || !ShouldCapture())
                {
                    Thread.Sleep(150);
                    _forceNext = true; // ao voltar, manda um frame completo
                    continue;
                }

                if (bmp == null || bmp.Width != b.Width || bmp.Height != b.Height)
                {
                    bmp?.Dispose();
                    bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb);
                    _forceNext = true;
                }

                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                    DrawCursor(g, b);
                }

                // Detecção de mudança: compara os pixels com o frame anterior.
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
                int len = Math.Abs(data.Stride) * data.Height;
                if (current.Length != len) current = new byte[len];
                Marshal.Copy(data.Scan0, current, 0, len);
                bmp.UnlockBits(data);

                bool changed = _forceNext || previous.Length != len || !current.AsSpan().SequenceEqual(previous);
                if (changed)
                {
                    _forceNext = false;
                    (previous, current) = (current, previous);
                    using var ms = new MemoryStream(256 * 1024);
                    encParams.Param[0] = new EncoderParameter(Encoder.Quality, Quality);
                    bmp.Save(ms, JpegCodec, encParams);
                    OnFrame(ms.ToArray());
                }
            }
            catch (Exception)
            {
                // Ex.: tela bloqueada (área de trabalho segura) ou monitor em reconfiguração.
                Thread.Sleep(250);
                _forceNext = true;
            }

            var wait = frameTime - sw.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }
        bmp?.Dispose();
    }

    private static void DrawCursor(Graphics g, Rectangle bounds)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        if (!bounds.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y)) return;

        int hx = 0, hy = 0;
        if (GetIconInfo(ci.hCursor, out var info))
        {
            hx = info.xHotspot; hy = info.yHotspot;
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }
        IntPtr hdc = g.GetHdc();
        try
        {
            DrawIconEx(hdc, ci.ptScreenPos.X - bounds.X - hx, ci.ptScreenPos.Y - bounds.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }
}
