using System.Drawing;
using static RonicMirror.Native;

namespace RonicMirror;

internal enum DisplaySide { Left, Right, Top, Bottom }

/// <summary>Um monitor ativo, para o editor visual de posição.</summary>
internal sealed record DisplayTileInfo(string DeviceName, Rectangle Frame, string Name, bool IsPrimary, bool IsVirtual);

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

    /// <summary>
    /// Aplica a resolução mais próxima de reqW×reqH e posiciona: num lado (side) ou numa origem livre (custom),
    /// sempre "grudando" na borda do monitor mais próximo. Devolve o tamanho e a origem final.
    /// </summary>
    public (int Width, int Height, Point Origin) ApplyLayout(int reqW, int reqH, DisplaySide side, Point? custom)
    {
        bool portrait = reqH > reqW;
        var (mw, mh) = ChooseMode(DeviceName, Math.Max(reqW, reqH), Math.Min(reqW, reqH));
        int w = portrait ? mh : mw, h = portrait ? mw : mh;

        var origin = ComputeOrigin(new Size(w, h), side, custom);
        var dm = DEVMODE.Create();
        EnumDisplaySettingsW(DeviceName, ENUM_CURRENT_SETTINGS, ref dm);
        dm.dmPelsWidth = w;
        dm.dmPelsHeight = h;
        dm.dmDisplayOrientation = portrait ? 1 /* DMDO_90 */ : 0 /* DMDO_DEFAULT */;
        dm.dmPositionX = origin.X;
        dm.dmPositionY = origin.Y;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_POSITION | 0x80 /* DM_DISPLAYORIENTATION */;

        int r = ChangeDisplaySettingsExW(DeviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        if (r != DISP_CHANGE_SUCCESSFUL && portrait)
        {
            // Sem suporte a rotação: usa o modo paisagem mesmo (o aparelho mostra com faixas pretas).
            origin = ComputeOrigin(new Size(mw, mh), side, custom);
            dm.dmPelsWidth = mw; dm.dmPelsHeight = mh; dm.dmDisplayOrientation = 0;
            dm.dmPositionX = origin.X; dm.dmPositionY = origin.Y;
            ChangeDisplaySettingsExW(DeviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        }
        ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); // aplica tudo de uma vez

        Thread.Sleep(300);
        RefreshBounds();
        return (Bounds.Width, Bounds.Height, Bounds.Location);
    }

    private Point ComputeOrigin(Size size, DisplaySide side, Point? custom)
    {
        var others = AttachedDisplays().Where(d => d.Name != DeviceName).Select(d => d.Rect).ToList();
        if (others.Count == 0) return Point.Empty;
        var union = others.Aggregate(Rectangle.Union);
        var primary = others.FirstOrDefault(r => r.X == 0 && r.Y == 0, others[0]);

        Point desired = custom ?? side switch
        {
            DisplaySide.Left => new Point(union.Left - size.Width, primary.Top),
            DisplaySide.Right => new Point(union.Right, primary.Top),
            DisplaySide.Top => new Point(primary.Left + primary.Width / 2 - size.Width / 2, union.Top - size.Height),
            _ => new Point(primary.Left + primary.Width / 2 - size.Width / 2, union.Bottom),
        };
        return Snap(new Rectangle(desired, size), others);
    }

    /// <summary>
    /// Encosta o retângulo na borda mais próxima de algum monitor, sem sobrepor nenhum
    /// (o Windows exige que os monitores se toquem).
    /// </summary>
    public static Point Snap(Rectangle r, IReadOnlyList<Rectangle> others, int minOverlap = 80)
    {
        static int Clamp(int v, int lo, int hi) => lo > hi ? (lo + hi) / 2 : Math.Min(Math.Max(v, lo), hi);
        bool OverlapsAny(Rectangle c) => others.Any(o => { var i = Rectangle.Intersect(o, c); return i.Width > 0 && i.Height > 0; });

        Point? best = null;
        double bestDist = double.MaxValue;
        foreach (var d in others)
        {
            int my = Math.Min(minOverlap, Math.Min(r.Height / 2, d.Height / 2));
            int mx = Math.Min(minOverlap, Math.Min(r.Width / 2, d.Width / 2));
            int y = Clamp(r.Y, d.Top - r.Height + my, d.Bottom - my);
            int x = Clamp(r.X, d.Left - r.Width + mx, d.Right - mx);
            var candidates = new[]
            {
                new Point(d.Right, y),            // à direita de d
                new Point(d.Left - r.Width, y),   // à esquerda de d
                new Point(x, d.Bottom),           // abaixo de d
                new Point(x, d.Top - r.Height),   // acima de d
            };
            foreach (var c in candidates)
            {
                if (OverlapsAny(new Rectangle(c, r.Size))) continue;
                double dist = Math.Sqrt(Math.Pow(c.X - r.X, 2) + Math.Pow(c.Y - r.Y, 2));
                if (dist < bestDist) { bestDist = dist; best = c; }
            }
        }
        return best ?? r.Location;
    }

    /// <summary>Todos os monitores ativos, para o editor visual.</summary>
    public static List<DisplayTileInfo> Layout(IReadOnlyDictionary<string, string> virtualNames)
    {
        var list = new List<DisplayTileInfo>();
        int n = 1;
        foreach (var (name, rect) in AttachedDisplays())
        {
            bool isVirtual = virtualNames.ContainsKey(name);
            bool primary = rect.X == 0 && rect.Y == 0;
            string label = isVirtual ? virtualNames[name] : primary ? "Este PC" : $"Monitor {++n}";
            list.Add(new DisplayTileInfo(name, rect, label, primary, isVirtual));
        }
        return list;
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
