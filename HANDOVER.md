# Composa 中文版 · 交接与续作说明

给「换一台电脑继续干活」用的。**工程本身的规则在上游的 [AGENTS.md](AGENTS.md) 里，写得非常详尽
（架构、渲染/位图所有权规则、PSD/XCF/MCP 的约束、命令清单），请以它为准。** 这份文档只写
AGENTS.md 里没有的三类东西：**本分支（中文试用版）特有的约定**、**换机器的准备工作**、
**已经踩过的坑**。

---

## 0. 一分钟速览

| | |
|---|---|
| 仓库 | https://github.com/shhzhang/Composa-zhcn （**fork** 自 `dvdstelt/Composa`）|
| 当前 `main` | `711ea16` |
| 版本标签 | `1.4.0-CN` → `711ea16` |
| 已发布版本 | Release **1.4.0-CN**，15 个文件（Windows 6 / Linux 8 / `sha256sums.txt`）|
| 技术栈 | C# / .NET 10 + Avalonia 12.1.2 + SkiaSharp 3.119.4；**`Composa.Core` 不得引用 Avalonia**；UI 全部 C# 手写，**没有 XAML** |
| 测试基线 | `Composa.Core.Tests` **386** + `Composa.App.Tests` **277** = 663，全绿 |
| 一句话现状 | 界面全中文 · 发布者 SHHZHANG · 版本 `-CN` · **更新检查已关闭**（信源指向 `http://localhost`）|

---

## 1. 换电脑：从零到能跑

### 1.1 工具链

```bash
# 必需
.NET SDK 10            # global.json 锁 10.0.100，rollForward: latestFeature；本机 10.0.402

# 打 MSI 必需（Windows）
dotnet tool install --global wix --version 7.0.0
wix eula accept wix7                                    # ← 不先接受会卡在无人能应答的提示上
wix extension add -g WixToolset.UI.wixext/7.0.0

# 打 Inno 的 setup.exe（可选）
#   注意：scripts/package/windows.sh 只找
#     /c/Program Files (x86)/Inno Setup 6/ISCC.exe
#     /c/Program Files/Inno Setup 6/ISCC.exe
#   以及 PATH 上的 iscc。装到 %LOCALAPPDATA%\Programs\... 的话脚本找不到，
#   本机就是这种情况 → 要么把 ISCC 加进 PATH，要么打包时传 --no-installer。

# 打 Linux 包（在 Linux 上）
sudo apt-get install -y rpm desktop-file-utils

# 模型（必须，且会联网）
scripts/models/fetch.sh     # 只下 modnet.onnx；u2netp / realesr 在 git 里
```

> `huggingface.co` 在国内不通，`fetch.sh` 已改走 `hf-mirror.com`，SHA-256 与脚本里的 pin 一致。
> 没跑 fetch 的检出**仍能编译和测试**，只是少「主体：人物」这个选项。

### 1.2 Git 与网络（这是最容易卡住的地方）

- **网络时通时不通，先测一下再决定要不要代理**（这一步能省半小时）：

  ```bash
  curl -s -o /dev/null -w "%{http_code}\n" --max-time 8 https://github.com
  ```

  - **返回 `200`** → 直连可用（VPN 切到了 TUN / 全局模式）。**不需要代理，也不该留着代理配置。**
  - **超时或 `000`** → 需要本地代理（VPN 的规则模式 / 系统代理模式）。代理端口从注册表读：
    `HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings` 的 `ProxyServer`
    （本机曾长期是 `http://127.0.0.1:7892`，端口换机器很可能不同）。

- 需要代理时，**只对 github.com 生效**地配置，不污染别的远端（`origin` 那个 gh-proxy 地址不受影响）：

  ```bash
  git config http.https://github.com.proxy http://127.0.0.1:7892
  # 直连可用之后要撤掉它，否则一个已经关掉的代理会让推送直接报
  #   Failed to connect to github.com:443 over proxy 127.0.0.1
  git config --unset http.https://github.com.proxy
  ```

  这是 **repo 级**配置，换机器时按上面的直连测试重新判断一次。
- 两个远端，别弄混：

  | 远端 | 地址 | 用途 |
  |---|---|---|
  | `origin` | `https://v6.gh-proxy.org/github.com/dvdstelt/Composa.git` | 上游，**只读，永远不要 push** |
  | `zhcn` | `https://github.com/shhzhang/Composa-zhcn.git` | **自己的仓库，push 到这里** |

- **推送要用带 Git Credential Manager 的 Git。** 本机可用的在
  `C:\Users\sayer\scoop\apps\git\2.56.0.2\bin\git.exe`（WorkBuddy 自带的 PortableGit 提交/合并没问题，
  但推送要显式 `-c credential.helper=manager`）。首次会弹浏览器授权 GitHub，之后凭据缓存住，
  **换电脑要重新授权一次**。

  ```bash
  export HTTPS_PROXY=http://127.0.0.1:7892
  git -c credential.helper=manager push zhcn main
  ```

- 提交身份（仓库级已设，换机器要重设）：

  ```bash
  git config user.name  shhzhang
  git config user.email "132824951+shhzhang@users.noreply.github.com"
  ```

  `132824951` 是你的 GitHub 用户 ID。这个 `<ID>+<用户名>@users.noreply.github.com` 格式
  **才会关联到账号**；写成 `shhzhang@users.noreply.github.com` 也能提交，但头像和主页点不进去。

### 1.3 先验证环境是好的

```bash
dotnet test                       # 期望：Core 386 / App 277，全绿
dotnet build -c Release
```

---

## 2. 五分钟日常循环

```bash
dotnet build                                        # 编译
dotnet test                                         # 全量测试（约 1.5 分钟）
dotnet run --project src/Composa.App                # 跑起来看界面
```

UI 改动想看效果又不想开窗口：**App 测试会把每个界面截成 PNG 存到 `artifacts/screenshots/`**
（已被 gitignore）。跑完 `dotnet test` 直接去看那批图，比开程序快。

---

## 3. 中文版特有的约定（AGENTS.md 里没有）

### 3.1 版本号：`-CN` 是怎么加上去的

- 版本依旧由 **MinVer 从 git 标签推导**，绝不手改版本号文件。
- **但 MinVer 只认 `v` 开头的标签**，而本仓库的标签叫 `1.4.0-CN` → MinVer 看不见它，
  会把构建算成 `1.4.1-alpha.0.N`。两道补丁：
  1. **本机构建**：`Directory.Build.props` 里的 `StampChineseEdition` 目标（挂在 MinVer 之后）
     给**信息版号**追加 `-CN` —— 只改 `InformationalVersion`，`AssemblyVersion`/`FileVersion` 保持纯数字，
     所以不会引发程序集重绑定。`AppInfo.Version` 剥掉 `+<sha>` 后得到 `1.4.0-CN`。
  2. **CI 发布**：`release.yml` 把发布标签**显式**传给 MinVer（`MinVerVersionOverride`），
     两条触发路径都传，这样标签不叫 `v*` 也能得到正确版本号。
- **MSI 的 `ProductVersion` 必须是三段纯数字**（MSI 规范），所以它拿 `1.4.0`；
  给人看的 `1.4.0-CN` 走 `DisplayVersion`，进 `[ProductName]`，控制面板里显示
  「Composa 1.4.0-CN」。

### 3.2 发布者署名

- `Directory.Build.props`：`Authors`/`Company` = **SHHZHANG**，`Copyright` 保留原作者
  （MIT 要求署名，不能删）。
- MSI 的 `Manufacturer` = **SHHZHANG**；`ARPHELPLINK`（原作者 issue 跟踪）**已移除**，保留
  `ARPURLINFOABOUT` 指向上游项目做署名。
- ⚠️ **Linux 包的信息还没改**，仍是上游的 `MAINTAINER="Dennis van der Stelt <…>"` 与
  `HOMEPAGE="https://github.com/dvdstelt/Composa"`（在 `scripts/package/common.sh`）。
  要改成自己的，改那两行即可 —— 这一项我没擅自动，见第 7 节。

### 3.3 汉化的边界（改代码时必须守）

- **只汉化会显示给用户的字符串**：菜单、对话框、按钮、选项栏、状态栏、工具提示、提示/错误消息、
  历史步骤名、混合模式名、调整/滤镜/图层效果名、导入转换报告。
- **绝对不要汉化**：
  - **`src/Composa.App/Mcp/` 全部**（工具描述是给 AI 客户端读的契约，译了会破坏调用）。
    例外：其中两处**步骤名会显示在历史面板**，已译（`ComposaTools.Layers.cs` 的 `"Opacity"`→`"不透明度"`、
    `ComposaTools.Select.cs` 的 `"Color Range"`→`"色彩范围"`）。另外 `ComposaTools.Adjust.cs` 里 color lookup
    工具描述中**列出的外观名**必须是中文，否则客户端按英文名调用会失败。
  - PSD/XCF 的格式常量与键名、`.cube` 解析里的关键字、代码注释、标识符、`Palette.cs` 的颜色/样式类名。
- **快捷键 id 与显示标题分离**：`MainWindow.Commands.cs` 的 `Key(id, key, …, title)` —— `id` 是设置里
  持久化的键，**保持英文**（`MarqueeKey` 等常量就是 id），只有 `title` 用中文。
- **`DockSection` 的键与显示名分离**：`Title` 是设置持久化键（`"History"`，别动），`Label` 才是
  界面上显示的「历史记录」。
- **`HistoryPanel.IconFor` 按步骤名逐字匹配选图标**。规则已改成中文，且旋转/翻转必须用
  **`Contains` 而不是 `StartsWith`** —— 实际步骤名是「**顺时针**旋转画布 90°」这种带前缀的形式。
  新增步骤名如果既不匹配任何规则也不是图层类步骤，要给它补一行。
- **枚举经 `ToString()` 显示的一律要显式 switch**（混合模式、色相范围、形状、网格颜色、抖动像素
  形状、绘画风格、Camera Raw 分组/辉光样式…），别指望自动翻译。
- **测试里有大量直接断言中文界面文字的用例**（54 个测试文件、460+ 处）。改界面文案必须同步改断言，
  否则 `dotnet test` 直接红。

### 3.4 允许保留英文的地方（不是遗漏）

快捷键 id 常量、`Recovery.cs` / `BundledImageMagick.cs` 里的 `Console.Error.WriteLine` 诊断
（GUI 看不到）、`.cube` 文件内注释（写进导出文件供外部工具读）、所有代码注释、
PSD/XCF 格式常量。

---

## 4. 更新检查：现在是关着的，恢复要动三处

**为什么关**：本版自报 `1.4.0-CN`，`ReleaseVersion` 会把它读成上游 `1.4.0` 的**预发布版**，
于是检查一旦可用就会把**原版英文安装包**当成新版本推荐 —— 用户装下去就把中文版换掉了。

**三处封堵（都在，恢复要一起改回）**：

| # | 位置 | 现状 | 恢复时 |
|---|---|---|---|
| 1 | `src/Composa.App/UpdateCheck.cs` → `public static readonly bool Offered` | `false` | 改 `true` |
| 2 | 同文件 → `GitHubReleaseSource.Endpoint` | `http://localhost` | 换成自己的 releases API 地址 |
| 3 | `src/Composa.App/AppInfo.cs` → `ReleasesUrl` | `http://localhost` | 换成自己的 releases 页 |

`Offered` 用 `static readonly` 而不是 `const`：`const` 会让编译器把受它保护的代码判成
**CS0162「无法访问的代码」**，而那段代码要留着（测试在驱动它，将来还要打开）。

界面侧：帮助菜单的「检查更新…」和「自动检查更新」**保留但置灰**，并带说明性提示
（`MainWindow.Updates.cs` 的 `WithdrawnUpdates`）。注意**只关菜单项的 `IsEnabled`，不要用
`Item(..., enabled:)` 参数** —— 那会走 `Execute` 的守卫，连带废掉 12 个 `UpdateWindowTests`。

---

## 5. 打包与发布

### 5.1 本地打包

```bash
# Linux：一次出 AppImage + deb + rpm + tar.gz
scripts/package/all.sh linux-x64 dist

# Windows：一次 publish，出 zip + MSI + setup.exe
scripts/package/windows.sh win-x64 dist                  # Windows
scripts/package/windows.sh win-x64 dist --no-installer   # 只想出 zip + MSI
scripts/package/windows.sh win-arm64 dist --no-msi       # 不想装 WiX
```

产物名形如 `composa-1.4.0-CN-win-x64.msi`（版本串来自 MinVer；验证时可临时用
`COMPOSA_VERSION=1.4.0-CN` 覆盖）。

**MSI 定义**：`packaging/windows/composa-msi.wxs`，x64 与 arm64 **共用一份**，靠变量区分：
`PublishDir` / `DisplayVersion` / `ProductVersion` / `UpgradeCode`，架构由 `wix build -arch` 给。
**UpgradeCode 必须按架构不同**（同 GUID 会让两个包互相当成自己的旧版本）；
x64 的 GUID 是 1.4.0-CN 已发布的值，**不要改**，否则老用户升级不上。
许可页 `packaging/windows/license.rtf`，中文必须写成 `\uNNNN?` 转义（否则乱码）。

### 5.2 由 GitHub Actions 发布（推荐）

**⚠️ 这是 fork，Actions 默认关闭**，第一次要去仓库 **Actions 标签页**点
「I understand my workflows, go ahead and enable them」。

| 工作流 | 触发 | 产出 |
|---|---|---|
| `CI` | push 到 main / 开 PR（**纯 .md 改动不触发**）| 只编译 + 测试 |
| `Packages` | **只在 PR**（且命中 `scripts/**`、`packaging/**`、模型、工作流、csproj/props/targets 等）+ 手动 Run workflow | 只有 **x64**：Linux 四格式 + Windows zip/MSI/setup.exe |
| `Release` | 发布 Release，或**手动 dispatch**（可带 `draft_tag`）| **全部 8 个组合**（双架构 Linux + 双架构 Windows，含两个 MSI），并挂到 Release |

**⚠️ 推送到 main 只跑 CI，不出安装包。**

发版顺序（顺序反了会失败）：

1. Releases → **Draft a new release** → 标签填 `1.4.0-CN`、目标 `main` → **Save draft**（先别发布）
2. Actions → **Release** → **Run workflow** → `draft_tag` 填 `1.4.0-CN`
   （留空则只演练构建，不上传 —— 想先看看能不能过就用这个）
3. 跑完检查草稿里的 14 个文件 + `sha256sums.txt`，确认无误再 **Publish**

> 先跑工作流再建草稿的话，`publish` 任务会在最后一步 `gh release upload` 失败
> （找不到目标 Release）。**产物已经构建好**，补建草稿后 **Re-run failed jobs** 即可，不必重建。

---

## 6. 踩过的坑（省你时间）

- **`wix build -out` 新建文件时会落成 8.3 短名** `COMPOS~1.MSI`（文件系统本身正常，
  用 `touch` 验证过，是 wix 的行为）。`windows.sh` 已在构建后按 `find -newer` 改名兜住。
- **wix 会写 `.wixpdb`**，而发布任务把 `dist/` 里**所有文件**都当下载文件挂上去 →
  `windows.sh` 构建后删除。
- **`-culture zh-CN` 不能省**，否则安装向导的按钮与标题回到英文（Next / Cancel /
  I accept the terms…），光设 `Language="2052"` 没用。
- **WiX 不接受覆盖内置对话框**（重定义 `VerifyReadyDlg` 直接报符号重复）。
  另外 **MSI 的完成页在 `InstallFinalize` 之后**，那里的复选框读不到，**做不了「完成页勾选创建
  快捷方式」** —— 所以桌面快捷方式做成了「功能选择」页上的勾选项（`WixUI_FeatureTree`）。
- **`wix build -d PublishDir` 必须绝对路径**（相对路径按 .wxs 所在目录解析）。
- **Git Bash 下不要用管道接 git/wix 的输出**（`| tail`）—— 会缓冲，交互式认证看起来像卡死。
  写文件：`git push … > log 2>&1`，配合 `GCM_TRACE=<路径>` 才能看到认证过程。
- **排查凭据时不要先设 `GIT_TERMINAL_PROMPT=0`** —— 那会把交互路堵死，报「没有凭据」，
  让你误判成「本机完全没配凭据」。
- **`.workbuddy/` 与 `build/` 已被 gitignore**：前者是协作工具的工作数据，后者是发布树和 MSI。
- **本机 PowerShell 工具里 COM/`Start-Process` 被禁**，别指望用它启动 GUI 或操作 Office。
- **GitHub 的 job 日志接口需要管理员权限**（未认证 403），看日志只能去网页。

---

## 7. 待决 / 未完成

1. **Linux 包的信息仍是上游的**：`scripts/package/common.sh` 的 `MAINTAINER` 和 `HOMEPAGE`
   指向 Dennis van der Stelt / `dvdstelt/Composa`。要换成自己的就改这两行（URL 字段是纯文本，
   不参与测试）。
2. **主菜单助记键重复**：`图像(_I)` 与 `滤镜(_I)` 都是 I，按 Alt+I 无法区分。
   **这是上游自带的**（英文原版 `_Image` 与 `F_ilter` 也都是 I），不是汉化引入的。
   Photoshop 简体中文用的是「滤镜(T)」，改一个字母即可。
3. **更新检查恢复**：等中文版有自己的发布源再说，见第 4 节。
4. **`zh-cn` 分支落后 `main`**（停在 `4baa796`）。后续提交都直接提在 `main` 上，
   如果想让分支保持同步，快进一下即可。
5. **macOS 尚未支持**（上游计划中），新代码请避免平台假设。

---

## 8. 想找什么去哪儿

| 想做的事 | 去哪 |
|---|---|
| 工程规则、渲染/位图所有权、MCP 约束 | **`AGENTS.md`**（上游维护，权威）|
| 发版流程 | `RELEASING.md` |
| 编辑命令（每个操作） | `src/Composa.Core/Editing/EditorSession.*.cs`（按领域拆成 partial）|
| 文档模型、混合模式、图层效果 | `src/Composa.Core/Model/`、`Rendering/` |
| 滤镜与调整 | `src/Composa.Core/Filters/` |
| 文件读写（PSD / XCF） | `src/Composa.Core/IO/Psd/`、`IO/Xcf/` |
| 本地 AI 视觉（主体检测、放大） | `src/Composa.Core/Vision/` |
| 界面（菜单/快捷键/标签页） | `src/Composa.App/MainWindow.*.cs` |
| 画布、工具、标尺参考线 | `src/Composa.App/Controls/CanvasView*.cs` |
| 面板（图层、历史） | `src/Composa.App/Controls/LayersPanel.cs`、`HistoryPanel.cs` |
| 对话框 | `src/Composa.App/Dialogs/` |
| MCP 服务 | `src/Composa.App/Mcp/`（**不要汉化**）|
| 打包脚本 | `scripts/package/`；WiX 定义 `packaging/windows/composa-msi.wxs` |
| 模型下载 | `scripts/models/fetch.sh` |

---

*本文档随仓库走，任何电脑上 `git clone` 之后即可查阅。最后更新：2026-10-14，对应 `main = 711ea16`。*
