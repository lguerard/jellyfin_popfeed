# Jellyfin Popfeed Plugin

This plugin pushes Jellyfin watched and unwatched actions to Popfeed over ATProto.

It now supports separate Popfeed credentials per Jellyfin user and routes watch-state writes through a strategy interface so the current list-item approach can be replaced when Popfeed ships a dedicated watched-history lexicon.

It also supports an exclusion list of Jellyfin item ids so specific movies or full series can be hidden from automatic Popfeed sync.

## Current mapping

Popfeed does not currently expose a dedicated watched-history record comparable to Trakt history. This plugin therefore models watched state by:

- creating or reusing a `social.popfeed.feed.list` record named `Watched`
- creating `social.popfeed.feed.listItem` records in that list
- setting list item `status` to `#finished`
- deleting the matching list item when a title is marked unwatched, if enabled

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
- `WatchedListName` (optional, defaults to `Watched`)
- `Enabled` (optional)

You can also configure exclusions directly from the settings page by searching for items and adding them to the excluded list.

Exclusion behavior:

- add a movie id to block that movie from automatic sync
- add a series id to block all episodes from that series
- add an episode id to block only that specific episode

If `EnableDebugLogging` is enabled, the plugin writes detailed decision logs for event handling, exclusion checks, ATProto session creation, list lookup, and record create/delete actions.

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

That also writes `artifacts/manifest.json`, which you can attach to the GitHub release and use as the custom repository URL in Jellyfin.

The repository also includes a GitHub Actions workflow at `.github/workflows/release.yml` that builds and uploads the ZIP automatically for tags like `v0.1.0`.

For tagged GitHub releases, the workflow now uploads both the plugin ZIP and a matching `manifest.json`, and it asks GitHub to generate release notes automatically.

## Build

```powershell
dotnet build .\Jellyfin.Plugin.Popfeed\Jellyfin.Plugin.Popfeed.csproj
```

Copy the built DLL into your Jellyfin `plugins/Popfeed` directory.