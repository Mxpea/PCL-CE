# aria2 下载后端

默认使用 **aria2（自动管理）**，可在“设置 → 管理 → 游戏资源获取行为”的“下载后端”中切换。已有的后端选择仍会保留，修改对新开始的文件下载生效；版本列表与 API 请求继续使用内置网络层。

## 安装与进程管理

- 首次使用从启动器内嵌的 [aria2 官方 1.37.0 发行包](https://github.com/aria2/aria2/releases/tag/release-1.37.0) 安装 Windows x64 引擎，无需连接 GitHub。安装或启动失败时，本次启动期间回退到内置下载器，并记录原因。
- 安装位置为 `%APPDATA%\PCL\Tools\aria2\1.37.0-win-x64\`。安装包和可执行文件均校验固定 SHA-256；安装包哈希可与 [winget 清单](https://github.com/microsoft/winget-pkgs/blob/master/manifests/a/aria2/aria2/1.37.0/aria2.aria2.installer.yaml) 对照。离线部署可把完整的官方发行包解压内容放入该目录。
- 每个启动器进程管理自己的 aria2 子进程，使用随机 RPC 端口与随机令牌，仅监听本机。不会连接或终止用户自行运行的 aria2。启动器退出后 aria2 随之退出。
- Windows ARM64 使用该 x64 程序的系统仿真；无法运行时回退到内置下载器。当前未提供原生 ARM64 aria2 二进制。

## 下载行为与边界

- 保持现有的候选源顺序和外层重试逻辑，每次只交给 aria2 一个地址，避免把内容不同的备用源混合分片。
- UA、业务请求头及 HTTP 代理凭据通过 RPC 传递。RPC 请求本身不使用代理。不能映射的代理类型使用内置下载器。
- 两种后端共享连接配额；aria2 为每个文件预留分片连接数，因此界面的总连接数表示占用配额，可能高于实际连接数。混合运行时限速预算按预留连接数分配，aria2 每 250 毫秒同步设置，切换时可能有短暂速率波动。
- 下载到 `.PCLDownloading` 临时文件，停止写入后沿用现有文件校验与提交逻辑。取消与失败会清理临时文件及 `.aria2` 控制文件；本版不提供跨重启续传。
- aria2 使用其自身的 HTTP/1.1、DNS 和 TLS 实现；启动器的 HTTP 协议偏好、DoH 配置仅作用于内置下载器。
- 传输失败继续按现有规则换源重试；只有初始化不可用或代理不支持时直接使用内置下载器。

## 本地验证

安装 .NET 10 SDK 后，在 Windows 上执行：

```powershell
dotnet build "Plain Craft Launcher 2.slnx" -c Debug -p:Platform=x64
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~Aria2
```

测试从内嵌发行包离线安装，覆盖损坏安装包拒绝、许可证保留和损坏引擎修复；集成测试仅访问本地 HTTP 服务，覆盖文件内容、中文及空格路径、请求头、取消后释放文件、HTTP 失败与进程退出。界面需手动验证后端切换、下载进度、限速、源切换与错误校验。

## 第三方发行信息

aria2 是独立进程。仓库在 `Plain Craft Launcher 2/Dependencies/aria2/` 保存未经修改的官方 Windows x64 发行包，作为资源嵌入单文件发布产物；安装时保留全部 `COPYING`、`LICENSE.OpenSSL`、说明及作者文件。来源、SHA-256 与源码位置见该目录的 `NOTICE.md`。aria2 使用 GPL-2.0-or-later。
