# 微软正版登录配置

微软登录需要启动器自己的 OAuth 应用 Client ID。官方 CI 从仓库的
`CLIENT_ID` Secret 注入；自行编译不会自动获得官方配置。
缺少配置时，登录会在发出网络请求前失败，与下载后端无关。

## 本地测试

使用你管理的、已正确配置微软个人账户设备代码流程的应用 Client ID。
完成 Minecraft 登录还取决于该应用的 Minecraft 服务 API 访问权限。
Client ID 是公开的应用标识，不是账号密码，也不需要填写 Client Secret。

对于未嵌入 Client ID 的 Debug 或 Release 构建，在 PowerShell 中执行：

```powershell
$env:PCL_MS_CLIENT_ID = '<你的应用 Client ID>'
& '.\artifacts\PCL-CEedit-x64\PCL-CEedit.exe'
```

变量只影响当前 PowerShell 及其启动的进程。设置后必须重新启动程序。
已有嵌入配置时优先使用嵌入值，环境变量不会替换它。

## 分发构建

要让测试者无需设置环境变量，在编译时嵌入 Client ID：

```powershell
$env:PCL_WRITE_SECRET = '1'
$env:PCL_MS_CLIENT_ID = '<你的应用 Client ID>'
dotnet clean 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj' -c Release -p:Platform=x64
dotnet publish 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj' `
  -c Release -p:Platform=x64 -p:UseSharedCompilation=false `
  --no-self-contained -o artifacts/PCL-CEedit-x64
```

源生成器在编译进程中读取环境变量。更改变量后须重新编译核心库；
禁用共享编译可避免旧编译服务保留先前的环境。
`PCL_WRITE_SECRET` 会嵌入所有非空的 `PCL_*` 环境变量，
请仅在干净的构建环境中设置需要发布的配置。

构建成功只证明配置可以读取。发布前还需用实际应用验证获取设备代码、
浏览器授权、Minecraft 资料获取及重启后的令牌刷新。

## 设备代码请求被拒绝

若微软返回 `invalid_client` / `AADSTS70002`，提示应用必须标记为
`mobile`，请在 Microsoft Entra 管理中心打开对应的“应用注册”，
进入“身份验证”，将“允许公共客户端流”（Allow public client flows）
设为“是”并保存。该设置可能位于“高级设置”中。
同时确认应用支持个人 Microsoft 账户；启动器使用 `consumers` 端点。

这些是微软端的应用设置，调整后使用相同 Client ID 的构建重新登录即可。
获取设备代码成功后，仍需验证 Minecraft 服务访问权限。

## Minecraft 令牌交换返回 403

若 OAuth、Xbox Live 和 XSTS 均成功，而
`api.minecraftservices.com/authentication/login_with_xbox` 返回 403，
先确认这个 Client ID 是否已获准访问 Minecraft Java 服务 API。
启用公共客户端流仅解决设备代码授权，不代表 Minecraft API 访问已获批准。
应用注册中的普通 API 权限设置也不能代替 Minecraft 服务的应用审核。
审核申请入口：<https://aka.ms/mce-reviewappid>。

若应用已获批准，则进一步排查网络代理、出口或网关拦截。
此阶段尚未检查购买许可，不能直接将 403 解释为账号未购买 Minecraft。
