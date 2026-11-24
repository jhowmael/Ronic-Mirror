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

/// <summary>Configurações locais simples (%APPDATA%\RonicMirror\settings.json).</summary>
internal sealed class Settings
{
    public DisplaySide Position { get; set; } = DisplaySide.Right;
    public int Width { get; set; } = 1366;
    public int Height { get; set; } = 1024;

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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}

/// <summary>
/// Orquestra: monitor virtual → captura → core-server → iPad (equivalente ao AppModel.swift do macOS).
/// Todas as propriedades são lidas/escritas na thread da interface; o evento Changed avisa a tela.
/// </summary>
internal sealed class AppController : IDisposable
{
    public event Action? Changed;

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
    public DisplaySide Position => _settings.Position;

    private readonly SynchronizationContext _ui;
    private readonly Settings _settings = Settings.Load();
    private readonly ServerProcess _server = new();
    private readonly SourceClient _source = new();
    private readonly ScreenCapturer _capturer;
    private VirtualMonitor? _monitor;
    private volatile int _viewers;
    private System.Threading.Timer? _resizeTimer;

    public AppController()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _capturer = new ScreenCapturer
        {
            Bounds = () => _monitor?.Bounds,
            ShouldCapture = () => _viewers > 0 && _source.IsConnected,
            OnFrame = jpeg => _source.TrySendFrame(jpeg),
        };
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
        _source.ConnectionChanged += connected => { if (connected) Post(SendStreamInfo); };
        _source.Message += msg => Post(() => HandleServerMessage(msg));

        _server.Start();
        _capturer.Start();
        _ = CreateMonitorAsync();
    }

    // ---------------- Monitor virtual ----------------

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
        await ApplyLayoutAsync(_settings.Width, _settings.Height);
    }

    private async Task ApplyLayoutAsync(int w, int h)
    {
        var monitor = _monitor;
        if (monitor == null) return;
        var side = _settings.Position;
        await _displayLock.WaitAsync();
        try
        {
            var (aw, ah) = await Task.Run(() => monitor.ApplyLayout(w, h, side));
            Resolution = $"{aw}×{ah}";
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

    public void SetPosition(DisplaySide side)
    {
        if (_settings.Position == side) return;
        _settings.Position = side;
        _settings.Save();
        _ = ApplyLayoutAsync(_settings.Width, _settings.Height);
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
            case "viewer-resolution":
                int w = msg.GetProperty("width").GetInt32(), h = msg.GetProperty("height").GetInt32();
                // debounce: o iPad pode mandar várias mudanças seguidas ao girar
                _resizeTimer?.Dispose();
                _resizeTimer = new System.Threading.Timer(_ => Post(() =>
                {
                    _settings.Width = w;
                    _settings.Height = h;
                    _settings.Save();
                    _ = ApplyLayoutAsync(w, h);
                }), null, 500, Timeout.Infinite);
                break;
        }
    }

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
        _capturer.Dispose();
        _source.Dispose();
        _server.Dispose();
        _monitor?.Dispose(); // remove o monitor virtual
        _monitor = null;
    }
}
