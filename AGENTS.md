# Repository Guidelines

## Project Structure & Module Organization
`Jellyfin.Plugin.VFQ/` contains the only code project in this repo. Core plugin entrypoints live at the project root: `Plugin.cs`, `PluginServiceRegistrator.cs`, `VfqStartupFilter.cs`, `VfqPlaybackInfoMiddleware.cs`, and `VfqAudioSelectorService.cs`. Configuration types and the embedded admin page live under `Jellyfin.Plugin.VFQ/Configuration/`. Repository-level metadata lives in `Directory.Build.props`, `build.yaml`, and `manifest.json`. CI release automation is defined in `.github/workflows/release.yml`.

## Build, Test, and Development Commands
Use the .NET 9 SDK; CI builds with `9.0.x`.

Build and release are performed by the GitHub pipeline defined in `.github/workflows/release.yml`. Prefer pushing to `main` and letting CI produce the release artifacts when local .NET 9 tooling is unavailable.

- `dotnet restore Jellyfin.Plugin.VFQ.sln` restores Jellyfin plugin dependencies.
- `dotnet build Jellyfin.Plugin.VFQ.sln -c Debug` builds the plugin for local iteration.
- `dotnet build Jellyfin.Plugin.VFQ.sln -c Release` matches the release workflow output.
- `dotnet clean Jellyfin.Plugin.VFQ.sln` clears previous build artifacts.

CI packages `Jellyfin.Plugin.VFQ.dll` into a zip and updates `manifest.json` plus `Directory.Build.props` on pushes to `main`.

## Coding Style & Naming Conventions
Follow the existing C# style: 4-space indentation, file-scoped namespaces, nullable reference types enabled, and implicit usings left on. Use `PascalCase` for public types and members, `_camelCase` for private fields, and keep plugin-specific classes prefixed with `Vfq` when they implement VFQ behavior. Preserve XML documentation on public APIs and keep logging messages explicit about playback decisions.

## Testing Guidelines
There is currently no dedicated test project in this repository. At minimum, validate changes with `dotnet build Jellyfin.Plugin.VFQ.sln -c Release` and a manual playback check in Jellyfin to confirm VFQ track selection and config-page behavior. If you add automated tests, place them in a sibling test project and mirror the production namespaces.

## Commit & Pull Request Guidelines
Keep commit subjects short and imperative, matching the existing history: `Update manifest.json`, `Automate version bump`, `Release v1.0.2.0: update manifest and version [ci skip]`. Reserve `[ci skip]` for release automation commits. Pull requests should describe the playback or configuration behavior changed, link the relevant issue if one exists, and include screenshots only for `configPage.html` UI changes. Note the Jellyfin version used for manual verification.
