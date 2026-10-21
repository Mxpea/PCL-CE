using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.IO.Download.Aria2;

/// <summary>从随程序提供的官方发行包安装 aria2，不依赖首次运行时的外网连接。</summary>
public static class Aria2Distribution
{
    public const string ArchiveHash = "67D015301EEF0B612191212D564C5BB0A14B5B9C4796B76454276A4D28D9B288";
    public const string ExecutableHash = "BE2099C214F63A3CB4954B09A0BECD6E2E34660B886D4C898D260FEBFE9D70C2";

    public static async Task<bool> HasExpectedExecutableAsync(string directory, CancellationToken cancellationToken = default)
        => await _HasExpectedHashAsync(Path.Combine(directory, "aria2c.exe"), ExecutableHash, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>校验完整发行包与可执行文件，保留发行包内所有许可证和说明文件。</summary>
    public static async Task<string> InstallAsync(Stream archive, string directory, CancellationToken cancellationToken = default)
    {
        var staging = Path.GetFullPath(directory) + "." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            var archivePath = Path.Combine(staging, "aria2.zip");
            await using (var output = File.Create(archivePath))
                await archive.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            if (!await _HasExpectedHashAsync(archivePath, ArchiveHash, cancellationToken).ConfigureAwait(false))
                throw new IOException("aria2 安装包 SHA-256 校验失败。");
            ZipFile.ExtractToDirectory(archivePath, staging);
            var extracted = Path.Combine(staging, "aria2-1.37.0-win-64bit-build1");
            if (!await HasExpectedExecutableAsync(extracted, cancellationToken).ConfigureAwait(false))
                throw new IOException("aria2 可执行文件 SHA-256 校验失败。");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(extracted))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
            return Path.Combine(directory, "aria2c.exe");
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    private static async Task<bool> _HasExpectedHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) == expected;
    }
}
