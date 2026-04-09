# Jellyfin Popfeed Plugin

This plugin pushes Jellyfin watched and unwatched actions to Popfeed over ATProto.

It supports separate Popfeed credentials per Jellyfin user and posts watched items as native Popfeed activity records. It can also create an additional Bluesky post for the same watched event when enabled per user.

It also supports an exclusion list of Jellyfin item ids so specific movies or full series can be hidden from automatic Popfeed sync.

## Current mapping

This plugin models Jellyfin watch activity by:

- creating `social.popfeed.feed.review` records as native Popfeed activity when a title is marked watched
- deleting the matching plugin-created activity record when a title is marked unwatched, if enabled
- optionally creating an additional `app.bsky.feed.post` record when the per-user Bluesky checkbox is enabled

## Supported media

- movies with IMDb or TMDb ids
- episodes with TMDb TV series id plus season/episode numbers, or episode-level IMDb/TMDb ids

## Configuration

Open the plugin settings page in Jellyfin and set:

- the watch-state strategy
- one or more per-user Popfeed mappings using the row editor

Each mapping can include:

- `JellyfinUserId`
- `Identifier`
- `AppPassword`
- `PdsUrl` (optional, defaults to `https://bsky.social`)
- `BlueskyPostLanguage` (optional, defaults to `en`)
- `PostWatchedItemsToBluesky` (optional)
- `Enabled` (optional)

You can also configure exclusions directly from the settings page by searching for items and adding them to the excluded list.

Exclusion behavior:

- add a movie id to block that movie from automatic sync
- add a series id to block all episodes from that series
- add an episode id to block only that specific episode

If `EnableDebugLogging` is enabled, the plugin writes detailed decision logs for event handling, exclusion checks, ATProto session creation, list lookup, and record create/delete actions.

## Install

The easiest install path is through a custom Jellyfin plugin repository entry that points to this plugin's `manifest.json`.

Jellyfin expects a manifest URL, not raw JSON pasted into the UI.

For stable releases, use this repository manifest URL in Jellyfin:

```text
https://raw.githubusercontent.com/lguerard/jellyfin_popfeed/release-manifest/manifest.json
```

That URL is intended to stay constant. Each new stable tag release updates the manifest behind it so existing Jellyfin installs can see new plugin versions without changing the repository entry.

For maintenance, the stable flow is just as simple: publish a normal tag like `v0.1.0`, and the workflow will update that fixed Jellyfin manifest URL automatically.

Manual workflow dispatch remains for prereleases. Those prereleases keep their own version-specific release assets and do not replace the stable repository manifest.

For release and development details, including the exact Jellyfin steps and manual install fallback, see [CONTRIBUTING.md](CONTRIBUTING.md).

## Settings Page Helpers

The plugin settings page now includes:

- a row-based editor for per-user Popfeed account mappings
- item search for building the exclusion list without editing JSON
- a test sync panel that can run either a dry run or a real sync for a selected item and Jellyfin user
- a recent activity panel that shows the latest sync results per Jellyfin user

The dry-run test does not write to Popfeed. It validates the current mapping, exclusion rules, and identifier mapping, and shows you the result in the plugin page while also writing logs.

## GitHub Release Packaging

To build a ready-to-upload ZIP for GitHub Releases:

```powershell
.\build-release.ps1 -Configuration Release -Version v0.1.0
```

This creates a release archive under `artifacts/` containing the plugin DLL, PDB when available, and the README.

If you also want a Jellyfin repository manifest for direct install by manifest URL, pass the release asset URL while packaging:

```powershell
.\build-release.ps1 -Configuration Release -Version v0.1.0 -ManifestSourceUrl https://github.com/<owner>/<repo>/releases/download/v0.1.0/Jellyfin.Plugin.Popfeed-v0.1.0.zip
```

That also writes `artifacts/manifest.json`, which you can attach to the GitHub release. For stable installs in Jellyfin, users should still use the fixed repository URL shown above rather than a version-specific release asset URL.

The repository also includes a GitHub Actions workflow at `.github/workflows/release.yml` that builds and uploads the ZIP automatically for tags like `v0.1.0`.

For tagged GitHub releases, the workflow uploads both the plugin ZIP and a matching `manifest.json`, and it asks GitHub to generate release notes automatically. The full release workflow, prerelease flow, and Jellyfin repository setup are documented in [CONTRIBUTING.md](CONTRIBUTING.md).

## Build

```powershell
dotnet build .\Jellyfin.Plugin.Popfeed\Jellyfin.Plugin.Popfeed.csproj
```

Copy the built DLL into your Jellyfin `plugins/Popfeed` directory.
