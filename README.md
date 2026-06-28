# Photoshop Unity Bridge

HTTP REST bridge for Adobe Photoshop COM automation, designed for Unity Editor integration.

[中文版](README_zh.md)

## Overview

This is an ASP.NET Core Minimal API server that wraps Photoshop's COM interface behind simple HTTP endpoints. Unity (or any HTTP client) can query document info, inspect layers, and export PNGs — without embedding COM logic or ExtendScript directly.

## Quick Start

```bash
dotnet run --project src/PhotoshopUnityBridge
```

The server starts on `http://localhost:9876`. Set `PS_BRIDGE_PORT` to change the port.

**Prerequisites:** .NET 9 SDK, Adobe Photoshop (installed and licensed).

## API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/ps/info` | Photoshop version, active document, COM health |
| `GET` | `/ps/document` | Document dimensions, resolution, color mode |
| `GET` | `/ps/layers?fields=name,bounds,text,font_size` | Recursive layer tree with text properties |
| `POST` | `/ps/layers/{index}/export` | Export a single layer as PNG |
| `POST` | `/ps/layers/export-batch` | Batch export multiple layers as PNGs |

### Example: Export a layer from Unity

```csharp
using UnityEngine.Networking;
// ...
var req = new ExportRequest { outputPath = @"C:\Output\Bg.png", scale = 0.5, trim = true };
var json = JsonUtility.ToJson(req);
using var uwr = UnityWebRequest.Post("http://localhost:9876/ps/layers/0/export", json);
uwr.SetRequestHeader("Content-Type", "application/json");
await uwr.SendWebRequest();
```

## Architecture

```
Unity (HTTP client)
        │
        ▼
┌─────────────────────────────┐
│  ASP.NET Core Minimal API   │  Program.cs — endpoint registration, CORS
│  http://localhost:9876      │
├─────────────────────────────┤
│  PhotoshopService           │  Generates ExtendScript, parses results
├─────────────────────────────┤
│  PhotoshopBridge (singleton)│  STA-thread COM isolation
│  ┌───────────────────────┐  │
│  │  STA Thread           │  │  Serial work queue, timeout protection
│  │  BlockingCollection   │  │
│  └──────────┬────────────┘  │
│             ▼               │
│  Photoshop COM (single-inst)│
└─────────────────────────────┘
```

**Key design:** All Photoshop COM calls happen on a single dedicated STA thread. HTTP requests enqueue work items and await asynchronously — the request thread never touches COM directly. This avoids COM marshaling bugs and enables timeout support.

## Layer Export Output

Each exported layer produces a PNG with:
- Layer origin translated to (0,0) — ready for layout in Unity
- Optional scale (e.g. `0.5` for 50%)
- Optional transparent pixel trimming
- Transparent background (`DocumentFill.TRANSPARENT`)

## License

MIT
