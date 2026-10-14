# Releasing Composa

Cutting a release is drafting it on GitHub. The version is derived from the tag by MinVer, so no version number is edited in a file, and the workflow builds and attaches every artifact.

## Steps

1. Move the `## [Unreleased]` entries in `CHANGELOG.md` under a new `## [x.y.z]` heading with today's date, and leave a fresh empty `## [Unreleased]` above it. Add a matching `<release>` entry at the top of the `<releases>` block in `packaging/composa.metainfo.xml`, which is the version history Linux software centres show. Commit both on a branch and merge it.
2. On GitHub, go to Releases and draft a new release. Create a new tag in the form `vX.Y.Z` (for example `v0.2.0`) targeting `main`, and paste in the changelog section for this version. **Save it as a draft; do not publish yet.**
3. Run the **Release** workflow manually, giving it that tag as `draft_tag`. It builds every artifact and attaches them to the draft.
4. Check the draft: every expected artifact is present, and `sha256sums.txt` lists them all. That is, for each of x86-64 and arm64, an AppImage, a `.deb`, an `.rpm` and a tarball for Linux, and a `-setup.exe`, an `.msi` and a zip for Windows: fourteen files plus the checksums. Download one and run it.
5. Publish the draft. GitHub creates the tag at that moment, so nothing is ever pushed from a terminal. A tag containing `-` (for example `v0.3.0-beta.1`) is marked as a pre-release.

Publishing first and letting the workflow fill the release in afterwards also works, and is one step shorter. The reason the draft comes first is the few minutes in between: a published release with no downloads on it is worse than no release, because the update check points people at whatever the latest release is.

### Why the draft needs its tag passed in

GitHub does not create a git tag until a release is published, so while it is still a draft there is no tag for MinVer to read and the artifacts would come out as `0.0.0-alpha.0.N`. Passing `draft_tag` sets MinVer's own version override for that build, so the artifacts carry the version the release is about to have. Publishing the draft then creates that same tag on the same commit, and the two agree.

The workflow takes the version from the release tag in both cases, a draft and a published release alike, rather than letting MinVer infer it. That is what makes this edition's tags work: they are named `1.4.0-CN`, without the `v` MinVer looks for, so MinVer on its own would see the `v1.4.0` tag behind them and version the artifacts `1.4.1-alpha.0.N`. With the override, the tag is the version, whatever it is called. `-CN` then reaches the application through `AppInfo`, which reports `1.4.0-CN`, and the installer through `DisplayVersion`, which puts the same string in the product name.

## The Chinese trial edition

This tree builds the Chinese edition, so a release here is tagged `1.4.0-CN` rather than `v1.4.0`, and the artifacts are named after that tag: `composa-1.4.0-CN-win-x64.msi` and the rest. An MSI's own `ProductVersion` cannot hold `-CN` — it is three numbers and nothing more — so the version that goes into `Programs and Features` comes from `DisplayVersion` instead, and `ProductVersion` carries `1.4.0`.

Updates are withdrawn in this edition, so a release built here will not offer itself to anyone; see `UpdateCheck.Offered` for what has to be put back before that changes.

## Versioning

Composa follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html): breaking is major, a feature is minor, a fix is patch. Pre-release suffixes sort alphabetically, so use `-alpha`, `-beta` and then `-preview` as the last stage before a stable release.

MinVer derives the version from the nearest tag. A commit that is not tagged builds as a pre-release of the next patch, so a development build reports something like `0.2.1-alpha.0.7` and is never mistaken for a release.

## Verifying a build locally

```bash
scripts/publish.sh
```

That produces a self-contained build under `dist/`. To confirm what version a build thinks it is, open About in the Help menu.

## If the release workflow fails

The release stays published with whatever artifacts it managed to attach, which is worse than no release at all, because the update check will start pointing people at it. Either delete the release and its tag and start again, or fix the workflow and re-run the failed jobs so the missing artifacts are attached.
