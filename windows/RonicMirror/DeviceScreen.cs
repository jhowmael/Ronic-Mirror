using System.Drawing;

namespace RonicMirror;

/// <summary>
/// A tela de UM aparelho: um monitor virtual próprio, sua captura e seu fluxo de imagens.
/// Criada quando o aparelho é aceito e destruída (o monitor some) quando ele sai.
/// </summary>
internal sealed class DeviceScreen : IDisposable
{
    public string Conn { get; }
    public string DeviceKey { get; }
    public string Name { get; }
    public VirtualMonitor Monitor { get; }
    public ScreenCapturer Capturer { get; }
    public int RequestedWidth { get; set; }
    public int RequestedHeight { get; set; }
    public string SizeLabel { get; set; } = "";

    /// <summary>0xA0 + tamanho + conn: o servidor entrega a mensagem só a este aparelho.</summary>
    public byte[] Prefix { get; }

    public DeviceScreen(DeviceInfo device, VirtualMonitor monitor, SourceClient source)
    {
        Conn = device.Conn;
        DeviceKey = device.DeviceId != "" ? device.DeviceId : device.Conn;
        Name = device.Name;
        Monitor = monitor;
        RequestedWidth = device.Width > 0 ? device.Width : 1366;
        RequestedHeight = device.Height > 0 ? device.Height : 1024;
        Prefix = PrefixFor(device.Conn);
        var prefix = Prefix;
        Capturer = new ScreenCapturer
        {
            Bounds = () => monitor.Bounds,
            ShouldCapture = () => source.IsConnected,
            OnFrame = jpeg => source.TrySendFrame(Concat(prefix, jpeg)),
        };
    }

    public static byte[] PrefixFor(string conn)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(conn);
        var p = new byte[2 + Math.Min(bytes.Length, 255)];
        p[0] = 0xA0;
        p[1] = (byte)(p.Length - 2);
        Array.Copy(bytes, 0, p, 2, p.Length - 2);
        return p;
    }

    public static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    /// <summary>Converte coordenadas normalizadas (0…1) do aparelho em pixels da área de trabalho.</summary>
    public Point ToDesktop(double x, double y)
    {
        var b = Monitor.Bounds;
        return new Point(b.X + (int)Math.Round(Math.Clamp(x, 0, 1) * (b.Width - 1)),
                         b.Y + (int)Math.Round(Math.Clamp(y, 0, 1) * (b.Height - 1)));
    }

    public void Dispose()
    {
        Capturer.Dispose();
        Monitor.Dispose(); // remove o monitor virtual
    }
}
