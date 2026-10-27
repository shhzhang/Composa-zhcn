# 修复记录：画布文字工具无法输入中文

**日期**：2026-10-27
**引入版本**：本版 `V1.4.0-CN-R1`
**影响范围**：Type（文字）工具在画布上的全部键盘输入
**结论**：不是汉化改坏的，是上游自绘文字编辑器一直没有向平台申请输入法。

---

## 一、症状

在画布上点一下开始输入文字时：

1. 输入法语言栏是灰的，**切不到中文**；
2. 强制切到中文输入法后按键无反应，**连拼音都不出现**；
3. 只能敲出 ASCII 字符（英文、数字、标点），中文一个字也进不去。

三个症状同时出现，说明问题不在"字形"层，而在**输入法根本没接上**。

## 二、根因

### 2.1 Avalonia 的 Windows 后端：没有 client 就把 IMM 摘掉

`Avalonia.Win32.Input.Imm32InputMethod.SetClient(null)` 的实现是 `ImmAssociateContext(hwnd, 0)` —— 直接把输入法上下文从窗口上解除关联。此后：

- 语言栏判定该窗口不支持输入法 → 变灰；
- `WM_IME_*` 消息不再进来 → 没有组合串；
- `WindowImpl` 里 `WM_CHAR` 的处理被 `!IsComposing` 挡着 → 也拿不到字符。

而「有没有 client」取决于焦点元素会不会回答 `TextInputMethodClientRequested` 事件：

```
焦点落到元素
  → 框架发 TextInputMethodClientRequested
    → 元素 e.Client = xxx   ⇒ SetClient(xxx)，输入法开
    → 无人回答               ⇒ SetClient(null)，输入法关
```

`TextBox` 有 `TextBoxTextInputMethodClient` 兜着，所以输入框里中文一直正常。而 `CanvasView` 的文字工具是**自己用 Skia 画的编辑器**（`TextEditor` + `TextLayout`），从来不回答这个请求 —— 于是 Windows 认为这个窗口不要输入法。

> 也就是说：原版就只支持英文直接输入，中文版只是把这个既有缺陷暴露得更明显。

### 2.2 第二个坑：默认字体 `Inter` 一个中文字形都没有

即使输入法接上了，`TextStyle.FontFamily` 的默认值是 `"Inter"` —— 那是 Avalonia 的**界面字体**，Skia 的字体集合里未必有，即便有也只有拉丁字形。中文会画成一排空白/豆腐块。所以修输入法的同时必须换默认字体，否则"能输入但看不见"。

## 三、改动

### 3.1 新增 `src/Composa.App/Controls/CanvasView.TextInput.cs`

按 Avalonia 自家 `TextBoxTextInputMethodClient` 的契约，把 canvas 当成一个文本字段报给平台：

| 成员 | 含义 |
| --- | --- |
| `TextViewVisual` | 返回 canvas 自身 |
| `CursorRectangle` | 插入点的矩形（**控件坐标**），Windows 用它定位组合窗与候选框 |
| `Selection` / `SurroundingText` | 字符索引，与 `TextEditor` 的计数一致，无需换算 |
| `SupportsPreedit` | 允许平台把未上屏的组合串交回来 |
| `SetPreeditText` | 收下组合串，转存到 `preedit` 字段并重绘 |

关键点：

- **只在编辑时才提供 client**：`AcceptsTextInput => session?.TextEdit != null`。编辑结束立刻撤回并触发 `TextInputMethodClientRequeryRequested`，让平台重新问一次（平台自己不会再问）。这样输入法跟着文字工具进出，画笔/选框状态下不会抢键盘。
- **选中变化要回灌编辑器**：`Selection` 的 setter 走 `textClientApplying` 闸门，避免平台与编辑器互相纠正成死循环。
- **编辑器变化要回灌平台**：`TextEditor.Changed` 订阅到 `NotifyTextChanged()`，光标矩形/选区/上下文文字三者一起刷新。

### 3.2 `CanvasView.cs` / `CanvasView.Text.cs`

- 构造里订阅 `InputMethod.TextInputMethodClientRequestedEvent`；
- 文本编辑开启/关闭时调用 `SyncTextInput()`；
- `HandleTextKey` 在 `preedit` 非空时**直接返回 false**：组合期间的 Space、数字键、回车都属于输入法（选候选词），不能被当成"打空格/打数字/换行"；
- 渲染阶段把组合串画在插入点：用图层字体与颜色，带下划线，尺寸随图层缩放，此时**文档里仍是空的**；有组合串时光标让位；
- 结束编辑/切换图层时清空 `preedit`，否则残留会画到下一次编辑里。

**坐标坑（踩过一次）**：`TextLayout` 的坐标是**图层像素**，不是文档像素。光标矩形第一版直接乘视图矩阵，移动/旋转过的文字层候选框会错位。正确算法：

```csharp
var toView = layer.Matrix.PostConcat(ViewMatrix());
```

### 3.3 默认字体 → 微软雅黑

`src/Composa.Core/Model/Layer.cs`：

```csharp
public string FontFamily { get; init; } = DefaultFamily;
public static string DefaultFamily => OperatingSystem.IsWindows() ? "Microsoft YaHei" : "Inter";
```

- Windows 上用 **Microsoft YaHei**：系统必装、有完整中文字形；
- 其他平台保留 `Inter` 并交给 Skia 做字体回退（Linux 上硬塞雅黑等于指定一个不存在的字体）。

配套：`MainWindow.Options.cs` 里字体下拉的匹配改为**大小写不敏感**，因为字体管理器回来的拼写不一定与默认值一字不差。

## 四、验证

- `dotnet build`：0 错误。
- `dotnet test`：**Core 387 + App 280 = 667 全绿**（修复前基线 663）。
- 新增/更新的测试：
  - `TextTests.New_text_is_set_in_a_face_that_has_Chinese_letters`（默认字体真的能画出中文）
  - `TextEditingTests.The_canvas_offers_an_input_method_client_only_while_text_is_open`
  - `TextEditingTests.Keys_belong_to_the_input_method_while_it_is_composing`
  - `TextEditingTests.The_composition_is_drawn_in_place_but_not_typed_into_the_document`
  - `XcfTests` 里默认字体断言同步更新（默认族改变）
- 截图 `artifacts/screenshots/87-text-composition.png`：模拟组合「zhong」时，组合串带下划线画在插入点、字体栏显示 `Microsoft YaHei`、图层内容仍为空。

### 仍需真人验证

无头测试跑不了 Windows 的真实 IME 消息循环。发布前请手动确认：

1. 选文字工具在画布点一下，语言栏应**亮起**；
2. 切中文输入法打「中文」，候选框应**贴着光标**；
3. 拼音带下划线出现在画布上，空格/数字选词正常，回车后落进文档且下划线消失。

若仍有问题，按现象分流：**语言栏不亮** → 查 client 请求这条线（3.1）；**候选框位置错** → 查坐标换算这条线（3.2 的 `layer.Matrix.PostConcat`）。

## 五、版本号：V1.4.0-CN-R1

原来的后缀只有 `-CN`（`1.4.0` → `1.4.0-CN`）。本版加上自己的修订号 **`-R1`**，用来区分「同一上游版本下先后打包的几次中文修正」——上游没有这个概念，这是本版自己的标记。

`Directory.Build.props`：

```xml
<ChineseEditionSuffix>-CN-R1</ChineseEditionSuffix>
```

仍然只进 `InformationalVersion`，`AssemblyVersion` 保持 `1.0.0.0`（MinVer 的 major 位）、`FileVersion` 为 `1.4.0.0`、MSI `ProductVersion` 由 `msi_product_version` 剥出三段纯数字 `1.4.0`。所以控制面板/产品名显示 `Composa 1.4.0-CN-R1`，而 MSI 规范要求的数字版本不受影响。

另外补了一个基准号兜底（**本仓库的中文标签长成 `1.4.0-CN`，没有 MinVer 认的 `v` 前缀，它推不出正确版本**；官方发布流程也是显式把版本传进来覆盖它）：

```xml
<PropertyGroup Condition="'$(MinVerVersionOverride)' == ''">
  <MinVerVersionOverride>1.4.0</MinVerVersionOverride>
</PropertyGroup>
```

只要调用方给了值就不再插手 —— 命令行 `-p:MinVerVersionOverride=` 与 CI 写进环境的同名变量优先级都更高，发布时照旧由标签决定版本。

`-R1` 带来一个打包上的连带问题：RPM 的 `Release` 字段不接受连字符，`1.4.0-CN-R1` 直接让 `rpmbuild` 报
`Illegal char '-'`。已在 `scripts/package/common.sh` 里处理：RPM 取 version `1.4.0` + release `0.CN.R1`；
Debian 只把第一个 `-` 换成 `~`、其余换成 `.`，得到 `1.4.0~CN.R1` —— 排在 `1.4.0~CN` 之后、`1.4.0` 之前，
升级顺序正确。

实际产物（有仓库时 MinVer 还会追加提交哈希作为 build metadata，`AppInfo.Version` 在 `+` 处截断）：

```
AssemblyInformationalVersion = 1.4.0-CN-R1+<sha>   ← AppInfo.Version 报告 1.4.0-CN-R1
AssemblyFileVersion          = 1.4.0.0
AssemblyVersion              = 1.0.0.0
```

## 六、后续注意

1. **新加任何自绘输入入口**（例如给图层重命名做画布内编辑），都要回答 `TextInputMethodClientRequested`，否则同样只能打英文。可以复用 `CanvasView.TextInput.cs` 的写法。
2. **组合串不写进文档**：`preedit` 只用于绘制，提交由平台的 `TextInput` 路径负责。改这块时别"顺手"把它塞进 `TextEditor`，会造成上屏两遍。
3. **macOS / Linux 未验证**：本修复只对齐了 Windows 的 IMM32 行为，其他平台若后端实现不同，需要单独验证。
4. **更新检查仍然是关的**。`1.4.0-CN-R1` 里的 `CN-R1` 会被读成预发布标识符，与 `-CN` 同样的道理，恢复更新前必须先有自己的发布源。
