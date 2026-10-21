using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO.Download.Aria2;

namespace PCL.Core.Test.IO.Download;

/// <summary>使用内置官方发行包和本地 HTTP 服务，无需访问外网。</summary>
[TestClass]
public class Aria2SessionTest
{
    private static string _directory = null!;
    private static string _executable = null!;

    [ClassInitialize]
    public static async Task InstallAsync(TestContext context)
    {
        _directory = CreateDirectory();
        await using var archive = typeof(Aria2SessionTest).Assembly.GetManifestResourceStream("PCL.Aria2.Distribution.zip")
            ?? throw new FileNotFoundException("缺少内置 aria2 官方发行包。");
        _executable = await Aria2Distribution.InstallAsync(archive, _directory);
    }

    [ClassCleanup]
    public static void Cleanup() => Directory.Delete(_directory, true);

    private static string GetExecutable() => _executable;

    [TestMethod]
    public async Task DownloadsExactBytesAndForwardsHeaders()
    {
        using var server = new LocalServer();
        using var session = await Aria2Session.StartAsync(GetExecutable());
        var directory = CreateDirectory();
        try
        {
            var gid = await AddAsync(session, server.Url, directory, "文件 with spaces.bin",
                new() { ["header"] = new[] { "X-PCL-Test: forwarded" }, ["user-agent"] = "PCL-Aria2-Test" });
            var result = await WaitForTerminalAsync(session, gid);
            Assert.AreEqual("complete", result.GetProperty("status").GetString());
            CollectionAssert.AreEqual(server.Bytes, await File.ReadAllBytesAsync(Path.Combine(directory, "文件 with spaces.bin")));
            Assert.AreEqual("forwarded", server.LastHeader);
            Assert.AreEqual("PCL-Aria2-Test", server.LastUserAgent);
            await session.RemoveAsync(gid);
            Assert.IsTrue(session.IsRunning);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task RemovingOneTaskStopsWritesAndLeavesOtherTasksUsable()
    {
        using var server = new LocalServer();
        using var session = await Aria2Session.StartAsync(GetExecutable());
        var directory = CreateDirectory();
        try
        {
            var gid = await AddAsync(session, server.Url, directory, "cancel.bin", new() { ["max-download-limit"] = "16K" });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var result = await session.CallAsync("aria2.tellStatus", [gid], timeout.Token);
                if (long.Parse(result.GetProperty("completedLength").GetString()!) > 0) break;
                await Task.Delay(50, timeout.Token);
            }
            await session.RemoveAsync(gid);
            var path = Path.Combine(directory, "cancel.bin");
            // Exclusive access and deletion prove aria2 released its writer before cleanup returns.
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(path);
            var next = await AddAsync(session, server.Url, directory, "next.bin");
            Assert.AreEqual("complete", (await WaitForTerminalAsync(session, next)).GetProperty("status").GetString());
            await session.RemoveAsync(next);
        }
        finally { session.Dispose(); Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task HttpFailureIsReportedAndTaskCanBeCleanedUp()
    {
        using var server = new LocalServer();
        using var session = await Aria2Session.StartAsync(GetExecutable());
        var directory = CreateDirectory();
        try
        {
            var gid = await AddAsync(session, server.Url + "missing", directory, "missing.bin");
            var result = await WaitForTerminalAsync(session, gid);
            Assert.AreEqual("error", result.GetProperty("status").GetString());
            Assert.AreNotEqual("0", result.GetProperty("errorCode").GetString());
            await session.RemoveAsync(gid);
            Assert.IsTrue(session.IsRunning);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DisposingSessionStopsActiveDownloadsAndIsIdempotent()
    {
        using var server = new LocalServer();
        using var session = await Aria2Session.StartAsync(GetExecutable());
        var directory = CreateDirectory();
        try
        {
            await AddAsync(session, server.Url, directory, "stop.bin", new() { ["max-download-limit"] = "16K" });
            session.Dispose();
            session.Dispose();
            Assert.IsFalse(session.IsRunning);
            await Assert.ThrowsAsync<IOException>(() => session.CallAsync("aria2.getVersion", []));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pcl-aria2-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> AddAsync(Aria2Session session, string url, string directory, string name,
        Dictionary<string, object>? extra = null)
    {
        var options = extra ?? new Dictionary<string, object>();
        options["dir"] = directory;
        options["out"] = name;
        options["split"] = "1";
        options["all-proxy"] = "";
        return (await session.CallAsync("aria2.addUri", [new[] { url }, options])).GetString()!;
    }

    private static async Task<JsonElement> WaitForTerminalAsync(Aria2Session session, string gid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var result = await session.CallAsync("aria2.tellStatus", [gid], timeout.Token);
            if (result.GetProperty("status").GetString() is "complete" or "error" or "removed") return result;
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class LocalServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serve;
        public byte[] Bytes { get; } = new byte[512 * 1024];
        public string Url { get; }
        public string? LastHeader { get; private set; }
        public string? LastUserAgent { get; private set; }

        public LocalServer()
        {
            Random.Shared.NextBytes(Bytes);
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}/";
            socket.Stop();
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _serve = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    try
                    {
                        LastHeader = context.Request.Headers["X-PCL-Test"];
                        LastUserAgent = context.Request.UserAgent;
                        if (context.Request.Url!.AbsolutePath == "/missing")
                            context.Response.StatusCode = 404;
                        else
                        {
                            context.Response.ContentLength64 = Bytes.Length;
                            await context.Response.OutputStream.WriteAsync(Bytes);
                        }
                    }
                    catch (Exception ex) when (ex is HttpListenerException or IOException) { }
                    finally { context.Response.Close(); }
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException) { }
        }

        public void Dispose()
        {
            _listener.Close();
            _serve.GetAwaiter().GetResult();
        }
    }
}
