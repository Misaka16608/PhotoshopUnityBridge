# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build
dotnet build

# Run (default port 9876)
dotnet run --project src/PhotoshopUnityBridge

# Run on a different port
PS_BRIDGE_PORT=9877 dotnet run --project src/PhotoshopUnityBridge
```

## Tests

```bash
# Run all tests
dotnet test

# Run a specific test class
dotnet test --filter "FullyQualifiedName~JsHelpersTests"
```

Tests live in `tests/PhotoshopUnityBridge.Tests/`, using **xUnit** + coverlet. They cover `JsHelpers` (string escaping, field filtering) — the logic that can be tested without a live Photoshop instance. `PhotoshopBridge` and `PhotoshopService` require Photoshop COM and are not unit-testable.

## Architecture

This is an **ASP.NET Core Minimal API** server that wraps Adobe Photoshop COM automation behind HTTP REST endpoints. It exists so Unity (or any HTTP client) can script Photoshop without embedding COM logic directly.

### Threading model — the most critical design decision

Photoshop COM requires **STA apartment** threading. ASP.NET Core's thread pool threads are MTA by default. Calling COM from MTA threads causes RPC marshaling, reentrancy bugs, and freezes.

The solution: **`PhotoshopBridge`** is a singleton that owns a **single dedicated STA thread**. All COM calls are serialized through a `BlockingCollection<StaWorkItem>` — HTTP requests enqueue work items and await their `TaskCompletionSource`. The STA thread dequeues and executes them sequentially.

This means:
- Only one COM operation runs at a time (Photoshop is single-instance anyway).
- The HTTP request thread is never blocked on COM — it awaits asynchronously.
- Timeouts are enforced via `CancellationTokenSource`: if a COM call exceeds its timeout, `_comHealthy` is set to `false` and all subsequent calls fail fast (the server must be restarted to recover).

### Key files

| File | Role |
|------|------|
| `Program.cs` | ASP.NET Core host, endpoint registration, CORS setup |
| `PhotoshopBridge.cs` | STA-thread COM isolation, `ExecuteJavaScriptAsync()`, work queue |
| `PhotoshopService.cs` | ExtendScript generation, JSON parsing, HTTP result formatting |
| `Infrastructure/JsHelpers.cs` | ExtendScript JSON polyfill (ES3 has no `JSON.stringify`) and field filtering |

### REST API

All endpoints live under `/ps`:

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/ps/info` | Photoshop version, active document, COM health |
| `GET` | `/ps/document` | Document dimensions, resolution, color mode |
| `GET` | `/ps/layers?fields=...` | Recursive layer tree with text properties (Action Manager) |
| `POST` | `/ps/layers/{index}/export` | Export a single layer as PNG |
| `POST` | `/ps/layers/export-batch` | Batch export multiple layers as PNGs |

Logging goes to **stderr** (`Console.Error`) so stdout stays clean for HTTP responses.

### ExtendScript execution strategy

`PhotoshopBridge.ExecuteJavaScriptInternal()` tries three `DoJavaScript` parameter modes in sequence, and wraps scripts to suppress dialogs when Photoshop throws error `-2147212704`. Scripts that don't contain `return` or `JSON.stringify` get a `'success'` suffix appended so they don't return empty strings.

### ExtendScript constraints

ExtendScript is **ES3-based**. It lacks `JSON.stringify`, `Array.prototype.map`/`filter`, arrow functions, `let`/`const`, and template literals. The `JsHelpers.JsonPolyfill` provides a minimal `_json()` function. Layer scripts must be authored in ES3-compatible syntax.

### Layer text properties

Text layer metadata (font name, size, color, alignment) is extracted via Photoshop's **Action Manager** (`ActionDescriptor`/`ActionReference`), not the DOM. The DOM `textItem.font`/`textItem.size` are used only as fallbacks. The Action Manager code walks the `textStyle` → `baseParentStyle` chain to find inherited values.
