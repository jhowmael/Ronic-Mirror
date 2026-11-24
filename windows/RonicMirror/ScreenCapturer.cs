using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static RonicMirror.Native;

namespace RonicMirror;

/// <summary>
/// Captura EXCLUSIVAMENTE a área do monitor virtual (GDI), desenha o cursor e codifica em JPEG, a até 60 fps.
/// Pipeline em duas threads: enquanto uma captura o próximo frame, a outra comprime e envia o anterior.
/// Só envia quando algo mudou na tela, e só captura enquanto houver aparelho conectado.
/// </summary>
internal sealed class ScreenCapturer : IDisposable
{
    public Func<Rectangle?> Bounds { get; init; } = () => null;
    public Func<bool> ShouldCapture { get; init; } = () => true;
    /// <summary>Envia o frame; bloqueia até terminar (backpressure natural).</summary>
    public Action<byte[]> OnFrame { get; init; } = _ => { };

    // JPEG é ~10× mais pesado que H.264: 30 fps mantém o Wi-Fi livre (Wi-Fi congestionado = atraso).
    public int Fps { get; } = 30;
    public long Quality { get; set; } = 55;

    private Thread? _captureThread, _encodeThread;
    private volatile bool _running;
    private volatile bool _forceNext;

    // Entrega do frame capturado para a thread de codificação (sempre o mais recente).
    private readonly object _slotLock = new();
    private readonly AutoResetEvent _hasWork = new(false);
    private readonly Stack<byte[]> _free = new();
    private byte[]? _pending;
    private int _pendingW, _pendingH, _pendingStride;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public void Start()
    {
        if (_running) return;
        _running = true;
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Captura", Priority = ThreadPriority.AboveNormal };
        _encodeThread = new Thread(EncodeLoop) { IsBackground = true, Name = "JPEG", Priority = ThreadPriority.AboveNormal };
        _captureThread.Start();
        _encodeThread.Start();
    }

    /// <summary>Envia o próximo frame mesmo que nada tenha mudado (ex.: mudou a resolução, aparelho novo).</summary>
    public void ForceNextFrame() => _forceNext = true;

    public void Dispose()
    {
        _running = false;
        _hasWork.Set();
        _captureThread?.Join(1000);
        _encodeThread?.Join(1000);
    }

    // ---------------- Thread 1: captura ----------------

    private void CaptureLoop()
    {
        Bitmap? bmp = null;
        byte[] current = Array.Empty<byte>(), previous = Array.Empty<byte>();
        var frameTime = TimeSpan.FromSeconds(1.0 / Fps);
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
                    // O cursor NÃO vai no vídeo: o CursorStreamer manda a posição separada, sem atraso.
                    g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                }

                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
                int stride = Math.Abs(data.Stride);
                int len = stride * data.Height;
                if (current.Length != len) current = new byte[len];
                Marshal.Copy(data.Scan0, current, 0, len);
                bmp.UnlockBits(data);

                // Detecção de mudança: compara os pixels com o frame anterior (vetorizado).
                bool changed = _forceNext || previous.Length != len || !current.AsSpan().SequenceEqual(previous);
                if (changed)
                {
                    _forceNext = false;
                    Publish(current, bmp.Width, bmp.Height, stride);
                    (previous, current) = (current, previous);
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

    /// <summary>Copia os pixels para um buffer livre e entrega à thread de codificação (substitui o pendente).</summary>
    private void Publish(byte[] pixels, int width, int height, int stride)
    {
        byte[] buf;
        lock (_slotLock)
        {
            buf = _free.Count > 0 && _free.Peek().Length == pixels.Length ? _free.Pop() : new byte[pixels.Length];
        }
        Buffer.BlockCopy(pixels, 0, buf, 0, pixels.Length);
        lock (_slotLock)
        {
            if (_pending != null) _free.Push(_pending); // a codificação não acompanhou: descarta o antigo
            _pending = buf;
            _pendingW = width; _pendingH = height; _pendingStride = stride;
        }
        _hasWork.Set();
    }

    // ---------------- Thread 2: JPEG + envio ----------------

    private void EncodeLoop()
    {
        var encParams = new EncoderParameters(1);
        while (_running)
        {
            _hasWork.WaitOne(200);
            byte[]? buf;
            int w, h, stride;
            lock (_slotLock)
            {
                buf = _pending;
                _pending = null;
                w = _pendingW; h = _pendingH; stride = _pendingStride;
            }
            if (buf == null) continue;

            try
            {
                var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try
                {
                    using var bmp = new Bitmap(w, h, stride, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
                    using var ms = new MemoryStream(256 * 1024);
                    encParams.Param[0] = new EncoderParameter(Encoder.Quality, Quality);
                    bmp.Save(ms, JpegCodec, encParams);
                    OnFrame(ms.ToArray());
                }
                finally
                {
                    handle.Free();
                }
            }
            catch (Exception)
            {
                _forceNext = true;
            }

            lock (_slotLock)
            {
                if (_free.Count < 3) _free.Push(buf);
            }
        }
    }
}
