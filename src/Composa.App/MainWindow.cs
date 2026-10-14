using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow : Window
{
    private readonly List<EditorSession> sessions = [];
    private EditorSession? session;
    private readonly CanvasView canvas = new();
    private readonly LayersPanel layers = new() { Width = 296 };
    private readonly HistoryPanel history = new();
    /// <summary>The right-hand column: the Layers panel with the panels of the Window menu under it.</summary>
    private readonly SideDock dock;
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Border optionsHost = new() { Height = 40, Background = Palette.Panel, Padding = new Thickness(12, 0) };
    private readonly Dictionary<Tool, ToolButton> toolButtons = [];
    private readonly TextBlock zoomText = new() { Width = 56, Foreground = Palette.Secondary };
    private readonly TextBlock sizeText = new() { Foreground = Palette.Secondary };
    private readonly TextBlock positionText = new() { Foreground = Palette.Secondary, Width = 96 };
    private readonly TextBlock hintText = new() { Foreground = Palette.Secondary, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock aiText = new() { Foreground = Palette.Accent };
    private Mcp.McpHost? aiControl;
    private readonly Border foregroundSwatch = new() { Width = 26, Height = 26, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(3) };
    private readonly Border backgroundSwatch = new() { Width = 26, Height = 26, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(3) };
    private readonly Panel welcome;
    private readonly UpdateNotice updateNotice = new();
    private Action? refreshOptions;
    private readonly Settings settings = Settings.Load();
    private readonly Recovery? recovery = Settings.Persist ? new Recovery() : null;
    // The status bar shows a problem in orange, or a note in the hint's color, in place of the tool hint until the next command.
    private string? problem, note;

    public MainWindow()
    {
        Title = "Composa";
        Width = Math.Clamp(settings.WindowWidth, 800, 10000);
        Height = Math.Clamp(settings.WindowHeight, 520, 10000);
        if (settings.Maximized) WindowState = WindowState.Maximized;
        canvas.ShowPixelGrid = settings.ShowPixelGrid;
        canvas.ShowTransformControls = settings.ShowTransformControls;
        canvas.AutoSelect = settings.AutoSelect;
        canvas.ObjectClick = SelectObjectAt;
        canvas.ObjectBox = SelectObjectIn;
        jpegQuality = Math.Clamp(settings.JpegQuality, 1, 100);
        MinWidth = 800;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://composa/Assets/icon.png")));

        welcome = BuildWelcome();
        var canvasHost = new Panel { Children = { canvas, welcome } };
        var center = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };
        center.Children.Add(BuildToolRail());
        AddAt(center, Ui.Separator(), 1).Margin = new Thickness(0);
        AddAt(center, canvasHost, 2);
        AddAt(center, Ui.Separator(), 3).Margin = new Thickness(0);
        var historySection = new DockSection("History", history, settings.Dock.GetValueOrDefault("History") ?? new DockPanelState(), label: "历史记录");
        historySection.Changed += () => { settings.Dock["History"] = historySection.State; settings.Save(); };
        // The column has the Layers panel's width, so a long step name is cut off rather than widening it.
        dock = new SideDock(layers, historySection) { Width = layers.Width };
        AddAt(center, dock, 4);
        history.GoToRequested += GoToHistory;

        // The update notice sits under the menu, where it is visible without covering anything.
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto,Auto") };
        root.Children.Add(BuildMenu());
        AddRow(root, updateNotice, 1);
        AddRow(root, BuildTabBar(), 2);
        AddRow(root, optionsHost, 3);
        AddRow(root, Ui.Separator(false), 4);
        AddRow(root, center, 5);
        AddRow(root, Ui.Separator(false), 6);
        AddRow(root, BuildStatusBar(), 7);
        Content = root;

        WireUpdateNotice();
        StartUpdateCheck();

        canvas.ViewChanged += UpdateStatus;
        canvas.PointerAt += point => positionText.Text = point is { } p ? $"{p.X}, {p.Y}" : "";
        canvas.Problem += message => { problem = message; UpdateStatus(); };
        canvas.ToolStateChanged += () => { refreshOptions?.Invoke(); UpdateColors(); };
        // Opening text from the canvas with another tool switches to the Type tool, so the toolbar has to follow.
        canvas.TextEditingChanged += () => { if (session != null) ShowTool(session.Tool); RebuildOptions(); UpdateStatus(); };
        layers.EditTextRequested += BeginTextEdit;
        layers.EditAdjustmentRequested += layer => _ = EditAdjustmentLayer(layer, isNew: false);
        layers.NewAdjustmentRequested += kind => _ = NewAdjustmentLayer(kind);
        layers.EditEffectRequested += (layer, kind) => _ = EditEffect(layer, kind);
        layers.NewEffectRequested += kind => _ = NewEffect(kind);

        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) => canvas.ModifierKeyChanged(e.Key, e.KeyModifiers, down: true), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) => canvas.ModifierKeyChanged(e.Key, e.KeyModifiers, down: false), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) => { if (!SwallowAlt(e)) canvas.HandleKeyUp(e); }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
        Closing += OnClosing;

        SetSession(null);
        if (settings.AllowAiControl) _ = SetAiControl(true);

        if (recovery != null)
        {
            var autosave = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            autosave.Tick += (_, _) => recovery.Save(sessions);
            autosave.Start();
            Opened += (_, _) => _ = OfferRecovery();
        }
    }

    private async Task OfferRecovery()
    {
        var abandoned = recovery!.FindAbandoned();
        if (abandoned.Count == 0) return;
        var names = string.Join("\n", abandoned.Select(e => $"• {e.Title}（自动保存于 {e.SavedAt:g}）"));
        var recover = await Dialogs.Prompts.Confirm(this, "恢复未保存的工作",
            $"Composa 上次未正常关闭。以下文档有未保存的更改：\n\n{names}\n\n是否恢复？选择「取消」将丢弃自动保存的副本。", "恢复");
        foreach (var entry in abandoned)
        {
            if (recover)
            {
                try
                {
                    var restored = new EditorSession(Composa.IO.ProjectFile.Load(entry.ProjectPath)) { SuggestedName = entry.Title + "（已恢复）" };
                    restored.History.BaseName = "已恢复";
                    restored.MarkModified();
                    AddSession(restored);
                }
                catch (Exception error)
                {
                    await Dialogs.Prompts.Alert(this, "无法恢复 " + entry.Title, error.Message + "\n\n自动保存的副本保留在 " + entry.ProjectPath);
                    continue;
                }
            }
            recovery.Discard(entry);
        }
    }

    private static T AddAt<T>(Grid grid, T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
        return control;
    }

    private static void AddRow(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    public EditorSession? Session => session;
    /// <summary>Every open document, in tab order.</summary>
    public IReadOnlyList<EditorSession> Sessions => sessions;
    /// <summary>Whether a drag on the canvas is mutating a bitmap in place, when nothing else may touch the document.</summary>
    internal bool IsDragging => canvas.IsDragging;
    /// <summary>The remembered preferences; tests read them back without anything reaching disk.</summary>
    public Settings Settings => settings;
    public CanvasView Canvas => canvas;
    /// <summary>The tool rail's button for a tool.</summary>
    public ToolButton RailButton(Tool tool) => toolButtons[tool];

    // ---- Sessions and tabs --------------------------------------------------------------------------------------

    public void AddSession(EditorSession added)
    {
        // The first document starts from the remembered view options; later ones inherit them from the current tab.
        if (lastToolSource == null) { added.View = settings.View; added.Detect = settings.Detect; }
        sessions.Add(added);
        added.HistoryChanged += RebuildTabs;
        added.Problem += message => { if (added == session) ShowProblem(message); };
        added.LayersChanged += () => { if (added == session) OnSessionLayersChanged(); };
        added.TextChanged += () => { if (added == session) refreshOptions?.Invoke(); };
        added.SelectionChanged += () => { if (added == session) refreshOptions?.Invoke(); };
        SetSession(added);
    }

    private void SetSession(EditorSession? next)
    {
        if (session != null && session != next)
        {
            canvas.CancelInteraction();
            if (session.IsPreviewing) session.CancelPreview();
            if (session.IsEditingText) session.FinishText();
            if (session.ColorRange != null) session.CommitColorRange();
        }
        var tool = session?.Tool ?? Tool.Move;
        session = next;
        if (session != null) CarryToolState(session, tool);
        canvas.Session = session;
        layers.Session = session;
        history.Session = session;
        welcome.IsVisible = session == null;
        RebuildTabs();
        RebuildOptions();
        UpdateColors();
        UpdateStatus();
        if (session != null) canvas.Focus();
    }

    private EditorSession? lastToolSource;

    /// <summary>Tool choice, colors and brush settings follow the user from tab to tab.</summary>
    private void CarryToolState(EditorSession target, Tool tool)
    {
        if (lastToolSource is { } from && from != target)
        {
            target.Foreground = from.Foreground; target.Background = from.Background; target.Brush = from.Brush;
            target.EraserMode = from.EraserMode; target.SmearMode = from.SmearMode; target.MarqueeKind = from.MarqueeKind; target.LassoKind = from.LassoKind;
            target.Feather = from.Feather; target.WandTolerance = from.WandTolerance; target.WandContiguous = from.WandContiguous;
            target.SampleAllLayers = from.SampleAllLayers; target.CloneAligned = from.CloneAligned; target.ShapeKind = from.ShapeKind;
            target.ShapeCornerRadius = from.ShapeCornerRadius; target.GradientRadial = from.GradientRadial; target.GradientToTransparent = from.GradientToTransparent; target.TextDefaults = from.TextDefaults;
            target.ShapeLineWidth = from.ShapeLineWidth; target.WandMode = from.WandMode; target.ObjectEdgeOffset = from.ObjectEdgeOffset; target.View = from.View; target.Detect = from.Detect;
            target.SelectionExpandAmount = from.SelectionExpandAmount; target.SelectionContractAmount = from.SelectionContractAmount; target.SelectionFeatherAmount = from.SelectionFeatherAmount;
            target.CropRatio = from.CropRatio;
            target.Tool = tool;
        }
        lastToolSource = target;
        ShowTool(target.Tool);
    }

    private void RebuildTabs()
    {
        tabs.Children.Clear();
        foreach (var item in sessions)
        {
            var label = Ui.Label(item.Title + (item.IsModified ? " •" : ""), item == session ? Palette.Foreground : Palette.Secondary);
            var close = new Button { Classes = { "flat" }, Padding = new Thickness(3), Content = Icons.Create(Icons.Close, 10), VerticalAlignment = VerticalAlignment.Center };
            close.Click += (_, e) => { _ = CloseSession(item); e.Handled = true; };
            var tab = new Border
            {
                Child = Ui.Row(8, label, close), Padding = new Thickness(12, 4, 6, 4), CornerRadius = new CornerRadius(6),
                Background = item == session ? Palette.PanelRaised : Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand)
            };
            tab.PointerPressed += (_, e) =>
            {
                // A right-click opens the tab's menu without switching to it, as browsers do.
                var pressed = e.GetCurrentPoint(tab).Properties;
                if (pressed.IsMiddleButtonPressed) _ = CloseSession(item);
                else if (pressed.IsLeftButtonPressed && item != session) SetSession(item);
            };
            tab.ContextMenu = TabMenu(item);
            tabs.Children.Add(tab);
        }
        Title = session == null ? "Composa" : $"{session.Title}{(session.IsModified ? " •" : "")} - Composa";
    }

    /// <summary>A click or a scrub in the History panel. Like any command it waits for a drag on the canvas to end.</summary>
    private void GoToHistory(int index)
    {
        if (session == null || canvas.IsDragging) return;
        var typing = session.IsEditingText;
        problem = note = null;
        session.GoToHistory(index);
        // Going to another state commits the text being typed, which the Type bar has to hear about.
        if (typing) RebuildOptions();
        UpdateStatus();
    }

    /// <summary>What a right-click on a tab offers, for that document whether or not it is the current one.</summary>
    private ContextMenu TabMenu(EditorSession item)
    {
        MenuItem Entry(string header, Action run, bool enabled = true)
        {
            var entry = new MenuItem { Header = header, IsEnabled = enabled };
            entry.Click += (_, _) => run();
            return entry;
        }
        return new ContextMenu
        {
            Items =
            {
                Entry("复制图像", () => _ = CopyImage(item)),
                Entry("复制", () => AddSession(item.Duplicate())),
                new Separator(),
                Entry("在文件夹中显示", () => _ = ShowInFolder(item.FilePath!), item.FilePath != null),
                new Separator(),
                Entry("关闭", () => _ = CloseSession(item)),
                Entry("关闭其他", () => _ = CloseOthers(item), sessions.Count > 1)
            }
        };
    }

    /// <summary>Closes every other document, each asking about unsaved changes as Close does; a Cancel stops there.</summary>
    private async Task CloseOthers(EditorSession keep)
    {
        foreach (var other in sessions.Where(s => s != keep).ToList())
            if (!await CloseSession(other)) return;
        if (session != keep && sessions.Contains(keep)) SetSession(keep);
    }

    /// <summary>Shows a file selected in the file manager, or at least opens its folder.</summary>
    private async Task ShowInFolder(string path)
    {
        if (!await FileReveal.Show(path, Launcher))
            ShowProblem("无法打开文件夹 " + (Path.GetDirectoryName(Path.GetFullPath(path)) ?? path) + "。");
    }

    private async Task<bool> CloseSession(EditorSession item)
    {
        // A save still writing finishes first, so its file is never cut short and the prompt knows whether it is needed.
        while (saving.TryGetValue(item, out var writing)) await writing.Task;
        // Text still being typed is an open edit: commit it so it counts as a change and is in what gets saved.
        if (item.IsEditingText) item.FinishText();
        if (item.IsModified)
        {
            if (item != session) SetSession(item);
            var answer = await Dialogs.Prompts.SaveChanges(this, item.Title);
            if (answer == null) return false;
            if (answer == true && !await Save(item, saveAs: false)) return false;
        }
        var index = sessions.IndexOf(item);
        sessions.Remove(item);
        recovery?.Forget(item);
        item.HistoryChanged -= RebuildTabs;
        if (lastToolSource == item) lastToolSource = null;
        if (item == session) SetSession(sessions.Count == 0 ? null : sessions[Math.Clamp(index, 0, sessions.Count - 1)]);
        else RebuildTabs();
        GC.Collect();
        return true;
    }

    private bool closingConfirmed;

    private void RememberWindow()
    {
        settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { settings.WindowWidth = Width; settings.WindowHeight = Height; }
        settings.JpegQuality = jpegQuality;
        RememberToolSettings();
    }

    /// <summary>
    /// Auto Select, the transform controls, the pixel grid, rulers, guides, the grid and the snapping switches keep
    /// whatever they were last set to, across tabs and across launches, the way Photoshop's tool options do.
    /// </summary>
    private void RememberToolSettings()
    {
        settings.ShowPixelGrid = canvas.ShowPixelGrid;
        settings.ShowTransformControls = canvas.ShowTransformControls;
        settings.AutoSelect = canvas.AutoSelect;
        if (session != null) { settings.View = session.View; settings.Detect = session.Detect; }
        settings.Save();
    }

    /// <summary>Whether the MCP server is running, so an AI agent can connect and drive the editor.</summary>
    public bool AiControl => aiControl != null;

    public async Task SetAiControl(bool on)
    {
        if (on == AiControl) return;
        settings.AllowAiControl = on;
        settings.Save();
        if (on)
        {
            var host = new Mcp.McpHost(this);
            host.ConnectionsChanged += UpdateAiText;
            if (await host.StartAsync()) aiControl = host;
            else ShowProblem("另一个 Composa 窗口已允许 AI 控制；智能体将连接到那个窗口。");
        }
        else
        {
            aiControl!.Dispose();
            aiControl = null;
        }
        UpdateAiText();
    }

    private void UpdateAiText()
    {
        var connected = aiControl?.Connections ?? 0;
        aiText.IsVisible = connected > 0;
        aiText.Text = connected == 1 ? "AI 已连接" : $"{connected} 个 AI 已连接";
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        RememberWindow();
        aiControl?.Dispose();
        if (session?.IsEditingText == true) session.FinishText();
        if (closingConfirmed || (saving.Count == 0 && sessions.All(s => !s.IsModified) && download == null)) return;
        e.Cancel = true;
        if (!await ConfirmQuit()) return;
        closingConfirmed = true;
        Close();
    }

    /// <summary>
    /// What quitting waits for and asks about: saves still writing finish, so no file is cut short;
    /// each unsaved document asks, and a Cancel stops the quit; a download under way is cancelled and
    /// its partial file removed. False when the person cancelled.
    /// </summary>
    private async Task<bool> ConfirmQuit()
    {
        // Text being typed is an open edit that does not count as a change until it is committed,
        // so it is committed first or quitting would pass it by without asking.
        foreach (var typing in sessions.Where(s => s.IsEditingText)) typing.FinishText();
        while (saving.Count > 0) await Task.WhenAll(saving.Values.Select(w => w.Task).ToList());
        foreach (var item in sessions.Where(s => s.IsModified).ToList())
            if (!await CloseSession(item)) return false;
        await StopDownload();
        return true;
    }

    // ---- Chrome -------------------------------------------------------------------------------------------------

    private Control BuildTabBar()
    {
        var newButton = Ui.IconButton(Icons.Plus, "新建画布 (Ctrl+N)", () => _ = NewCanvas());
        var scroll = new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var zoomControls = Ui.Row(2,
            Ui.TextButton("适合屏幕", () => canvas.Fit()), Ui.TextButton("100%", () => canvas.ZoomTo(1)),
            Ui.IconButton(Icons.ZoomOut, "缩小 (Ctrl+-)", canvas.ZoomOut), Ui.IconButton(Icons.ZoomIn, "放大 (Ctrl++)", canvas.ZoomIn));
        foreach (var button in zoomControls.Children.OfType<Button>()) { button.MinWidth = 0; button.Classes.Add("flat"); }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Background = Palette.Window, Margin = new Thickness(6, 2) };
        grid.Children.Add(newButton);
        AddAt(grid, scroll, 1).Margin = new Thickness(6, 0);
        AddAt(grid, zoomControls, 2);
        return grid;
    }

    private static readonly (Tool Tool, Icons.Icon Icon, string Tip)[] ToolList =
    [
        (Tool.Move, Icons.Move, "移动 / 变换 (V)"), (Tool.Marquee, Icons.Marquee, "选框 (M)"), (Tool.Lasso, Icons.Lasso, "套索 (L)"),
        (Tool.Wand, Icons.Wand, "魔棒 (W)"), (Tool.Crop, Icons.Crop, "裁剪 (C)"), (Tool.Brush, Icons.Brush, "画笔 (B) · 橡皮擦 (E)"),
        (Tool.SpotHealing, Icons.Heal, "污点修复画笔 (J)"), (Tool.CloneStamp, Icons.Stamp, "仿制图章 (S) · 按住 Alt 单击设置源"),
        (Tool.Smear, Icons.Drop, "涂抹 (R)"), (Tool.Gradient, Icons.Gradient, "渐变 (G)"), (Tool.Shape, Icons.Shape, "形状 (U)"),
        (Tool.Text, Icons.Text, "文字 (T) · 单击创建点文字，拖动创建段落文本框，单击文字进行编辑"), (Tool.Eyedropper, Icons.Eyedropper, "吸管 (I)"),
        (Tool.Hand, Icons.Hand, "抓手 (H) · 任意工具下按住空格"), (Tool.Zoom, Icons.Zoom, "缩放 (Z)")
    ];

    /// <summary>
    /// The tools a rail button holds, which it offers beside itself when held down, as Photoshop groups its tools. Empty for a
    /// tool on its own. The keys are looked up when the group opens, so a rebound key shows as it is now.
    /// </summary>
    private IReadOnlyList<ToolChoice> ToolGroup(Tool tool)
    {
        ToolChoice Choice(string name, Icons.Icon icon, string key, Func<EditorSession, bool> isCurrent, Action<EditorSession> apply) => new(
            name, icon, () => toolKeys.FirstOrDefault(k => k.Id == key)?.Gesture, () => session != null && isCurrent(session),
            () =>
            {
                if (session == null) return;
                apply(session);
                SelectTool(tool);
                canvas.Focus();
            });
        ToolChoice[] Kinds<T>(string key, Func<EditorSession, T> get, Action<EditorSession, T> set, params (T Kind, string Name, Icons.Icon Icon)[] kinds) where T : struct, Enum =>
            kinds.Select(k => Choice(k.Name, k.Icon, key, s => get(s).Equals(k.Kind), s => set(s, k.Kind))).ToArray();
        return tool switch
        {
            Tool.Marquee => Kinds(MarqueeKey, s => s.MarqueeKind, (s, v) => s.MarqueeKind = v,
                (MarqueeKind.Rectangle, "矩形选框", Icons.Marquee), (MarqueeKind.Ellipse, "椭圆选框", Icons.MarqueeEllipse)),
            Tool.Lasso => Kinds(LassoKey, s => s.LassoKind, (s, v) => s.LassoKind = v,
                (LassoKind.Freehand, "套索", Icons.Lasso), (LassoKind.Polygonal, "多边形套索", Icons.PolygonLasso)),
            Tool.Wand => Kinds(MagicKey, s => s.WandMode, (s, v) => s.WandMode = v,
                (WandMode.Wand, "魔棒", Icons.Wand), (WandMode.Object, "对象选择", Icons.ObjectSelect)),
            Tool.Brush =>
            [
                Choice("画笔", Icons.Brush, BrushKey, s => !s.EraserMode, s => s.EraserMode = false),
                Choice("橡皮擦", Icons.Eraser, EraserKey, s => s.EraserMode, s => s.EraserMode = true)
            ],
            Tool.Smear => Kinds(SmearKey, s => s.SmearMode, (s, v) => s.SmearMode = v,
                (SmearMode.Liquify, "液化", Icons.Liquify), (SmearMode.Blur, "模糊", Icons.Drop), (SmearMode.Smudge, "抹擦", Icons.Smudge),
                (SmearMode.Dodge, "减淡", Icons.Dodge), (SmearMode.Burn, "加深", Icons.Burn)),
            Tool.Shape => Kinds(ShapeKey, s => s.ShapeKind, (s, v) => s.ShapeKind = v,
                (ShapeKind.Rectangle, ShapeStyle.DisplayName(ShapeKind.Rectangle), Icons.Rectangle),
                (ShapeKind.RoundedRectangle, ShapeStyle.DisplayName(ShapeKind.RoundedRectangle), Icons.RoundedRectangle),
                (ShapeKind.Ellipse, ShapeStyle.DisplayName(ShapeKind.Ellipse), Icons.Ellipse), (ShapeKind.Line, ShapeStyle.DisplayName(ShapeKind.Line), Icons.Line)),
            _ => []
        };
    }

    private Control BuildToolRail()
    {
        var rail = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (tool, icon, tip) in ToolList)
        {
            var button = new ToolButton(icon, tip, ToolGroup(tool));
            button.Click += (_, _) => SelectTool(tool);
            toolButtons[tool] = button;
            rail.Children.Add(button);
        }

        foregroundSwatch.Cursor = backgroundSwatch.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(foregroundSwatch, "前景色");
        ToolTip.SetTip(backgroundSwatch, "背景色");
        foregroundSwatch.PointerPressed += (_, _) => _ = PickColor(foreground: true);
        backgroundSwatch.PointerPressed += (_, _) => _ = PickColor(foreground: false);
        backgroundSwatch.Margin = new Thickness(14, 14, 0, 0);
        foregroundSwatch.HorizontalAlignment = backgroundSwatch.HorizontalAlignment = HorizontalAlignment.Left;
        foregroundSwatch.VerticalAlignment = backgroundSwatch.VerticalAlignment = VerticalAlignment.Top;
        var swatches = new Panel { Width = 42, Height = 42, Margin = new Thickness(0, 8, 0, 0), Children = { backgroundSwatch, foregroundSwatch } };
        rail.Children.Add(swatches);
        var swap = Ui.IconButton(Icons.Swap, "交换颜色 (X) · D 恢复为黑白", () => { session?.SwapColors(); UpdateColors(); }, 14);
        swap.HorizontalAlignment = HorizontalAlignment.Center;
        rail.Children.Add(swap);

        return new ScrollViewer
        {
            Content = rail, Width = 56, Background = Palette.Panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private Control BuildStatusBar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), Height = 28, Background = Palette.Panel };
        zoomText.Margin = new Thickness(14, 0, 8, 0);
        sizeText.Margin = new Thickness(0, 0, 24, 0);
        hintText.HorizontalAlignment = HorizontalAlignment.Right;
        hintText.Margin = new Thickness(0, 0, 14, 0);
        grid.Children.Add(zoomText);
        AddAt(grid, sizeText, 1);
        AddAt(grid, positionText, 2);
        AddAt(grid, hintText, 3);
        aiText.Margin = new Thickness(0, 0, 14, 0);
        aiText.IsVisible = false;
        AddAt(grid, aiText, 4);
        foreach (var text in new[] { zoomText, sizeText, positionText, hintText, aiText }) text.FontSize = 11.5;
        return grid;
    }

    private Panel BuildWelcome()
    {
        var title = Ui.Label("Composa", size: 26, weight: FontWeight.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var subtitle = Ui.Label("新建画布、打开项目或图像，或将文件拖放到此处。", Palette.Secondary);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        var buttons = Ui.Row(10, Ui.TextButton("新建画布…", () => _ = NewCanvas(), accent: true), Ui.TextButton("打开…", () => _ = Open()));
        buttons.HorizontalAlignment = HorizontalAlignment.Center;
        var box = Ui.Column(14, title, subtitle, buttons);
        var recent = settings.RecentFiles.Where(p => File.Exists(p) || Directory.Exists(p)).Take(6).ToList();
        if (recent.Count > 0)
        {
            var heading = Ui.Label("最近使用", Palette.Secondary);
            heading.HorizontalAlignment = HorizontalAlignment.Center;
            heading.Margin = new Thickness(0, 18, 0, 0);
            box.Children.Add(heading);
            foreach (var path in recent)
            {
                var link = new Button { Classes = { "flat" }, Content = Ui.Label(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), Palette.Accent), HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(8, 3) };
                ToolTip.SetTip(link, path);
                link.Click += (_, _) => _ = OpenPaths([path]);
                box.Children.Add(link);
            }
        }
        box.VerticalAlignment = VerticalAlignment.Center;
        return new Panel { Children = { box } };
    }

    private void UpdateColors()
    {
        if (session == null) return;
        foregroundSwatch.Background = new SolidColorBrush(session.Foreground.ToAvalonia());
        backgroundSwatch.Background = new SolidColorBrush(session.Background.ToAvalonia());
    }

    private async Task PickColor(bool foreground)
    {
        if (session == null) return;
        var target = session;
        // Text being typed follows the foreground color, so it previews the picker's working color as the Type bar's own
        // swatch does (on the selected letters, or all of them), and goes back to its own colors on Cancel.
        var editing = foreground ? target.TextEdit : null;
        var original = target.CurrentTextStyle;
        void Recolor(SKColor color) { if (editing != null && target.TextEdit == editing) target.SetTextColor((uint)color | 0xFF000000); }
        var picked = await Dialogs.Prompts.Color(this, foreground ? "前景色" : "背景色", foreground ? target.Foreground : target.Background, editing != null ? Recolor : null);
        if (picked is not { } color)
        {
            if (editing != null && target.TextEdit == editing) target.RestoreTextColors(original);
            return;
        }
        if (foreground) target.Foreground = color; else target.Background = color;
        Recolor(color);
        UpdateColors();
        refreshOptions?.Invoke();
    }

    /// <summary>Opens a text layer for typing on the canvas.</summary>
    private void BeginTextEdit(Layer layer)
    {
        if (session == null || layer.Text == null) return;
        if (session.Tool != Tool.Text) SelectTool(Tool.Text);
        canvas.EditText(layer);
        RebuildOptions();
        UpdateStatus();
    }

    public void SelectTool(Tool tool)
    {
        if (session == null) { foreach (var button in toolButtons.Values) button.IsChecked = false; return; }
        session.Tool = tool;
        problem = note = null;
        ShowTool(tool);
        canvas.ToolChanged();
        RebuildOptions();
        UpdateStatus();
    }

    /// <summary>Marks the tool's button and shows each group's current tool on its button.</summary>
    private void ShowTool(Tool tool)
    {
        if (session == null) return;
        foreach (var (key, button) in toolButtons)
        {
            button.IsChecked = key == tool;
            button.Refresh();
        }
    }

    private void UpdateStatus()
    {
        if (session == null)
        {
            zoomText.Text = "";
            sizeText.Text = "";
            hintText.Text = "准备就绪";
            return;
        }
        zoomText.Text = canvas.Zoom >= 0.1 ? $"{canvas.Zoom * 100:0.#}%" : $"{canvas.Zoom * 100:0.##}%";
        sizeText.Text = $"{session.Document.Width} × {session.Document.Height} px · {session.Document.Resolution:0.#} ppi · sRGB";
        hintText.Text = problem ?? (saving.Count > 0 ? "正在保存 " + string.Join(", ", saving.Values.Select(w => Path.GetFileName(w.Path))) + "…" : note ?? Hint(session));
        hintText.Foreground = problem != null ? new SolidColorBrush(Color.Parse("#FFB454")) : Palette.Secondary;
    }

    private static string Hint(EditorSession s) => s.Tool == Tool.Move ? ToolHint(s) : ToolHint(s) + " · 按住 Ctrl 拖动可移动图层";

    private static string ToolHint(EditorSession s) => s.Tool switch
    {
        Tool.Move => "拖动以移动 · 控制点调整大小（Shift 自由缩放，Alt 从中心缩放）· 在边角外侧旋转 · Ctrl+拖动角可扭曲 · Ctrl+单击选取图层 · 1–0 不透明度",
        Tool.Marquee => "拖动以选择 · Shift 相加 · Alt 相减 · Shift+Alt 交叉 · 在选区内拖动可移动 · Delete 清除 · Ctrl+D 取消选择",
        Tool.Lasso => s.LassoKind == LassoKind.Freehand ? "拖动以选择 · Shift 相加 · Alt 相减 · 在选区内拖动可移动" : "单击各角点 · 单击起点、双击或按 Enter 闭合 · Backspace 移除角点 · Escape 取消",
        Tool.Wand => s.WandMode == WandMode.Object ? "单击对象以选择其轮廓 · 在小对象周围拖出一个框 · 按 Tab 切换到魔棒 · Shift 相加 · Alt 相减" : "单击以选择相似颜色 · 按 Tab 切换到对象选择 · Shift 相加 · Alt 相减",
        Tool.Crop => "拖动以裁剪 · Shift 保持比例 · Alt 对称 · Enter 应用 · Escape 取消",
        Tool.Brush => (s.EraserMode ? "拖动以擦除" : "拖动以绘画 · Alt+单击拾取颜色") + " · Shift+单击绘制直线 · [ ] 调整大小 · { } 调整硬度 · 1–0 不透明度",
        Tool.SpotHealing => "在瑕疵上拖动以修复 · [ ] 调整大小",
        Tool.CloneStamp => "Alt+单击设置源 · 拖动以仿制 · [ ] 调整大小 · 1–0 不透明度",
        Tool.Smear => "拖动以" + (s.SmearMode == SmearMode.Liquify ? "推移像素" : s.SmearMode.ToString().ToLowerInvariant()) + " · [ ] 调整大小 · 1–0 强度",
        Tool.Gradient => "拖动以从前景色绘制到" + (s.GradientToTransparent ? "透明" : "背景色") + " · 拖动端点进行调整 · Shift 吸附到 45° · Enter 应用 · Escape 取消",
        Tool.Shape => s.ShapeKind == ShapeKind.Line ? "拖动以在新图层上绘制直线 · Shift 吸附到 45° · 按 Tab 切换到下一个形状" : "拖动以在新图层上绘制形状 · Shift 绘制正方形 · Alt 从中心绘制 · 按 Tab 切换到下一个形状",
        Tool.Text => s.IsEditingText ? "输入 · 拖动文本框控制点调整其大小 · Alt+方向键调整字距与行距 · Ctrl+Enter 完成 · Escape 取消" : "单击创建点文字 · 拖动创建段落文本框 · 单击文字进行编辑",
        Tool.Eyedropper => "单击拾取前景色 · Alt+单击拾取背景色",
        Tool.Hand => "拖动以平移 · Ctrl+滚轮缩放",
        _ => "单击放大 · Alt+单击缩小 · 向右或向左拖动以平滑缩放"
    };

    private bool reportingFailure;

    /// <summary>Tells the user about an unexpected error, once at a time, after putting the editor back into a sane state.</summary>
    public void ReportFailure(Exception error)
    {
        try { canvas.CancelInteraction(); }
        catch (Exception secondary) { Console.Error.WriteLine(secondary); }
        if (reportingFailure) return;
        reportingFailure = true;
        _ = Show();

        async Task Show()
        {
            try
            {
                await Dialogs.Prompts.Alert(this, "出现了问题",
                    $"{error.GetType().Name}: {error.Message}\n\n上一个操作可能未完成。文档仍处于打开状态；建议现在保存一份副本（文件 > 另存为）。");
            }
            finally { reportingFailure = false; }
        }
    }

    public void ShowProblem(string message)
    {
        problem = message;
        UpdateStatus();
    }

    private void ShowNote(string message)
    {
        note = message;
        UpdateStatus();
    }
}
