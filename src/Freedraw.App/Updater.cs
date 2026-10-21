using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Freedraw.App;

/// <summary>
/// Auto-atualizacao pelas releases do GitHub, sem instalador.
///
/// Fluxo: consulta a release mais recente, baixa Freedraw.exe ao lado do exe
/// atual, confere contra o Freedraw.exe.sha256 publicado junto, e TROCA O
/// ARQUIVO com o app rodando -- o Windows deixa renomear um .exe em execucao,
/// so nao deixa apagar. O exe velho vira Freedraw.old.exe e e apagado na
/// proxima abertura. A versao nova vale no proximo start; o app nao reinicia
/// sozinho para nao derrubar ninguem no meio de um desenho.
///
/// So liga no exe gerado pelo workflow de release (metadado Freedraw.AutoUpdate,
/// ver Freedraw.App.csproj). Sem essa trava, um `dotnet run` baixaria a release
/// e substituiria o exe de desenvolvimento.
/// </summary>
public sealed class Updater : IDisposable
{
    private const string Repo = "lIlIlIIIlll/freedraw";
    private const string AssetName = "Freedraw.exe";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Freedraw", "update.log");

    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    public static bool Enabled { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == "Freedraw.AutoUpdate" && a.Value == "true") == true
        && Environment.ProcessPath is not null;

    /// <summary>Versao ja baixada e trocada no disco, esperando o proximo start.
    /// Escrito pela thread do updater, lido pelo Tick da UI.</summary>
    public Version? Installed => Volatile.Read(ref _installed);
    private Version? _installed;

    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _http;

    public Updater()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // A API do GitHub recusa requisicao sem User-Agent.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Freedraw", CurrentVersion.ToString()));
    }

    public void Start()
    {
        if (!Enabled) return;
        _ = Task.Run(() => LoopAsync(_stop.Token));
    }

    /// <summary>Apaga o exe trocado na atualizacao anterior. Chamado no start,
    /// quando ninguem mais o esta executando.</summary>
    public static void CleanupPreviousUpdate()
    {
        if (Environment.ProcessPath is not { } exe) return;
        try { File.Delete(OldPath(exe)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(FirstCheckDelay, stop);
            while (_installed is null)
            {
                try { await CheckAsync(stop); }
                catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    // Sem rede, GitHub fora, disco cheio: tenta de novo no proximo ciclo.
                    Log($"falhou: {ex.GetType().Name}: {ex.Message}");
                }
                await Task.Delay(CheckInterval, stop);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckAsync(CancellationToken stop)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, stop);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stop));
        var root = json.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";

        // Tag fora do padrao vX.Y.Z (ex.: a "release" da v1.0.0) nao e versao:
        // ignorar em vez de adivinhar.
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
        {
            Log($"tag '{tag}' nao e versao; nada a fazer");
            return;
        }
        if (Normalize(latest) <= Normalize(CurrentVersion))
        {
            Log($"em dia (atual {CurrentVersion.ToString(3)}, release {tag})");
            return;
        }

        string? exeUrl = null, hashUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (name == AssetName) exeUrl = url;
            else if (name == AssetName + ".sha256") hashUrl = url;
        }
        // Sem hash nao instala: e a unica garantia de que o arquivo que chegou
        // e o que o workflow publicou.
        if (exeUrl is null || hashUrl is null || !FromThisRepo(exeUrl) || !FromThisRepo(hashUrl))
        {
            Log($"release {tag} sem {AssetName} e {AssetName}.sha256 deste repo; ignorada");
            return;
        }

        string expected = (await _http.GetStringAsync(hashUrl, stop)).Split(' ', '\t', '\n', '\r')[0].Trim().ToLowerInvariant();

        string exe = Environment.ProcessPath!;
        string downloaded = Path.Combine(Path.GetDirectoryName(exe)!, "Freedraw.new.exe");
        try
        {
            await using (var source = await _http.GetStreamAsync(exeUrl, stop))
            await using (var target = File.Create(downloaded))
                await source.CopyToAsync(target, stop);

            string actual;
            await using (var file = File.OpenRead(downloaded))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, stop));
            if (actual != expected)
            {
                Log($"hash nao confere para {tag} (esperado {expected}, veio {actual}); descartado");
                return;
            }

            Swap(exe, downloaded);
        }
        finally
        {
            try { File.Delete(downloaded); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        Volatile.Write(ref _installed, latest);
        Log($"atualizado para {tag}; vale no proximo start");
    }

    /// <summary>Renomeia o exe em execucao e poe o novo no lugar. Se o segundo
    /// passo falhar, desfaz o primeiro: nao pode sobrar uma pasta sem Freedraw.exe.</summary>
    private static void Swap(string exe, string downloaded)
    {
        string old = OldPath(exe);
        File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(downloaded, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    private static string OldPath(string exe) => Path.Combine(Path.GetDirectoryName(exe)!, "Freedraw.old.exe");

    private static bool FromThisRepo(string url) =>
        url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static Version ReadCurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? Normalize(v) : new Version(0, 0, 0);

    /// <summary>Log curto em %APPDATA%\Freedraw\update.log: e o unico jeito de
    /// descobrir por que o PC de alguem nao atualizou.</summary>
    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // Mantem o arquivo pequeno: so as ultimas ~200 linhas.
            var lines = File.Exists(LogPath) ? File.ReadAllLines(LogPath).TakeLast(199).ToList() : [];
            lines.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} v{CurrentVersion.ToString(3)} {message}");
            File.WriteAllLines(LogPath, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
        _stop.Dispose();
    }
}
