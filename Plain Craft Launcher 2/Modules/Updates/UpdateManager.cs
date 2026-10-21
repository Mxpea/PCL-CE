using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;

namespace PCL;

public static class UpdateManager
{
    public static UpdateEnums.VersionStatus CachedVersionStatus { get; set; } = UpdateEnums.VersionStatus.Unknown;

    public static UpdatesWrapperModel remoteServer = new(new List<IUpdateSource>
    {
        new UpdatesMirrorChyanModel(),
        new UpdatesRandomModel(new[]
        {
            new UpdatesMinioModel("https://s3.pysio.online/pcl2-ce/", "Pysio"),
            new UpdatesMinioModel("https://staticassets.naids.com/resources/pclce/", "Naids")
        }),
        new UpdatesMinioModel("https://github.com/PCL-Community/PCL2_CE_Server/raw/main/", "GitHub")
    });

    public static bool IsCurrentVersionBeta
    {
        get
        {
            if (ModBase.versionBaseName.Contains("beta"))
                return true;
            return (int)Config.Update.UpdateChannel == 1;
        }
    }
    
    public static UpdateEnums.VersionStatus GetVersionStatus()
    {
        try
        {
            if (IsCurrentVersionBeta && (int)Config.Update.UpdateChannel != 1)
            {
                var isNewerThanStable = remoteServer.IsLatest(UpdateChannel.stable,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, SemVer.Parse(ModBase.versionBaseName),
                    ModBase.versionCode);
                var isBetaLatest = remoteServer.IsLatest(UpdateChannel.beta,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, SemVer.Parse(ModBase.versionBaseName),
                    ModBase.versionCode);
                return CachedVersionStatus = isNewerThanStable && isBetaLatest
                    ? UpdateEnums.VersionStatus.Latest
                    : UpdateEnums.VersionStatus.NotLatest;
            }

            return CachedVersionStatus = remoteServer.IsLatest(
                IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, SemVer.Parse(ModBase.versionBaseName),
                ModBase.versionCode)
                ? UpdateEnums.VersionStatus.Latest
                : UpdateEnums.VersionStatus.NotLatest;
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Update.Check.Failed"),
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Update.Check.Failed"));
            return CachedVersionStatus = UpdateEnums.VersionStatus.Unknown;
        }
    }
    
    public static ModLoader.LoaderCombo<JsonObject> updateLoader;

    /// <summary>仅由更新页的安装按钮发起下载、校验及安装。</summary>
    public static void UpdateStart()
    {
        var dlTargetPath = ModBase.exePath + @"PCL\Plain Craft Launcher Community Edition.exe";
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var version = remoteServer.GetLatestVersion(
                    IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64);
                ModBase.Log($"[Update] 手动更新：远程最新版本 {version.VersionName}，当前版本 {ModBase.versionBaseName}");
                if (!(SemVer.Parse(version.VersionName) > SemVer.Parse(ModBase.versionBaseName)))
                    return;

                ModBase.WriteFile($"{ModBase.pathTemp}CEUpdateLog.md", version.Changelog);
                var loaders = new List<ModLoader.LoaderBase>();
                loaders.AddRange(remoteServer.GetDownloadLoader(
                    IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, dlTargetPath));
                loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Update.Task.Check"), _ =>
                {
                    var curHash = ModBase.GetFileSHA256(dlTargetPath);
                    if ((curHash ?? "") != (version.Sha256 ?? ""))
                        throw new Exception(Lang.Text("Update.Error.Sha256Mismatch", version.Sha256, curHash));
                }));
                loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Update.Task.Install"), _ => UpdateRestart()));

                updateLoader = new ModLoader.LoaderCombo<JsonObject>(Lang.Text("Update.Title"), loaders);
                updateLoader.Start();
                ModLoader.LoaderTaskbarAdd(updateLoader);
                ModMain.frmMain.BtnExtraDownload.ShowRefresh();
                ModMain.frmMain.BtnExtraDownload.Ribble();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Update] 获取启动器更新失败");
                HintService.Hint(Lang.Text("Update.Error.FetchFailed"), HintType.Error);
            }
        });
    }

    private static void UpdateRestart()
    {
        try
        {
            var fileName = ModBase.exePath + @"PCL\Plain Craft Launcher Community Edition.exe";
            if (!File.Exists(fileName))
            {
                ModBase.Log("[System] 更新失败：未找到更新文件");
                return;
            }

            // id old new restart
            var text =
                $"update {Process.GetCurrentProcess().Id} \"{Basics.ExecutablePath}\" \"{fileName}\" true";
            ModBase.Log("[System] 更新程序启动，参数：" + text);
            Process.Start(new ProcessStartInfo(fileName)
                { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, Arguments = text });
            ModMain.frmMain.EndProgram(false);
            ModBase.Log("[System] 已由于手动更新结束程序");
        }
        catch (Win32Exception ex)
        {
            ModBase.Log(ex, "手动安装更新时触发 Win32 错误，疑似被拦截");
            ModMain.MyMsgBox(
                Lang.Text("Update.Error.UpdateBlockedMessage", ModBase.exePath),
                Lang.Text("Update.Error.UpdateBlocked"),
                Lang.Text("Common.Action.Confirm"),
                "",
                "",
                true);
        }
    }

    /// <summary>
    ///     确保 PathTemp 下的 Latest.exe 是最新正式版的 PCL，它会被用于整合包打包。
    ///     如果不是，则下载一个。
    /// </summary>
    internal static void DownloadLatestPCL(ModLoader.LoaderBase loaderToSyncProgress = null)
    {
        // 注意：如果要自行实现这个功能，请换用另一个文件路径，以免与官方版本冲突
        var latestPCLPath = Path.Combine(ModBase.pathTemp, "CE-Latest.exe");
        var target = remoteServer.GetLatestVersion(UpdateChannel.stable,
            SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64);
        if (target is null)
            throw new Exception(Lang.Text("Update.Error.UnableToGetUpdate"));
        if (File.Exists(latestPCLPath) && (ModBase.GetFileSHA256(latestPCLPath) ?? "") == (target.Sha256 ?? ""))
        {
            ModBase.Log("[System] 最新版 PCL 已存在，跳过下载");
            return;
        }

        if ((ModBase.GetFileSHA256(Basics.ExecutablePath) ?? "") == (target.Sha256 ?? "")) // 正在使用的版本符合要求，直接拿来用
        {
            ModBase.CopyFile(Basics.ExecutablePath, latestPCLPath);
            return;
        }

        var loaders = remoteServer.GetDownloadLoader(UpdateChannel.stable,
            SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, latestPCLPath);
        var loader = new ModLoader.LoaderCombo<int>(Lang.Text("Update.Task.DownloadLatestStable"), loaders);
        loader.Start();
        loader.WaitForExit();
    }

    /// <summary>
    ///     展示社区版提示
    /// </summary>
    /// <param name="IsUpdate">是否为更新时启动</param>
    public static void ShowCEAnnounce()
    {
        ModMain.MyMsgBox(Lang.Text("Update.CommunityNotice.Body"),
            Lang.Text("Update.CommunityNotice.Title"),
            Lang.Text("Update.CommunityNotice.Confirm"));
    }
}
