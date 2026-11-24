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

/// <summary>Configurações locais simples (%APPDATA%\RonicMirror\settings.json).</summary>
internal sealed class Settings
{
    public DisplaySide Position { get; set; } = DisplaySide.Right;
    public bool CustomPlacement { get; set; }
    public int CustomX { get; set; }
    public int CustomY { get; set; }
    public int Width { get; set; } = 1366;
    public int Height { get; set; } = 1024;
    public bool RequireApproval { get; set; } = true;
    public List<SavedDevice> Trusted { get; set; } = new();
    public List<SavedDevice> Blocked { get; set; } = new();

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
/// Orquestra: monitor virtual → captura → core-server → aparelhos (equivalente ao AppModel.swift do macOS).
/// Todas as propriedades são lidas/escritas na thread da interface; o evento Changed avisa a tela.
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
    public string Resolution { get; private set; } = "";
    public int Viewers { get; private set; }
    public IReadOnlyList<DeviceInfo> Devices { get; private set; } = Array.Empty<DeviceInfo>();
    public IReadOnlyList<SavedDevice> Trusted => _settings.Trusted;
    public IReadOnlyList<SavedDevice> Blocked => _settings.Blocked;
    public bool RequireApproval => _settings.RequireApproval;
    public DisplaySide Position => _settings.Position;
    public bool CustomPlacement => _settings.CustomPlacement;
    public string? VirtualDeviceName => _monitor?.DeviceName;

    private readonly SynchronizationContext _ui;
    private readonly Settings _settings = Settings.Load();
    private readonly ServerProcess _server = new();
    private readonly SourceClient _source = new();
    private readonly ScreenCapturer _capturer;
    private readonly CursorStreamer _cursor;
    private VirtualMonitor? _monitor;
    private volatile int _viewers;
    private System.Threading.Timer? _resizeTimer;
    private readonly HashSet<string> _prompted = new();

    public AppController()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _capturer = new ScreenCapturer
        {
            Bounds = () => _monitor?.Bounds,
            ShouldCapture = () => _viewers > 0 && _source.IsConnected,
            OnFrame = jpeg => _source.TrySendFrame(jpeg),
        };
        _cursor = new CursorStreamer(_source, () => _monitor?.Bounds);
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
                SendStreamInfo();
                _cursor.ResendAll();
            });
        };
        _source.Message += msg => Post(() => HandleServerMessage(msg));

        _server.Start();
        _capturer.Start();
        _cursor.Start();
        _ = CreateMonitorAsync();
    }

    // ---------------- Monitor virtual e posição ----------------

    private readonly SemaphoreSlim _displayLock = new(1, 1); // operações de monitor em série, fora da UI

    public async Task CreateMonitorAsync()
    {
        if (_monitor != null) return;
        DisplayStatus = Status.Waiting;
        DisplayError = null;
        Changed?.Invoke();
        await _displayLock.WaitAsync();
        try
        {
            var monitor = await Task.Run(VirtualMonitor.Create);
            _monitor = monitor;
            DriverMissing = false;
            DisplayStatus = Status.Ok;
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
            DisplayError = ex.Message;
        }
        finally
        {
            _displayLock.Release();
        }
        Changed?.Invoke();
        await ApplyLayoutAsync();
    }

    private async Task ApplyLayoutAsync()
    {
        var monitor = _monitor;
        if (monitor == null) return;
        int w = _settings.Width, h = _settings.Height;
        var side = _settings.Position;
        Point? custom = _settings.CustomPlacement ? new Point(_settings.CustomX, _settings.CustomY) : null;
        await _displayLock.WaitAsync();
        try
        {
            var (aw, ah, origin) = await Task.Run(() => monitor.ApplyLayout(w, h, side, custom));
            Resolution = $"{aw}×{ah}";
            if (_settings.CustomPlacement)
            {
                // guarda a posição já ajustada à borda
                _settings.CustomX = origin.X;
                _settings.CustomY = origin.Y;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            DisplayError = "Falha ao ajustar o monitor: " + ex.Message;
        }
        finally
        {
            _displayLock.Release();
        }
        _capturer.ForceNextFrame();
        SendStreamInfo();
        Changed?.Invoke();
    }

    /// <summary>Posição rápida: Esquerda / Direita / Acima / Abaixo.</summary>
    public void SetPosition(DisplaySide side)
    {
        _settings.Position = side;
        _settings.CustomPlacement = false;
        _settings.Save();
        _ = ApplyLayoutAsync();
    }

    /// <summary>Posição livre (arrastando no editor visual).</summary>
    public void MoveTo(Point origin)
    {
        _settings.CustomPlacement = true;
        _settings.CustomX = origin.X;
        _settings.CustomY = origin.Y;
        _settings.Save();
        _ = ApplyLayoutAsync();
    }

    public List<DisplayTileInfo> DisplayLayout() => VirtualMonitor.Layout(_monitor?.DeviceName);

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
            await CreateMonitorAsync();
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
        switch (msg.TryGetProperty("type", out var t) ? t.GetString() : null)
        {
            case "server-info":
                if (msg.TryGetProperty("urls", out var urls))
                    ApplyUrls(urls.EnumerateArray().Select(x => x.GetString() ?? "").ToArray());
                break;
            case "viewers":
                Viewers = _viewers = msg.GetProperty("count").GetInt32();
                if (_viewers > 0) _capturer.ForceNextFrame();
                break;
            case "keyframe-request":
                _capturer.ForceNextFrame(); // no JPEG todo frame é completo: só reenvia a tela atual
                break;
            case "devices":
                UpdateDevices(msg.GetProperty("devices"));
                break;
            case "viewer-resolution":
                int w = msg.GetProperty("width").GetInt32(), h = msg.GetProperty("height").GetInt32();
                // debounce: o aparelho pode mandar várias mudanças seguidas ao girar
                _resizeTimer?.Dispose();
                _resizeTimer = new System.Threading.Timer(_ => Post(() =>
                {
                    _settings.Width = w;
                    _settings.Height = h;
                    _settings.Save();
                    _ = ApplyLayoutAsync();
                }), null, 500, Timeout.Infinite);
                break;
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

        foreach (var d in Devices.Where(d => d.IsPending && _prompted.Add(d.Conn)))
            ApprovalRequested?.Invoke(d);
    }

    private static string Str(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private void SendStreamInfo()
    {
        if (_monitor == null) return;
        var b = _monitor.Bounds;
        _ = _source.SendJsonAsync(new { type = "stream-info", codec = "jpeg", width = b.Width, height = b.Height, fps = _capturer.Fps });
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
        _resizeTimer?.Dispose();
        _cursor.Dispose();
        _capturer.Dispose();
        _source.Dispose();
        _server.Dispose();
        _monitor?.Dispose(); // remove o monitor virtual
        _monitor = null;
    }
}
