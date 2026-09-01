# Jellyfin.Plugin.Premio

A **Jellyfin 10.11.11** backend plugin that lets Jellyfin search and stream content
from [Premiumize.me](https://www.premiumize.me) cloud storage using `.strm` files.

---

## Requirements

| Requirement | Version |
|---|---|
| .NET SDK | 8.0 |
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

## Key design decisions

* **Typed `HttpClient`** — `PremiumizeClient` is registered via `AddHttpClient<T>()`,
  which wires `IHttpClientFactory` properly and avoids socket exhaustion.
* **Async-first** — all I/O operations use `async`/`await` with `ConfigureAwait(false)`.
* **`IPluginServiceRegistrator`** — the Jellyfin 10.9+ DI hook means no manual
  host-side code changes are required.
* **`.strm` files** — Jellyfin natively indexes `.strm` files as remote media items,
  so no custom media provider is needed for basic playback.
