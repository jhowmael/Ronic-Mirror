using System.Drawing;
using static RonicMirror.Native;

namespace RonicMirror;

internal enum DisplaySide { Left, Right }

/// <summary>
/// Monitor virtual REAL (tela estendida) no Windows: cria via Parsec VDD, escolhe a resolução
/// mais próxima da do iPad e posiciona à esquerda/direita com ChangeDisplaySettingsEx.
/// </summary>
internal sealed class VirtualMonitor : IDisposable
{
    private readonly ParsecVdd _vdd;
    private readonly int _index;

    public string DeviceName { get; }
    public Rectangle Bounds { get; private set; }

    private VirtualMonitor(ParsecVdd vdd, int index, string deviceName)
    {
        _vdd = vdd;
        _index = index;
        DeviceName = deviceName;
        RefreshBounds();
    }

    public sealed class DriverMissingException : Exception
    {
        public DriverMissingException() : base("Driver de monitor virtual (Parsec VDD) não encontrado.") { }
    }

    /// <summary>Cria o monitor virtual. Lança DriverMissingException se o driver não estiver instalado.</summary>
    public static VirtualMonitor Create()
    {
        var vdd = ParsecVdd.TryOpen() ?? throw new DriverMissingException();
        try
        {
            var before = new HashSet<string>(ParsecDevices(attachedOnly: true));
            int index = vdd.AddDisplay();
            if (index < 0) throw new InvalidOperationException("O driver recusou a criação do monitor virtual.");

            // O Windows leva um instante para ligar o novo monitor à área de trabalho.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool forcedExtend = false;
            while (sw.ElapsedMilliseconds < 8000)
            {
                var added = ParsecDevices(attachedOnly: true).FirstOrDefault(d => !before.Contains(d));
                if (added != null) return new VirtualMonitor(vdd, index, added);
                if (!forcedExtend && sw.ElapsedMilliseconds > 2500)
                {
                    // Provavelmente o Windows está em "Duplicar": força o modo "Estender".
                    SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_TOPOLOGY_EXTEND | SDC_APPLY);
                    forcedExtend = true;
                }
                Thread.Sleep(200);
            }
            vdd.RemoveDisplay(index);
            throw new InvalidOperationException("O monitor virtual foi criado, mas não apareceu na área de trabalho.");
        }
        catch
        {
            vdd.Dispose();
            throw;
        }
    }

    /// <summary>Aplica a resolução mais próxima de reqW×reqH e posiciona do lado escolhido.</summary>
    public (int Width, int Height) ApplyLayout(int reqW, int reqH, DisplaySide side)
    {
        bool portrait = reqH > reqW;
        var (mw, mh) = ChooseMode(DeviceName, Math.Max(reqW, reqH), Math.Min(reqW, reqH));
        int w = portrait ? mh : mw, h = portrait ? mw : mh;

        // Área ocupada pelos demais monitores.
        int minX = int.MaxValue, maxX = int.MinValue;
        foreach (var (name, rect) in AttachedDisplays())
        {
            if (name == DeviceName) continue;
            minX = Math.Min(minX, rect.Left);
            maxX = Math.Max(maxX, rect.Right);
        }
        if (minX == int.MaxValue) { minX = 0; maxX = 0; }

        var dm = DEVMODE.Create();
        EnumDisplaySettingsW(DeviceName, ENUM_CURRENT_SETTINGS, ref dm);
        dm.dmPelsWidth = w;
        dm.dmPelsHeight = h;
        dm.dmDisplayOrientation = portrait ? 1 /* DMDO_90 */ : 0 /* DMDO_DEFAULT */;
        dm.dmPositionX = side == DisplaySide.Left ? minX - w : maxX;
        dm.dmPositionY = 0; // alinhado ao topo do monitor principal
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_POSITION | 0x80 /* DM_DISPLAYORIENTATION */;

        int r = ChangeDisplaySettingsExW(DeviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        if (r != DISP_CHANGE_SUCCESSFUL && portrait)
        {
            // Sem suporte a rotação: usa o modo paisagem mesmo (o iPad mostra com faixas pretas).
            dm.dmPelsWidth = mw; dm.dmPelsHeight = mh; dm.dmDisplayOrientation = 0;
            dm.dmPositionX = side == DisplaySide.Left ? minX - mw : maxX;
            ChangeDisplaySettingsExW(DeviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        }
        ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); // aplica tudo de uma vez

        Thread.Sleep(300);
        RefreshBounds();
        return (Bounds.Width, Bounds.Height);
    }

    public void RefreshBounds()
    {
        var dm = DEVMODE.Create();
        if (EnumDisplaySettingsW(DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
            Bounds = new Rectangle(dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight);
    }

    public void Dispose()
    {
        try { _vdd.RemoveDisplay(_index); } catch { /* ao sair, o driver remove sozinho sem o keep-alive */ }
        _vdd.Dispose();
    }

    // ---------------- Auxiliares ----------------

    /// <summary>Escolhe, entre os modos que o driver oferece, o mais parecido em proporção e área.</summary>
    private static (int, int) ChooseMode(string device, int wantW, int wantH)
    {
        var modes = new HashSet<(int, int)>();
        var dm = DEVMODE.Create();
        for (int i = 0; EnumDisplaySettingsW(device, i, ref dm); i++)
        {
            if (dm.dmPelsWidth >= dm.dmPelsHeight) modes.Add((dm.dmPelsWidth, dm.dmPelsHeight));
            dm = DEVMODE.Create();
        }
        if (modes.Count == 0) return (1920, 1080);

        double wantAr = (double)wantW / wantH, wantArea = (double)wantW * wantH;
        return modes
            .OrderBy(m =>
            {
                double ar = (double)m.Item1 / m.Item2;
                return Math.Abs(ar / wantAr - 1) * 10 + Math.Abs(Math.Log(m.Item1 * (double)m.Item2 / wantArea));
            })
            .First();
    }

    private static IEnumerable<string> ParsecDevices(bool attachedOnly)
    {
        var dd = DISPLAY_DEVICE.Create();
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            bool attached = (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            if (dd.DeviceString.Contains("Parsec", StringComparison.OrdinalIgnoreCase) && (!attachedOnly || attached))
                yield return dd.DeviceName;
            dd = DISPLAY_DEVICE.Create();
        }
    }

    private static IEnumerable<(string Name, Rectangle Rect)> AttachedDisplays()
    {
        var dd = DISPLAY_DEVICE.Create();
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                var dm = DEVMODE.Create();
                if (EnumDisplaySettingsW(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
                    yield return (dd.DeviceName, new Rectangle(dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight));
            }
            dd = DISPLAY_DEVICE.Create();
        }
    }
}
