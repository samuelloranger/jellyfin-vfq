# Repository Guidelines

## Project Structure & Module Organization
`Jellyfin.Plugin.VFQ/` contains the only code project in this repo. Core plugin entrypoints live at the project root: `Plugin.cs`, `PluginServiceRegistrator.cs`, `VfqStartupFilter.cs`, `VfqPlaybackInfoMiddleware.cs`, and `VfqAudioSelectorService.cs`. Configuration types and the embedded admin page live under `Jellyfin.Plugin.VFQ/Configuration/`. Repository-level metadata lives in `Directory.Build.props`, `build.yaml`, and `manifest.json`. CI release automation is defined in `.github/workflows/release.yml`.

## Build, Test, and Development Commands
Use the .NET 10 SDK; CI builds with `10.0.x`. The plugin targets `net10.0` and builds against `Jellyfin.Controller` / `Jellyfin.Model` `12.0.0-rc7` (Jellyfin 12). The 2.x release line is Jellyfin 12 only; Jellyfin 10.11 support ended at 1.0.5.0.

Build and release are performed by the GitHub pipeline defined in `.github/workflows/release.yml`. Prefer pushing to `main` and letting CI produce the release artifacts when local .NET 10 tooling is unavailable.

- `dotnet restore Jellyfin.Plugin.VFQ.sln` restores Jellyfin plugin dependencies.
- `dotnet build Jellyfin.Plugin.VFQ.sln -c Debug` builds the plugin for local iteration.
- `dotnet build Jellyfin.Plugin.VFQ.sln -c Release` matches the release workflow output.
- `dotnet clean Jellyfin.Plugin.VFQ.sln` clears previous build artifacts.

CI packages `Jellyfin.Plugin.VFQ.dll` into a zip and updates `manifest.json` plus `Directory.Build.props` on pushes to `main`. New manifest entries are written with `targetAbi: 12.0.0.0`. The release line (`MAJOR.MINOR`) is read from `Directory.Build.props`; CI only bumps the patch within that line, so starting a new line means editing that file.

### Jellyfin version pin

`Jellyfin.Plugin.VFQ.csproj` declares `<JellyfinVersion>`, which both `PackageReference`s consume. It is the single place the released-against Jellyfin version lives, and it is overridable:

```bash
dotnet build Jellyfin.Plugin.VFQ.sln -c Release -p:JellyfinVersion=12.0.0
```

When Jellyfin 12.0.0 goes stable, bump `<JellyfinVersion>` from `12.0.0-rc7` to `12.0.0` and re-verify before releasing. `targetAbi` stays `12.0.0.0` either way.

### Forward-compatibility checks

GitHub cannot trigger a workflow from a release in a repository you do not control — `repository_dispatch` would have to be sent by `jellyfin/jellyfin`, and webhooks need admin rights there. So `.github/workflows/jellyfin-compat.yml` polls instead, on `schedule` plus manual dispatch.

Three things about GitHub's cron that affect this workflow:

- **Schedules run only from the default branch.** The workflow does nothing on a schedule while it lives on a feature branch; it is dispatch-only until merged to `main`.
- **Delivery is best-effort.** Runs queue and can be delayed or skipped under load, so the cron sits at `23 6 * * 1` rather than on the hour, which is the most contended slot. A skipped weekly run is harmless here.
- **Schedules auto-disable after 60 days without repository activity** on public repos. GitHub emails a warning; any push resets the clock. Worth knowing for a plugin that goes quiet between Jellyfin releases — the check can stop without failing.

Times are UTC and do not follow DST. The `@daily` and `@weekly` shorthands are not supported, and the shortest interval is 5 minutes.

`scripts/resolve-jellyfin-version.py` compares `<JellyfinVersion>` against the newest published `Jellyfin.Controller` on NuGet. The comparison is stateless — no cache, no committed marker. Scheduled runs stop early when the two match; dispatch runs accept a `jellyfin_version` input and a `force` flag. The resolver rejects prerelease tags outside `alpha|beta|rc` + digits, because `jellyfin.controller` carries a malformed `12.0.0-rcrc3` that sorts above `12.0.0-rc7` under SemVer prerelease ordering. Run it directly to see what it would pick:

```bash
scripts/resolve-jellyfin-version.py          # pinned, latest, docker tag, changed
scripts/resolve-jellyfin-version.py --list   # every candidate, newest first
```

When a newer Jellyfin exists the workflow builds against it and then runs `scripts/smoke-test.sh <docker-tag>`, which boots that Jellyfin in Docker, installs the built plugin, and asserts the PlaybackInfo response. A build alone would not catch a removed method — that only throws at plugin load, which is how 1.0.5.0 broke. On failure the workflow opens (or comments on) an issue labelled `jellyfin-compat`. Published releases are unaffected; they stay built against the pin.

Run the smoke test locally against any tag:

```bash
dotnet build Jellyfin.Plugin.VFQ.sln -c Release
scripts/smoke-test.sh 12.0-rc7
```

It needs `docker`, `ffmpeg`, `curl` and `python3`, generates its own two-audio-track test file, and cleans up its container and temp directory on exit. It asserts a negative control — with `EnableAutoSelect` off, Jellyfin's own default must win — so the test cannot pass by accident if Jellyfin ever starts picking VFQ itself.

## Coding Style & Naming Conventions
Follow the existing C# style: 4-space indentation, file-scoped namespaces, nullable reference types enabled, and implicit usings left on. Use `PascalCase` for public types and members, `_camelCase` for private fields, and keep plugin-specific classes prefixed with `Vfq` when they implement VFQ behavior. Preserve XML documentation on public APIs and keep logging messages explicit about playback decisions.

## Testing Guidelines
There is currently no dedicated test project in this repository. At minimum, validate changes with `dotnet build Jellyfin.Plugin.VFQ.sln -c Release` and a manual playback check in Jellyfin to confirm VFQ track selection and config-page behavior. If you add automated tests, place them in a sibling test project and mirror the production namespaces.

## Commit & Pull Request Guidelines
Keep commit subjects short and imperative, matching the existing history: `Update manifest.json`, `Automate version bump`, `Release v1.0.2.0: update manifest and version [ci skip]`. Reserve `[ci skip]` for release automation commits. Pull requests should describe the playback or configuration behavior changed, link the relevant issue if one exists, and include screenshots only for `configPage.html` UI changes. Note the Jellyfin version used for manual verification.
