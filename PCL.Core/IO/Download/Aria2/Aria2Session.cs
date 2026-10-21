using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.IO.Download.Aria2;

/// <summary>管理启动器专用的 aria2 RPC 进程；不连接或终止用户已有的 aria2。</summary>
public sealed class Aria2Session : IDisposable
{
    private readonly Process _process;
    private readonly HttpClient _client;
    private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly Lock _lock = new();
    private bool _disposed;

    private Aria2Session(string executable, int port)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        foreach (var argument in new[]
        {
            "--no-conf=true", "--enable-rpc=true", "--rpc-listen-all=false", "--rpc-allow-origin-all=false",
            $"--rpc-listen-port={port}", $"--rpc-secret={_secret}",
            $"--stop-with-process={Environment.ProcessId}", "--enable-color=false", "--console-log-level=error",
            "--quiet=true", "--max-download-result=1024", "--max-concurrent-downloads=256",
            "--file-allocation=none", "--auto-file-renaming=false", "--allow-overwrite=true",
            "--continue=false", "--max-tries=1", "--connect-timeout=30", "--timeout=30",
            "--enable-dht=false", "--enable-dht6=false", "--enable-peer-exchange=false", "--bt-enable-lpd=false"
        }) start.ArgumentList.Add(argument);
        // Environment proxy variables must not override the launcher's per-request proxy policy.
        foreach (var key in start.Environment.Keys.Where(key => key.EndsWith("_proxy", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        _client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/jsonrpc"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        try { _process = Process.Start(start) ?? throw new IOException("无法启动 aria2。"); }
        catch { _client.Dispose(); throw; }
    }

    public bool IsRunning
    {
        get { lock (_lock) return !_disposed && !_process.HasExited; }
    }

    public static async Task<Aria2Session> StartAsync(string executable, CancellationToken cancellationToken = default)
    {
        // Let Windows select a free loopback port. Startup failure (including a port race) is reported to the caller.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var session = new Aria2Session(executable, port);
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            while (session.IsRunning)
            {
                startup.Token.ThrowIfCancellationRequested();
                try
                {
                    await session.CallAsync("aria2.getVersion", [], startup.Token).ConfigureAwait(false);
                    return session;
                }
                catch (HttpRequestException) { }
                await Task.Delay(100, startup.Token).ConfigureAwait(false);
            }
            throw new IOException("aria2 在 RPC 就绪前退出。");
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>返回脱离响应文档生命周期的 JSON 结果；不在异常中暴露请求参数或密钥。</summary>
    public async Task<JsonElement> CallAsync(string method, object[] parameters,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning) throw new IOException("aria2 进程已退出。");
        var authenticated = new object[parameters.Length + 1];
        authenticated[0] = "token:" + _secret;
        parameters.CopyTo(authenticated, 1);
        // aria2's RPC HTTP server requires a known Content-Length; streamed JsonContent can hang on chunked bodies.
        using var content = new StringContent(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = authenticated
        }), Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("", content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("error", out var error))
            throw new IOException($"aria2 RPC 调用失败：{method}（{error.GetProperty("code").GetInt32()}）。");
        return document.RootElement.GetProperty("result").Clone();
    }

    /// <summary>确认任务已停止写文件；无法确认时终止本会话，避免与回退下载器争用文件。</summary>
    public async Task RemoveAsync(string gid)
    {
        if (!IsRunning) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var result = await CallAsync("aria2.tellStatus", [gid, new[] { "status" }], timeout.Token).ConfigureAwait(false);
            var status = result.GetProperty("status").GetString();
            if (status is not ("complete" or "error" or "removed"))
            {
                await CallAsync("aria2.forceRemove", [gid], timeout.Token).ConfigureAwait(false);
                do
                {
                    await Task.Delay(50, timeout.Token).ConfigureAwait(false);
                    result = await CallAsync("aria2.tellStatus", [gid, new[] { "status" }], timeout.Token).ConfigureAwait(false);
                    status = result.GetProperty("status").GetString();
                } while (status is not ("complete" or "error" or "removed"));
            }
            await CallAsync("aria2.removeDownloadResult", [gid], timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            // Kill and wait before callers are allowed to remove or reuse the output file.
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
            _disposed = true;
            _process.Dispose();
            _client.Dispose();
        }
    }
}
