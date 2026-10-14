using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Composa.App.Tests;

public class UpdateNoticeTests
{
    private static readonly ReleaseVersion Version = new(9, 9, 9, "");

    private static (Window Window, UpdateNotice Notice) Open()
    {
        var notice = new UpdateNotice();
        var window = new Window { Width = 900, Height = 120, Content = new StackPanel { Children = { notice } } };
        window.Show();
        return (window, notice);
    }

    [AvaloniaFact]
    public void The_notice_stays_out_of_the_way_until_there_is_something_to_say()
    {
        var (_, notice) = Open();
        Assert.False(notice.IsVisible);
        Assert.Equal(UpdateNotice.Phase.Hidden, notice.State);
    }

    [AvaloniaFact]
    public void Showing_a_version_renders_the_strip()
    {
        var (window, notice) = Open();
        notice.Show(Version, canDownload: true);
        Assert.True(notice.IsVisible);
        Assert.Equal(UpdateNotice.Phase.Available, notice.State);
        Assert.Equal(["下载", "发行说明", "跳过此版本"], notice.Buttons);

        Assert.True(Screenshots.Save(window, "20-update-notice"));
    }

    /// <summary>A developer build, a managed build or a release without this install's file: the release notes are all there is.</summary>
    [AvaloniaFact]
    public void Without_a_file_to_fetch_there_is_no_Download()
    {
        var (_, notice) = Open();
        notice.Show(Version, canDownload: false);
        Assert.Equal(["发行说明", "跳过此版本"], notice.Buttons);
    }

    [AvaloniaFact]
    public void A_download_shows_how_far_it_got()
    {
        var (window, notice) = Open();
        var cancelled = 0;
        notice.CancelDownload += () => cancelled++;
        notice.Show(Version, canDownload: true);
        notice.ShowProgress(Version, new DownloadProgress(12_345_678, 45_117_217));

        Assert.Equal(UpdateNotice.Phase.Downloading, notice.State);
        Assert.Equal("正在下载 Composa 9.9.9… 12.3 / 共 45.1 MB", notice.Message);
        Assert.Equal(["取消"], notice.Buttons);
        Assert.True(Screenshots.Save(window, "20-update-downloading"));

        // Without a size from the server there is only the count.
        notice.ShowProgress(Version, new DownloadProgress(2_000_000, null));
        Assert.Equal("正在下载 Composa 9.9.9… 已下载 2.0 MB", notice.Message);

        notice.Press("取消");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, cancelled);
    }

    /// <summary>The bar along the bottom is as long as the part that arrived.</summary>
    [AvaloniaFact]
    public void The_bar_fills_with_the_download()
    {
        var (window, notice) = Open();
        notice.ShowProgress(Version, new DownloadProgress(25, 100));
        Screenshots.Save(window, "20-update-downloading-quarter");
        var bar = notice.GetVisualDescendants().OfType<Border>().Single(b => b.Height == 3);
        var fill = (Border)bar.Child!;
        Assert.Equal(bar.Bounds.Width / 4, fill.Width, 0.5);
        Assert.True(fill.Width > 100);

        notice.ShowProgress(Version, new DownloadProgress(100, 100));
        Assert.Equal(bar.Bounds.Width, fill.Width, 0.5);
    }

    [AvaloniaFact]
    public void A_downloaded_package_offers_its_folder_its_installer_and_its_command()
    {
        var (window, notice) = Open();
        window.Height = 160;
        notice.ShowReady(Version, "/home/ada/Downloads/composa_9.9.9_amd64.deb", "Open the package in your software installer",
            "sudo apt install /home/ada/Downloads/composa_9.9.9_amd64.deb");

        Assert.Equal(UpdateNotice.Phase.Ready, notice.State);
        Assert.Equal("Composa 9.9.9 已就绪，位于 Downloads：composa_9.9.9_amd64.deb", notice.Message);
        Assert.Equal(["在文件夹中显示", "安装"], notice.Buttons);
        Assert.Equal("sudo apt install /home/ada/Downloads/composa_9.9.9_amd64.deb", notice.Command);
        Assert.True(Screenshots.Save(window, "20-update-ready-package"));
    }

    [AvaloniaFact]
    public void A_download_without_an_installer_is_shown_in_its_folder_only()
    {
        var (window, notice) = Open();
        notice.ShowReady(Version, "/home/ada/Downloads/Composa-9.9.9-x86_64.AppImage", null, null);
        Assert.Equal(["在文件夹中显示"], notice.Buttons);
        Assert.Null(notice.Command);
        Assert.True(Screenshots.Save(window, "20-update-ready-appimage"));
    }

    [AvaloniaFact]
    public async Task The_command_is_copied_to_the_clipboard()
    {
        var (window, notice) = Open();
        notice.ShowReady(Version, "/home/ada/Downloads/composa-9.9.9-1.x86_64.rpm", "Open the package in your software installer",
            "sudo dnf install /home/ada/Downloads/composa-9.9.9-1.x86_64.rpm");
        var copy = notice.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "复制");
        copy.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 20 && copy.Content as string != "已复制"; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, TestContext.Current.CancellationToken); }
        Assert.Equal("已复制", copy.Content);
        Assert.Equal("sudo dnf install /home/ada/Downloads/composa-9.9.9-1.x86_64.rpm", await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!));
    }

    [AvaloniaFact]
    public void A_failed_download_says_why_and_offers_to_try_again()
    {
        var (window, notice) = Open();
        var retried = 0;
        notice.Download += () => retried++;
        notice.ShowFailed("无法连接到 GitHub。请检查网络连接后重试。");

        Assert.Equal(UpdateNotice.Phase.Failed, notice.State);
        Assert.Equal("无法连接到 GitHub。请检查网络连接后重试。", notice.Message);
        Assert.Equal(["重试", "发行说明"], notice.Buttons);
        Assert.True(Screenshots.Save(window, "20-update-failed"));

        notice.Press("重试");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, retried);
    }

    [AvaloniaFact]
    public void Dismissing_hides_it_without_skipping_the_version()
    {
        var (_, notice) = Open();
        var skipped = false;
        notice.Skip += () => skipped = true;

        notice.Show(Version, canDownload: true);
        notice.Hide();

        Assert.False(notice.IsVisible);
        Assert.False(skipped); // Dismissing is "not now", not "never tell me again".
    }

    [AvaloniaFact]
    public void Skipping_raises_the_event_and_hides_it()
    {
        var (_, notice) = Open();
        var skipped = 0;
        notice.Skip += () => skipped++;

        notice.Show(Version, canDownload: true);
        var skip = notice.GetVisualDescendants().OfType<Button>()
            .First(b => (b.Content as string) == "跳过此版本");
        // Ui.TextButton subscribes to Click rather than binding a Command, so the event is what
        // has to be raised; executing a Command here would silently do nothing.
        skip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, skipped);
        Assert.False(notice.IsVisible);
    }
}
