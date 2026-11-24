using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text.Json;

namespace RonicMirror;

internal static class AppInfo
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0";
}

internal enum Status { Waiting, Ok, Error }

/// <summary>Aparelho conectado ou aguardando autorização (vem do core-server).</summary>
internal sealed record DeviceInfo(string Conn, string DeviceId, string Name, string Ip, string State,
                                  DateTime Since, string Codec, int Width, int Height)
{
    public bool IsPending => State == "pending";
}

/// <summary>Aparelho lembrado (confiável ou bloqueado).</summary>
internal sealed record SavedDevice(string DeviceId, string Name, string Ip);

/// <summary>Posição salva da tela de um aparelho.</summary>
internal sealed class SavedPlacement
{
    public DisplaySide Side { get; set; } = DisplaySide.Right;
    public bool Custom { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Configurações locais simples (%APPDATA%\RonicMirror\settings.json).</summary>
internal sealed class Settings
{
    public bool RequireApproval { get; set; } = true;
    public List<SavedDevice> Trusted { get; set; } = new();
    public List<SavedDevice> Blocked { get; set; } = new();
    public Dictionary<string, SavedPlacement> Placements { get; set; } = new();

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RonicMirror", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>
/// Orquestra: uma tela virtual POR APARELHO → captura → core-server → aparelho
/// (equivalente ao AppModel.swift do macOS). Propriedades lidas/escritas na thread da interface.
/// </summary>
internal sealed class AppController : IDisposable
{
    public event Action? Changed;
    /// <summary>Um aparelho novo pediu para conectar (a tela mostra o pedido de autorização).</summary>
    public event Action<DeviceInfo>? ApprovalRequested;

    public string Url { get; private set; } = "Iniciando…";
    public string[] OtherUrls { get; private set; } = Array.Empty<string>();
    public Status ServerStatus { get; private set; } = Status.Waiting;
    public string? ServerError { get; private set; }
    public Status DisplayStatus { get; private set; } = Status.Waiting;
    public string? DisplayError { get; private set; }
    public bool DriverMissing { get; private set; }
    public bool InstallingDriver { get; private set; }
    public int Viewers { get; private set; }
    public IReadOnlyList<DeviceInfo> Devices { get; private set; } = Array.Empty<DeviceInfo>();
    public IReadOnlyList<DeviceScreen> Screens => _screens;
    public IReadOnlyList<SavedDevice> Trusted => _settings.Trusted;
    public IReadOnlyList<SavedDevice> Blocked => _settings.Blocked;
    public bool RequireApproval => _settings.RequireApproval;

    private readonly SynchronizationContext _ui;
    private readonly Settings _settings = Settings.Load();
    private readonly ServerProcess _server = new();
    private readonly SourceClient _source = new();
    private readonly CursorStreamer _cursor;
    private readonly InputInjector _input = new();
    private readonly List<DeviceScreen> _screens = new();
    private volatile Dictionary<string, DeviceScreen> _screenByConn = new(); // cópia para a thread de rede
    private readonly HashSet<string> _creating = new();
    private readonly HashSet<string> _prompted = new();
    private readonly Dictionary<string, System.Threading.Timer> _resizeTimers = new();
    private readonly SemaphoreSlim _displayLock = new(1, 1); // operações de monitor em série, fora da UI

    public AppController()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _cursor = new CursorStreamer(_source);
    }

    public void Start()
    {
        _server.Ready += (port, urls) => Post(() =>
        {
            ServerStatus = Status.Ok;
            ApplyUrls(urls);
            _source.Connect(port);
        });
        _server.Failed += msg => Post(() => { ServerStatus = Status.Error; ServerError = msg; });
        _source.ConnectionChanged += connected =>
        {
            if (!connected) return;
            Post(() =>
            {
                SendPolicy();
                foreach (var s in _screens) SendStreamInfo(s);
                _cursor.ResendAll();
            });
        };
        _source.Message += msg =>
        {
            // Toques: tratados direto na thread de rede, sem esperar a interface (resposta imediata).
            if (msg.TryGetProperty("type", out var t) && t.GetString() == "input")
                HandleInput(msg, msg.TryGetProperty("conn", out var c) ? c.GetString() : null);
            else
                Post(() => HandleServerMessage(msg));
        };

        _server.Start();
        _cursor.Start();
        _ = CheckDriverAsync();
    }

    // ---------------- Driver ----------------

    /// <summary>Verifica se o driver de monitor virtual está instalado (sem criar monitor).</summary>
    public async Task CheckDriverAsync()
    {
        bool ok = await Task.Run(() => { using var vdd = ParsecVdd.TryOpen(); return vdd != null; });
        DriverMissing = !ok;
        DisplayStatus = ok ? Status.Ok : Status.Error;
        DisplayError = ok ? null : new VirtualMonitor.DriverMissingException().Message;
        Changed?.Invoke();
        if (ok) SyncScreens();
    }

    /// <summary>Baixa e executa o instalador do driver (pede permissão de administrador).</summary>
    public async Task InstallDriverAsync()
    {
        if (InstallingDriver) return;
        InstallingDriver = true;
        Changed?.Invoke();
        try
        {
            string path = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(ParsecVdd.DriverDownloadUrl).LocalPath));
            using (var http = new HttpClient())
            {
                var bytes = await http.GetByteArrayAsync(ParsecVdd.DriverDownloadUrl);
                await File.WriteAllBytesAsync(path, bytes);
            }
            var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", Arguments = "/S" });
            if (p != null) await p.WaitForExitAsync();
            await Task.Delay(1500); // o driver leva um instante para iniciar
            await CheckDriverAsync();
        }
        catch (Exception ex)
        {
            DisplayError = "Não foi possível instalar automaticamente: " + ex.Message +
                           " — baixe e instale manualmente pelo link e clique em Tentar novamente.";
            OpenDriverPage();
        }
        finally
        {
            InstallingDriver = false;
            Changed?.Invoke();
        }
    }

    public static void OpenDriverPage() =>
        Process.Start(new ProcessStartInfo(ParsecVdd.DriverDownloadUrl) { UseShellExecute = true });

    // ---------------- Telas: uma por aparelho ----------------

    private void SyncScreens()
    {
        var active = Devices.Where(d => !d.IsPending).ToList();
        var activeConns = active.Select(d => d.Conn).ToHashSet();

        // Saíram: o monitor virtual some.
        foreach (var gone in _screens.Where(s => !activeConns.Contains(s.Conn)).ToList())
        {
            _screens.Remove(gone);
            var g = gone;
            _ = Task.Run(async () =>
            {
                await _displayLock.WaitAsync();
                try { g.Dispose(); } finally { _displayLock.Release(); }
            });
        }

        if (DriverMissing) { UpdateCursorTargets(); return; }

        foreach (var d in active)
        {
            if (_screens.Any(s => s.Conn == d.Conn) || !_creating.Add(d.Conn)) continue;
            _ = CreateScreenAsync(d);
        }
        UpdateCursorTargets();
    }

    private async Task CreateScreenAsync(DeviceInfo device)
    {
        await _displayLock.WaitAsync();
        try
        {
            if (!Devices.Any(d => d.Conn == device.Conn && !d.IsPending)) return; // saiu enquanto esperava
            var monitor = await Task.Run(VirtualMonitor.Create);
            var screen = new DeviceScreen(device, monitor, _source);
            var placement = PlacementFor(screen.DeviceKey);
            var (w, h, _) = await Task.Run(() => monitor.ApplyLayout(screen.RequestedWidth, screen.RequestedHeight,
                placement.Side, placement.Custom ? new Point(placement.X, placement.Y) : null));
            screen.SizeLabel = $"{w}×{h}";
            _screens.Add(screen);
            screen.Capturer.Start();
            UpdateCursorTargets();
            SendStreamInfo(screen);
            DisplayStatus = Status.Ok;
            DisplayError = null;
        }
        catch (VirtualMonitor.DriverMissingException ex)
        {
            DriverMissing = true;
            DisplayStatus = Status.Error;
            DisplayError = ex.Message;
        }
        catch (Exception ex)
        {
            DisplayStatus = Status.Error;
            DisplayError = $"Não foi possível criar a tela de {device.Name}: {ex.Message}";
        }
        finally
        {
            _creating.Remove(device.Conn);
            _displayLock.Release();
        }
        // Se o aparelho saiu enquanto a tela era criada, a próxima sincronização a remove.
        SyncScreens();
        Changed?.Invoke();
    }

    private async Task ApplyLayoutAsync(DeviceScreen screen)
    {
        var placement = PlacementFor(screen.DeviceKey);
        await _displayLock.WaitAsync();
        try
        {
            if (!_screens.Contains(screen)) return;
            var (w, h, origin) = await Task.Run(() => screen.Monitor.ApplyLayout(screen.RequestedWidth, screen.RequestedHeight,
                placement.Side, placement.Custom ? new Point(placement.X, placement.Y) : null));
            screen.SizeLabel = $"{w}×{h}";
            if (placement.Custom)
            {
                // guarda a posição já ajustada à borda
                placement.X = origin.X;
                placement.Y = origin.Y;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            DisplayError = "Falha ao ajustar a tela: " + ex.Message;
        }
        finally
        {
            _displayLock.Release();
        }
        screen.Capturer.ForceNextFrame();
        SendStreamInfo(screen);
        Changed?.Invoke();
    }

    private SavedPlacement PlacementFor(string key)
    {
        if (!_settings.Placements.TryGetValue(key, out var p))
        {
            p = new SavedPlacement();
            _settings.Placements[key] = p;
        }
        return p;
    }

    /// <summary>Posição rápida: Esquerda / Direita / Acima / Abaixo.</summary>
    public void SetPosition(DeviceScreen screen, DisplaySide side)
    {
        var p = PlacementFor(screen.DeviceKey);
        p.Side = side;
        p.Custom = false;
        _settings.Save();
        _ = ApplyLayoutAsync(screen);
    }

    /// <summary>Posição livre (arrastando no editor visual).</summary>
    public void MoveTo(string deviceName, Point origin)
    {
        var screen = _screens.FirstOrDefault(s => s.Monitor.DeviceName == deviceName);
        if (screen == null) return;
        var p = PlacementFor(screen.DeviceKey);
        p.Custom = true;
        p.X = origin.X;
        p.Y = origin.Y;
        _settings.Save();
        _ = ApplyLayoutAsync(screen);
    }

    public SavedPlacement PlacementOf(DeviceScreen screen) => PlacementFor(screen.DeviceKey);

    public List<DisplayTileInfo> DisplayLayout() =>
        VirtualMonitor.Layout(_screens.ToDictionary(s => s.Monitor.DeviceName, s => s.Name));

    private void UpdateCursorTargets()
    {
        _cursor.SetTargets(_screens.Select(s => (s.Prefix, (Func<Rectangle>)(() => s.Monitor.Bounds))).ToList());
        _screenByConn = _screens.ToDictionary(s => s.Conn);
    }

    // ---------------- Aparelhos: autorização, desconectar, bloquear ----------------

    public void SetRequireApproval(bool value)
    {
        _settings.RequireApproval = value;
        _settings.Save();
        SendPolicy();
        Changed?.Invoke();
    }

    public void Accept(DeviceInfo d, bool trust)
    {
        if (trust && d.DeviceId != "" && !_settings.Trusted.Any(t => t.DeviceId == d.DeviceId))
        {
            _settings.Trusted.Add(new SavedDevice(d.DeviceId, d.Name, d.Ip));
            _settings.Save();
            SendPolicy();
        }
        DeviceAction(d, "accept");
    }

    public void Reject(DeviceInfo d) => DeviceAction(d, "reject");

    public void Disconnect(DeviceInfo d) => DeviceAction(d, "kick");

    public void Block(DeviceInfo d)
    {
        _settings.Trusted.RemoveAll(t => d.DeviceId != "" && t.DeviceId == d.DeviceId);
        if (!_settings.Blocked.Any(b => b.DeviceId == d.DeviceId && b.Ip == d.Ip))
            _settings.Blocked.Add(new SavedDevice(d.DeviceId, d.Name, d.Ip));
        _settings.Save();
        DeviceAction(d, "block");
        SendPolicy();
        Changed?.Invoke();
    }

    public void Unblock(SavedDevice s)
    {
        _settings.Blocked.Remove(s);
        _settings.Save();
        SendPolicy();
        Changed?.Invoke();
    }

    public void Forget(SavedDevice s)
    {
        _settings.Trusted.Remove(s);
        _settings.Save();
        SendPolicy();
        Changed?.Invoke();
    }

    public bool IsTrusted(DeviceInfo d) => d.DeviceId != "" && _settings.Trusted.Any(t => t.DeviceId == d.DeviceId);

    public bool IsStillPending(string conn) => Devices.Any(d => d.Conn == conn && d.IsPending);

    private void DeviceAction(DeviceInfo d, string action) =>
        _ = _source.SendJsonAsync(new { type = "device-action", conn = d.Conn, action });

    private void SendPolicy() =>
        _ = _source.SendJsonAsync(new
        {
            type = "policy",
            requireApproval = _settings.RequireApproval,
            trusted = _settings.Trusted.Select(t => t.DeviceId).Where(s => s != "").ToArray(),
            blockedIds = _settings.Blocked.Select(b => b.DeviceId).Where(s => s != "").ToArray(),
            blockedIps = _settings.Blocked.Select(b => b.Ip).Where(s => s != "").ToArray(),
        });

    // ---------------- Servidor ----------------

    private void HandleServerMessage(JsonElement msg)
    {
        string? conn = msg.TryGetProperty("conn", out var c) ? c.GetString() : null;
        switch (msg.TryGetProperty("type", out var t) ? t.GetString() : null)
        {
            case "server-info":
                if (msg.TryGetProperty("urls", out var urls))
                    ApplyUrls(urls.EnumerateArray().Select(x => x.GetString() ?? "").ToArray());
                break;
            case "viewers":
                Viewers = msg.GetProperty("count").GetInt32();
                break;
            case "keyframe-request":
                // no JPEG todo frame é completo: só reenvia a tela atual
                foreach (var s in _screens.Where(s => conn == null || s.Conn == conn)) s.Capturer.ForceNextFrame();
                break;
            case "devices":
                UpdateDevices(msg.GetProperty("devices"));
                break;
            case "viewer-resolution":
                if (conn == null) break;
                int w = msg.GetProperty("width").GetInt32(), h = msg.GetProperty("height").GetInt32();
                // debounce: o aparelho pode mandar várias mudanças seguidas ao girar
                if (_resizeTimers.TryGetValue(conn, out var old)) old.Dispose();
                _resizeTimers[conn] = new System.Threading.Timer(_ => Post(() =>
                {
                    var screen = _screens.FirstOrDefault(s => s.Conn == conn);
                    if (screen == null || (screen.RequestedWidth == w && screen.RequestedHeight == h)) return;
                    screen.RequestedWidth = w;
                    screen.RequestedHeight = h;
                    _ = ApplyLayoutAsync(screen);
                }), null, 500, Timeout.Infinite);
                break;
        }
    }

    /// <summary>Toque no aparelho → mouse no Windows.</summary>
    private void HandleInput(JsonElement msg, string? conn)
    {
        if (conn == null || !_screenByConn.TryGetValue(conn, out var screen)) return;
        double Num(string name) => msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var p = screen.ToDesktop(Num("x"), Num("y"));
        var b = screen.Monitor.Bounds;
        switch (msg.TryGetProperty("kind", out var k) ? k.GetString() : null)
        {
            case "tap": _input.Click(p, right: false); break;
            case "rtap": _input.Click(p, right: true); break;
            case "scroll": _input.Scroll(p, Num("dx") * b.Width, Num("dy") * b.Height); break;
            case "down": _input.DragBegin(p); break;
            case "move": _input.DragMove(p); break;
            case "up": _input.DragEnd(p); break;
        }
    }

    private void UpdateDevices(JsonElement list)
    {
        Devices = list.EnumerateArray().Select(d => new DeviceInfo(
            Str(d, "conn"), Str(d, "deviceId"), Str(d, "name", "Aparelho"), Str(d, "ip"), Str(d, "state"),
            DateTimeOffset.FromUnixTimeMilliseconds(d.TryGetProperty("since", out var s) ? s.GetInt64() : 0).LocalDateTime,
            Str(d, "codec") == "h264" ? "H.264" : "JPEG",
            d.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
            d.TryGetProperty("height", out var h) ? h.GetInt32() : 0)).ToList();

        SyncScreens();
        foreach (var d in Devices.Where(d => d.IsPending && _prompted.Add(d.Conn)))
            ApprovalRequested?.Invoke(d);
    }

    private static string Str(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private void SendStreamInfo(DeviceScreen s)
    {
        var b = s.Monitor.Bounds;
        _ = _source.SendJsonAsync(new { type = "stream-info", conn = s.Conn, codec = "jpeg", width = b.Width, height = b.Height, fps = s.Capturer.Fps });
    }

    private void ApplyUrls(string[] urls)
    {
        if (urls.Length == 0) return;
        Url = urls[0];
        OtherUrls = urls.Skip(1).ToArray();
    }

    private void Post(Action action) => _ui.Post(_ =>
    {
        action();
        Changed?.Invoke();
    }, null);

    public void Dispose()
    {
        foreach (var t in _resizeTimers.Values) t.Dispose();
        _cursor.Dispose();
        _source.Dispose();
        _server.Dispose();
        foreach (var s in _screens) s.Dispose(); // remove os monitores virtuais
        _screens.Clear();
    }
}
