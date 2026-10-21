using System.IO;
using System.Net.Http;
using PCL.Core.IO.Download.Aria2;
using PCL.Core.IO.Net.Http;

namespace PCL.Network;

/// <summary>将现有文件任务映射到启动器管理的 aria2 会话。</summary>
internal static class Aria2Downloader
{
    private static readonly SemaphoreSlim StartupLock = new(1, 1);
    private static Aria2Session? _session;
    private static bool _unavailable;

    static Aria2Downloader()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _session?.Dispose();
    }

    public static async Task<bool> TryDownloadAsync(string url, string localPath, bool useBrowserUserAgent,
        string customUserAgent, bool enableParallelChunks, DownloadFile? trackedFile, CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        if (uri.Scheme is not ("http" or "https")) return false;
        var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session is null) return false;

        var expectedSize = trackedFile?.Check?.actualSize ?? -1;
        var connections = enableParallelChunks && (expectedSize < 0 || expectedSize >= AdaptiveRangeDownloader.SmallFileThreshold)
            ? Math.Clamp(ModNet.NetTaskSingleFileConnectionLimit, 1,
                Math.Max(1, Math.Min(ModNet.NetTaskConnectionLimit, ModNet.NetTaskSingleFileConnectionLimitMax)))
            : 1;
        // Reserve the whole connection budget atomically: acquiring slots one by one could deadlock concurrent files.
        using var lease = await DownloadResourceManager.AcquireConnectionAsync(url, cancellationToken, connections, external: true)
            .ConfigureAwait(false);
        using var request = FileDownloader.CreateDownloadRequest(url, useBrowserUserAgent, customUserAgent,
            DownloadRequestKind.LargeOrUnknown);
        var options = new Dictionary<string, object>
        {
            ["dir"] = Path.GetDirectoryName(Path.GetFullPath(localPath))!,
            ["out"] = Path.GetFileName(localPath) + ModNet.NetDownloadEnd,
            ["split"] = connections.ToString(),
            ["max-connection-per-server"] = connections.ToString(),
            ["min-split-size"] = "4M",
            ["user-agent"] = request.Headers.UserAgent.ToString(),
            ["header"] = request.Headers.Where(header => !header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                .Select(header => $"{header.Key}: {string.Join(", ", header.Value)}").ToArray(),
            ["all-proxy"] = "",
            ["http-proxy"] = "",
            ["https-proxy"] = ""
        };
        var proxyManager = HttpProxyManager.Instance;
        var proxy = proxyManager.GetProxy(uri);
        if (!proxyManager.IsBypassed(uri) && proxy is not null && proxy != uri)
        {
            // aria2's HTTP backend supports HTTP proxies. Preserve other proxy semantics via the native downloader.
            if (proxy.Scheme != "http") return false;
            options["all-proxy"] = proxy.GetLeftPart(UriPartial.Authority);
            options["http-proxy"] = options["all-proxy"];
            options["https-proxy"] = options["all-proxy"];
            var credential = proxyManager.Credentials?.GetCredential(proxy, "Basic");
            if (credential is not null)
            {
                options["all-proxy-user"] = credential.UserName;
                options["all-proxy-passwd"] = credential.Password;
                options["http-proxy-user"] = credential.UserName;
                options["http-proxy-passwd"] = credential.Password;
                options["https-proxy-user"] = credential.UserName;
                options["https-proxy-passwd"] = credential.Password;
            }
        }

        string? gid = null;
        long recordedBytes = 0;
        long lastLimit = long.MinValue;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastLimit = Math.Max(0, DownloadResourceManager.Aria2SpeedLimit);
            await session.CallAsync("aria2.changeGlobalOption",
                [new Dictionary<string, string> { ["max-overall-download-limit"] = lastLimit.ToString() }], cancellationToken)
                .ConfigureAwait(false);
            // Do not cancel addUri in flight: otherwise aria2 could accept a task whose GID we never receive.
            var result = await session.CallAsync("aria2.addUri", [new[] { url }, options]).ConfigureAwait(false);
            gid = result.GetString() ?? throw new IOException("aria2 未返回任务标识。");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var limit = Math.Max(0, DownloadResourceManager.Aria2SpeedLimit);
                if (limit != lastLimit)
                {
                    await session.CallAsync("aria2.changeGlobalOption",
                        [new Dictionary<string, string> { ["max-overall-download-limit"] = limit.ToString() }], cancellationToken)
                        .ConfigureAwait(false);
                    lastLimit = limit;
                }
                var status = await session.CallAsync("aria2.tellStatus", [gid, new[]
                {
                    "status", "totalLength", "completedLength", "downloadSpeed", "connections", "errorCode"
                }], cancellationToken).ConfigureAwait(false);
                var total = ReadNumber(status, "totalLength");
                var completed = ReadNumber(status, "completedLength");
                DownloadResourceManager.RecordDownloadedBytes(Math.Max(0, completed - recordedBytes));
                recordedBytes = completed;
                if (trackedFile is not null)
                {
                    trackedFile.State = NetState.Downloading;
                    trackedFile.TotalSize = total > 0 ? total : expectedSize;
                    trackedFile.IsUnknownSize = trackedFile.TotalSize < 0;
                    trackedFile.DownloadedBytes = completed;
                    trackedFile.Speed = ReadNumber(status, "downloadSpeed");
                    trackedFile.ActiveThreads = (int)ReadNumber(status, "connections");
                }
                switch (status.GetProperty("status").GetString())
                {
                    case "complete":
                        if (total != completed || new FileInfo(localPath + ModNet.NetDownloadEnd).Length != completed)
                            throw new IOException("aria2 下载长度与输出文件不一致。");
                        return true;
                    case "error":
                        throw new IOException($"aria2 下载失败，错误码：{status.GetProperty("errorCode").GetString()}。");
                    case "removed":
                        throw new IOException("aria2 下载任务已被移除。");
                }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's RPC timeout is a transport failure, not a user cancellation: allow normal source retries.
            throw new TimeoutException("等待 aria2 RPC 响应超时。", ex);
        }
        finally
        {
            if (gid is not null)
                await session.RemoveAsync(gid).ConfigureAwait(false);
            else
                // An addUri response may have been lost. Stop this session before falling back or deleting output.
                session.Dispose();
            if (trackedFile is not null)
            {
                trackedFile.ActiveThreads = 0;
                trackedFile.Speed = 0;
            }
        }
    }

    private static long ReadNumber(JsonElement element, string property) =>
        long.Parse(element.GetProperty(property).GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<Aria2Session?> GetSessionAsync(CancellationToken cancellationToken)
    {
        await StartupLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_unavailable) return null;
            if (_session?.IsRunning == true) return _session;
            _session?.Dispose();
            try
            {
                var executable = await EnsureExecutableAsync(cancellationToken).ConfigureAwait(false);
                _session = await Aria2Session.StartAsync(executable, cancellationToken).ConfigureAwait(false);
                ModBase.Log("[Download] aria2 下载后端已启动。");
                return _session;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _unavailable = true;
                ModBase.Log(ex, "[Download] aria2 不可用，本次启动期间使用内置下载器。", ModBase.LogLevel.Hint);
                return null;
            }
        }
        finally { StartupLock.Release(); }
    }

    private static async Task<string> EnsureExecutableAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(ModBase.pathAppdata, "Tools", "aria2", "1.37.0-win-x64");
        if (await Aria2Distribution.HasExpectedExecutableAsync(directory, cancellationToken).ConfigureAwait(false))
            return Path.Combine(directory, "aria2c.exe");

        ModBase.Log("[Download] 正在从内置官方发行包安装 aria2 1.37.0。");
        await using var archive = typeof(Aria2Downloader).Assembly.GetManifestResourceStream("PCL.Aria2.Distribution.zip")
            ?? throw new IOException("缺少内置 aria2 官方发行包，请重新获取完整启动器。");
        return await Aria2Distribution.InstallAsync(archive, directory, cancellationToken).ConfigureAwait(false);
    }
}
