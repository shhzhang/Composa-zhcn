# Settings and updates

## What Composa remembers

Between launches, Composa keeps: the window size and whether it was maximized, the recent files, the JPEG export quality, the view options (rulers, grid, guides, snapping and what to snap to, locked guides, the pixel grid and the transform controls), Auto Select, the Detect choice for subjects, the Resample choice of Image Size, whether the History panel is shown or collapsed and how tall it is, your changed keyboard shortcuts, whether AI control is allowed, and the update check settings. Tool settings and colors carry from tab to tab within a session but start fresh at the next launch.

## Where files live

| | Linux | Windows |
|---|---|---|
| Settings | `~/.config/composa` | `%APPDATA%\Composa` |
| Recovery copies | `~/.cache/composa/recovery` | `%LOCALAPPDATA%\Composa\recovery` |
| Downloaded updates | your Downloads folder, as your desktop names it | your Downloads folder, wherever you moved it |

On Linux the XDG environment variables are honoured if you set them. The pipe an AI agent connects through lives in the same cache folder; see [AI control](ai-control.md).

## Updates

**Withdrawn in this edition.** The Chinese trial edition ships without update checking at all: Help > Check for Updates and its automatic counterpart are still in the menu but greyed out, and nothing reaches the network at launch. This build calls itself `1.4.0-CN`, which reads as a pre-release of the upstream `1.4.0`, so an offered update would announce the original English build as newer — and installing it would replace this one. The feed address behind the check points at `http://localhost` as well, so nothing is fetched even if the check is reached some other way. `UpdateCheck.Offered` records the three things to put back.

Everything below describes how updates work upstream, and is what returns once this edition has a release feed of its own.

Help > Check for Updates asks the releases page whether a newer version exists and tells you the result. With Help > Check for Updates Automatically on, Composa asks once a day at launch and shows a strip under the menu when a newer version is available, with Download, a link to its release notes, Skip this version, and a dismiss button. A check that fails says nothing. A stable version is never offered a pre-release.

Download fetches the file that replaces your copy, chosen by how it was installed and the processor it runs on: the `.deb`, `.rpm`, AppImage or tarball on Linux, the installer or the zip on Windows. The strip shows how far it got, with Cancel. The file goes into your Downloads folder and is checked against the checksums published with the release before it is offered; one that does not match is deleted. A file already there with the same contents is used as it is, and one with the same name but other contents is left alone, the download taking a name such as `composa_1.3.0_amd64 (1).deb`. A build run from source (`dotnet run`) has no file to download and offers the release notes only.

When the download is ready, Show in Folder opens the file manager with the file selected. Install, where the format has an installer, hands the file over:

- **Windows installer**: Composa quits, asking about unsaved documents as File > Quit does (Cancel stops the install too), and starts the setup, which upgrades it in place. The builds are not signed, so SmartScreen may warn about the setup, as it does for one downloaded from the releases page.
- **`.deb` and `.rpm`**: the package opens in your software installer (GNOME Software, KDE Discover or Ubuntu's App Center), and Composa keeps running. Some software centres refuse a package that does not come from a repository, so the strip also shows the command that installs it, `sudo apt install ...` or `sudo dnf install ...`, with a Copy button.
- **AppImage, tarball and Windows zip**: there is nothing to install. A downloaded AppImage is made executable, so it starts from the file manager; the tarball and the zip are yours to unpack.

The downloaded file stays in Downloads afterwards. Nothing is downloaded until you press Download, nothing is started until you press Install, and Composa never replaces its own files. The download sends the same bare `User-Agent: Composa` as the check and nothing else.

The `.deb` and `.rpm` check too: they are downloaded from the releases page and installed by hand, so no repository will offer you the next version. Download and Install in the strip upgrade them, as does installing the new release's file over the old one, with `sudo apt install ./composa_*.deb` or `sudo dnf install ./composa-*.rpm`. To stop the automatic check on any build, set the environment variable `COMPOSA_DISABLE_UPDATE_CHECK` to 1.

## If something goes wrong

An unexpected error shows a "Something went wrong" dialog with the error and advice to save a copy with File > Save As. The document stays open. A crash leaves recovery copies, which the next launch offers to restore.
