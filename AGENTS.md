# Repository Guidelines

## 项目结构与模块组织

- `Plain Craft Launcher 2/`：WPF 启动器；`Pages/` 放页面，`Controls/` 放控件，`Modules/` 放功能模块，`Images/` 与 `Resources/` 放资源。
- `PCL.Core/`：核心库，按 `Minecraft/`、`App/`、`IO/`、`Link/` 等领域组织；本地化字典位于 `App/Localization/Languages/`。
- `PCL.Core.SourceGenerators/`：Roslyn 源代码生成器。
- `PCL.Core.Test/`：核心库测试，子目录对应被测模块；`.github/workflows/` 保存构建与发布流程。

## 构建、测试与本地开发

安装 .NET 10 SDK，使用 C# 14。应用与测试依赖 Windows/WPF；Linux、macOS 可交叉编译，运行和界面验证请在 Windows 完成。在仓库根目录执行：

```powershell
# 还原依赖并构建解决方案
dotnet restore "Plain Craft Launcher 2.slnx"
dotnet build "Plain Craft Launcher 2.slnx" -c Debug -p:Platform=x64
# 启动应用
dotnet run --project "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Debug -p:Platform=x64
# 单独运行测试
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Debug -p:Platform=x64
```

与 CI 一致的发布构建使用：

```powershell
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c CI -p:Platform=x64 --no-self-contained -o ./artifact
```

CI 同时构建 x64 与 ARM64；涉及平台差异时验证对应架构。

## 编码风格与命名

沿用相邻代码的缩进与布局；C# 通常使用四空格缩进。遵循根目录及 `PCL.Core/.editorconfig`，使用编辑器的 EditorConfig 支持检查风格，避免无关的整文件格式化。

核心库中，类型和公开成员使用 `PascalCase`，接口加 `I` 前缀，局部变量与参数使用 `camelCase`；私有字段使用 `_camelCase`，私有静态只读字段、私有实例属性和私有方法使用 `_PascalCase`。新文件按职责命名，页面保持 `.xaml` 与 `.xaml.cs` 配对。

## 测试规范

使用 MSTest：测试类命名为 `*Test`，标记 `[TestClass]`；测试方法标记 `[TestMethod]`，参数化场景使用 `[DataRow]`。新增逻辑与缺陷修复应覆盖正常、边界和失败行为；界面变更需手动验证并记录步骤。

解决方案默认不构建测试项目，当前 CI 未运行测试，因此提交前须单独执行上述测试命令。仓库未设置覆盖率门槛；涉及网络或本机资源的测试需注明环境条件。

## 提交与 Pull Request

遵循现有 Angular 风格：`<type>(scope): <subject>`，例如 `fix(tooltip): 修复提示框偏移`。常见类型包括 `feat`、`fix`、`refactor`、`ci`、`docs` 和 `test`；主题简洁，不加句号。

PR 目标为 `dev`，说明问题、变更行为及验证结果，并关联 Issue（如 `Closes #123`）；界面变更附截图。提交前确保编译通过，避免混入构建产物或无关修改。更多流程见 `CONTRIBUTING.md` 及其开发指南、技术规范链接。

## 配置与密钥

构建流程通过 `PCL_MS_CLIENT_ID`、`PCL_CURSEFORGE_API_KEY` 等环境变量注入配置；按需在本地配置，勿将真实密钥、令牌或含敏感信息的日志提交到仓库。
