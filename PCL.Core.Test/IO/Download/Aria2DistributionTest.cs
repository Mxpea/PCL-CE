using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO.Download.Aria2;

namespace PCL.Core.Test.IO.Download;

[TestClass]
public class Aria2DistributionTest
{
    private static Stream OpenArchive() => typeof(Aria2DistributionTest).Assembly
        .GetManifestResourceStream("PCL.Aria2.Distribution.zip")
        ?? throw new FileNotFoundException("缺少内置 aria2 官方发行包。");

    [TestMethod]
    public async Task InstallsOfflinePreservesLicensesAndRepairsCorruptExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pcl-aria2-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.IsFalse(await Aria2Distribution.HasExpectedExecutableAsync(directory));
            await using (var archive = OpenArchive())
                await Aria2Distribution.InstallAsync(archive, directory);
            Assert.IsTrue(await Aria2Distribution.HasExpectedExecutableAsync(directory));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "COPYING")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "LICENSE.OpenSSL")));
            await File.WriteAllTextAsync(Path.Combine(directory, "aria2c.exe"), "corrupt");
            Assert.IsFalse(await Aria2Distribution.HasExpectedExecutableAsync(directory));
            await using (var archive = OpenArchive())
                await Aria2Distribution.InstallAsync(archive, directory);
            using var session = await Aria2Session.StartAsync(Path.Combine(directory, "aria2c.exe"));
            Assert.AreEqual("1.37.0", (await session.CallAsync("aria2.getVersion", [])).GetProperty("version").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task RejectsCorruptArchiveBeforeChangingExistingInstallation()
    {
        var parent = Path.Combine(Path.GetTempPath(), "pcl-aria2-reject-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(parent, "installed");
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "aria2c.exe");
            await File.WriteAllTextAsync(executable, "existing");
            using var archive = new MemoryStream([1, 2, 3]);
            await Assert.ThrowsAsync<IOException>(() => Aria2Distribution.InstallAsync(archive, directory));
            Assert.AreEqual("existing", await File.ReadAllTextAsync(executable));
            Assert.HasCount(1, Directory.GetDirectories(parent));
        }
        finally { Directory.Delete(parent, true); }
    }
}
