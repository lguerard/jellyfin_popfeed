# Contributing

This document collects the development, release, and Jellyfin installation details so the main README can stay focused on what the plugin does.

## Jellyfin Install

The preferred install path is through a custom Jellyfin plugin repository.

Jellyfin expects a URL to a `manifest.json` file. It does not provide a UI flow where you paste the raw JSON contents directly into the catalog.

For stable releases, the repository manifest URL is:

```text
https://raw.githubusercontent.com/lguerard/jellyfin_popfeed/release-manifest/manifest.json
```

This URL is meant to be added once in Jellyfin and then left alone. Each new stable tag release updates the manifest on the `release-manifest` branch so Jellyfin can detect plugin updates from the same repository entry.

After a release is published, use the `manifest.json` asset URL from that release:

```text
https://github.com/lguerard/jellyfin_popfeed/releases/download/v0.1.0/manifest.json
```

In Jellyfin for normal stable installs:

1. Open Dashboard.
2. Open Plugins.
3. Open Repositories.
4. Add a new repository.
5. Paste the fixed stable repository URL: `https://raw.githubusercontent.com/lguerard/jellyfin_popfeed/release-manifest/manifest.json`.
6. Save, refresh the catalog, then install or update Popfeed from the plugin list.

Use the version-specific release asset URL only when you explicitly want to test one published prerelease manifest instead of following the stable update channel.

For a prerelease published from manual dispatch, use the matching prerelease tag in the same URL shape, for example:

```text
https://github.com/lguerard/jellyfin_popfeed/releases/download/v0.1.1-beta.1/manifest.json
```

If you do not want to use a repository URL, you can install manually instead:

1. Download the plugin ZIP from the GitHub release.
2. Extract the contents.
3. Copy the plugin files into your Jellyfin `plugins/Popfeed` directory.
4. Restart Jellyfin.

Typical plugin locations include `/var/lib/jellyfin/plugins/` on many Linux installs, `%UserProfile%\AppData\Local\jellyfin\plugins` for direct Windows installs, and `%ProgramData%\Jellyfin\Server\plugins` for tray installs.

## Local Build

Build the plugin with:

```powershell
dotnet build .\Jellyfin.Plugin.Popfeed\Jellyfin.Plugin.Popfeed.csproj
```

Create a release ZIP locally with:

```powershell
.\build-release.ps1 -Configuration Release -Version v0.1.0
```

That writes a ZIP under `artifacts/` containing the plugin DLL, PDB when available, and the project README.

## Manifest Generation

To generate a Jellyfin repository manifest alongside the ZIP, pass the future release asset URL for the ZIP:

```powershell
.\build-release.ps1 `
  -Configuration Release `
  -Version v0.1.0 `
  -ManifestSourceUrl https://github.com/<owner>/<repo>/releases/download/v0.1.0/Jellyfin.Plugin.Popfeed-v0.1.0.zip
```

That also writes `artifacts/manifest.json`.

Before publishing it, verify that the file starts with `[` and therefore contains a JSON array at the root. Jellyfin custom repositories skip manifests that serialize as a single object instead of an array.

The manifest metadata is derived from `build.yaml`, and the generated version entry contains:

- the normalized plugin version
- the changelog from `build.yaml`
- the target Jellyfin ABI
- the release ZIP download URL
- the ZIP MD5 checksum
- the generation timestamp

## GitHub Actions Release Flow

The workflow in `.github/workflows/release.yml` supports two release paths.

### Stable Release From Tag Push

Push a tag like `v0.1.0` to create a normal GitHub release.

The workflow will:

1. Build the plugin ZIP.
2. Generate `artifacts/manifest.json` pointing at the release ZIP URL for that tag.
3. Upload both files to the GitHub release.
4. Update the stable repository manifest at `https://raw.githubusercontent.com/lguerard/jellyfin_popfeed/release-manifest/manifest.json`.

If users report that the plugin does not appear in Jellyfin after adding the repository URL, check the published manifest URL directly first. The common failure mode is a manifest with the wrong JSON root shape.

Quick verification:

```powershell
Invoke-WebRequest https://raw.githubusercontent.com/lguerard/jellyfin_popfeed/release-manifest/manifest.json | Select-Object -ExpandProperty Content
```

The response must be valid JSON whose root value is an array, and the `sourceUrl` inside it must point to an existing release ZIP.

This is the low-maintenance release path and should be the default way to publish user-facing updates.

### Prerelease From Manual Dispatch

Run the workflow manually and provide `tag_name`, for example `v0.1.1-beta.1`.

The workflow will publish a GitHub prerelease for that tag and upload the matching ZIP and `manifest.json` assets.

Use the prerelease `manifest.json` asset URL in Jellyfin if you want to test that prerelease through the repository flow.

Prereleases do not update the stable repository manifest.

## Notes For Contributors

- Keep `build.yaml` in sync with user-facing plugin metadata and changelog text.
- Prefer release tags that start with `v` because both the workflow trigger and current packaging examples assume that format.
- If the GitHub release step fails with a permissions error, check repository or organization Actions settings for read and write workflow token permissions.
