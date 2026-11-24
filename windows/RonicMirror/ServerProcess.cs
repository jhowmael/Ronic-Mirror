using System.Diagnostics;
using System.Text.Json;

namespace RonicMirror;

/// <summary>
/// Inicia o binário multiplataforma core-server.exe como processo filho (contrato: docs/PROTOCOL.md).
/// O stdin fica aberto; se o app morrer, o pipe fecha e o servidor encerra sozinho.
/// </summary>
internal sealed class ServerProcess : IDisposable
{
    public event Action<int, string[]>? Ready;
    public event Action<string>? Failed;

    private Process? _process;

    public void Start(int preferredPort = 3000)
    {
        string? exe = Locate("RONIC_MIRROR_SERVER", "core-server.exe", Path.Combine("core-server", "bin", "core-server.exe"));
        if (exe == null)
        {
            Failed?.Invoke("core-server.exe não encontrado ao lado do RonicMirror.exe.");
            return;
        }
        string? web = LocateWeb();

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(preferredPort.ToString());
        psi.ArgumentList.Add("--exit-on-stdin-close");
        if (web != null)
        {
            psi.ArgumentList.Add("--web");
            psi.ArgumentList.Add(web);
        }

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => HandleLine(e.Data);
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.WriteLine("[core-server] " + e.Data); };
        p.Exited += (_, _) => Failed?.Invoke($"O servidor local encerrou (código {SafeExitCode(p)}).");
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _process = p;
        }
        catch (Exception ex)
        {
            Failed?.Invoke("Falha ao iniciar o servidor: " + ex.Message);
        }
    }

    private void HandleLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.GetProperty("event").GetString())
            {
                case "ready":
                    int port = root.GetProperty("port").GetInt32();
                    var urls = root.TryGetProperty("urls", out var u)
                        ? u.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToArray()
                        : Array.Empty<string>();
                    Ready?.Invoke(port, urls);
                    break;
                case "error":
                    Failed?.Invoke(root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "Erro no servidor.");
                    break;
            }
        }
        catch (JsonException) { /* linha que não é evento */ }
        catch (KeyNotFoundException) { }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    public void Dispose()
    {
        var p = _process;
        _process = null;
        if (p == null) return;
        p.EnableRaisingEvents = false;
        try
        {
            p.StandardInput.Close();
            if (!p.WaitForExit(1500)) p.Kill(entireProcessTree: true);
        }
        catch { }
        p.Dispose();
    }

    // ---------------- Localização (pacote distribuído ou repositório em desenvolvimento) ----------------

    private static string? Locate(string envVar, string besideExe, string repoRelative)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var beside = Path.Combine(AppContext.BaseDirectory, besideExe);
        if (File.Exists(beside)) return beside;
        return WalkUp(repoRelative, File.Exists);
    }

    private static string? LocateWeb()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "web");
        if (File.Exists(Path.Combine(beside, "index.html"))) return beside;
        return WalkUp("shared-web", d => File.Exists(Path.Combine(d, "index.html")));
    }

    private static string? WalkUp(string relative, Func<string, bool> exists)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
