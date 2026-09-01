![Premio Logo](assets/premio-logo.png)

# Jellyfin.Plugin.Premio

A **Jellyfin 10.11.11** backend plugin that lets Jellyfin search and stream content
from [Premiumize.me](https://www.premiumize.me) cloud storage using `.strm` files.

[![Build & Publish](https://github.com/jsanderstechnologies/Premio/actions/workflows/build.yml/badge.svg)](https://github.com/jsanderstechnologies/Premio/actions/workflows/build.yml)

---

## Requirements

| Requirement | Version |
|---|---|
| .NET SDK | 9.0 |
| Jellyfin | **10.11.11** |
| Premiumize API key | — |

---

## Project structure

```
Jellyfin.Plugin.Premio/
│
├── Configuration/
│   └── PluginConfiguration.cs   # Serialised plugin settings (XML)
│
├── Models/
│   └── PremiumizeModels.cs      # Premiumize v2 API response DTOs
│
├── Services/
│   ├── PremiumizeClient.cs      # Typed HttpClient for the Premiumize REST API
│   └── StrmFileService.cs       # .strm file creation / management
│
├── Plugin.cs                    # IPlugin entry point (metadata, web pages)
├── ServiceRegistrator.cs        # IPluginServiceRegistrator DI wiring
└── Jellyfin.Plugin.Premio.csproj
```

---

## Configuration

Set the following values in the Jellyfin plugin settings UI (or directly in the XML):

| Setting | Default | Description |
|---|---|---|
| `ApiKey` | *(empty)* | Premiumize API key from https://www.premiumize.me/account |
| `ApiBaseUrl` | `https://www.premiumize.me/api` | Override for reverse-proxy setups |
| `RequestTimeoutSeconds` | `30` | HTTP timeout for API calls |
| `StrmOutputDirectory` | *(empty)* | Absolute directory where `.strm` files are written |
| `OverwriteExistingStrmFiles` | `true` | Whether to update existing `.strm` files on refresh |
| `MaxSearchResults` | `50` | Maximum results returned per search query |

---

## Building

```bash
dotnet restore
dotnet build -c Release
```

The compiled DLL (`Jellyfin.Plugin.Premio.dll`) and its dependencies are placed in
`bin/Release/net8.0/`. Copy the entire output folder to your Jellyfin plugin directory
(e.g. `/var/lib/jellyfin/plugins/Premio_1.0.0.0/`) and restart Jellyfin.

---

## Plugin Catalogue (manifest.json)

[`manifest.json`](manifest.json) is the [Jellyfin plugin repository descriptor](https://jellyfin.org/docs/general/server/plugins/index.html).
Point Jellyfin at the raw GitHub URL to install Premio directly from the catalogue:

```
https://raw.githubusercontent.com/jsanderstechnologies/Premio/master/manifest.json
```

The manifest `imageUrl` references the logo at:

```
https://raw.githubusercontent.com/jsanderstechnologies/Premio/master/assets/premio-logo.png
```

> **Note:** The `checksum` and `sourceUrl` fields in `manifest.json` are automatically
> patched by the CI publish job on every tagged release — do not edit them manually.

---

## CI / CD

The [Build & Publish](.github/workflows/build.yml) GitHub Actions workflow:

| Trigger | Job | What it does |
|---|---|---|
| Push / PR to `master` | **Build** | `dotnet restore` → `dotnet build -c Release` → uploads artifact |
| Tag `vX.Y.Z` | **Build** then **Publish** | Packages a Jellyfin-compatible ZIP, patches `manifest.json` (version, checksum, sourceUrl, timestamp via `jq`), creates a GitHub Release |

To cut a release, push a tag:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

---

## Assets

| File | Description |
|---|---|
| [`assets/premio-logo.png`](assets/premio-logo.png) | Plugin logo — Premiumize body + Torrentio µ-circle head |

---

## Key design decisions

* **Typed `HttpClient`** — `PremiumizeClient` is registered via `AddHttpClient<T>()`,
  which wires `IHttpClientFactory` properly and avoids socket exhaustion.
* **Async-first** — all I/O operations use `async`/`await` with `ConfigureAwait(false)`.
* **`IPluginServiceRegistrator`** — the Jellyfin 10.9+ DI hook means no manual
  host-side code changes are required.
* **`.strm` files** — Jellyfin natively indexes `.strm` files as remote media items,
  so no custom media provider is needed for basic playback.

