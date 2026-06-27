<p align="center">
  <img src="logo.svg" width="160" alt="VFQ Auto Selector Logo" />
</p>

<h1 align="center">VFQ Auto Selector for Jellyfin</h1>

<p align="center">
  <strong>Automatically select Québécois/French Canadian (VFQ) audio tracks in Jellyfin.</strong>
</p>

---

## 📖 Overview

**VFQ Auto Selector** is a Jellyfin server plugin that intercepts playback requests and automatically selects French Canadian/Québécois (VFQ) audio tracks for movies and TV shows. It scans track titles, languages, and metadata using a comprehensive set of keywords and tags (e.g., `VFQ`, `FR-CA`, `Québécois`, `Canadien`, `QC`). 

If multiple matching VFQ tracks exist, it selects the highest quality track based on codec and channel configurations.

---

## ✨ Features

- **Automatic Selection:** Scans audio track metadata server-side to instantly select the right track on playback start.
- **Robust Keyword Detection:** Matches standard tags: `VFQ`, `FR-CA`, `FRCA`, `Québécois`, `Quebecois`, `QC`, `Canadien`.
- **Quality-Based Ranking:** Intelligently picks higher channel counts (e.g., 5.1/7.1 over Stereo) and preferred codecs.
- **Client Agnostic:** Intercepts Jellyfin's server-side playback info API, meaning it works out-of-the-box on Android TV, web, iOS, Apple TV, and Roku.

---

## 🛠️ Build and Development

The plugin requires the **.NET 9 SDK** to build.

### Restore Dependencies
```bash
dotnet restore Jellyfin.Plugin.VFQ.sln
```

### Build (Debug Mode)
```bash
dotnet build Jellyfin.Plugin.VFQ.sln -c Debug
```

### Build (Release Mode)
```bash
dotnet build Jellyfin.Plugin.VFQ.sln -c Release
```

### Clean Artifacts
```bash
dotnet clean Jellyfin.Plugin.VFQ.sln
```
