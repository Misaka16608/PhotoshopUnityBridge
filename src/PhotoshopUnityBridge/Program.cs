using PhotoshopUnityBridge;

// =====================================================================
// Photoshop Unity Bridge — HTTP REST server
// =====================================================================
// Architecture:
//   - PhotoshopBridge (singleton) — STA-thread COM isolation
//   - PhotoshopService (scoped) — ExtendScript operations
//   - ASP.NET Core Minimal API — HTTP endpoints
//
// Unity calls http://localhost:9876/ps/* via UnityWebRequest.
// No MCP, no LLM, no token overhead.
// =====================================================================

var builder = WebApplication.CreateBuilder(args);

// Logging to stderr so it doesn't interfere with HTTP responses
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

// CORS: allow Unity Editor from localhost
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Register services
builder.Services.AddSingleton<PhotoshopBridge>();
builder.Services.AddSingleton<PhotoshopService>();

var app = builder.Build();
app.UseCors();

// ==================================================================
// REST Endpoints
// ==================================================================

var ps = app.MapGroup("/ps");

// GET /ps/info
ps.MapGet("/info", async (PhotoshopService svc) =>
    await svc.GetSessionInfo());

// GET /ps/document
ps.MapGet("/document", async (PhotoshopService svc) =>
    await svc.GetDocumentInfo());

// GET /ps/layers?fields=name,kind,bounds,text,font_size,alignment
ps.MapGet("/layers", async (string? fields, PhotoshopService svc) =>
    await svc.GetLayers(fields));

// POST /ps/layers/{index}/export
// Body: { "outputPath": "D:\\...\\Bg.png", "scale": 1.0, "trim": false }
ps.MapPost("/layers/{index:int}/export",
    async (int index, PhotoshopService.ExportRequest req, PhotoshopService svc) =>
        await svc.ExportLayer(index, req));

// POST /ps/layers/export-batch
// Body: { "exports": [{ "layerIndex": 0, "outputPath": "D:\\...\\Bg.png" }, ...] }
ps.MapPost("/layers/export-batch",
    async (PhotoshopService.BatchExportRequest req, PhotoshopService svc) =>
        await svc.ExportLayersBatch(req));

// ==================================================================
// Startup
// ==================================================================

const int defaultPort = 9876;
var portStr = Environment.GetEnvironmentVariable("PS_BRIDGE_PORT");
var port = int.TryParse(portStr, out var p) ? p : defaultPort;

Console.Error.WriteLine($"[photoshop-unity-bridge] Starting on http://localhost:{port}/");
Console.Error.WriteLine($"[photoshop-unity-bridge] Endpoints:");
Console.Error.WriteLine($"  GET  /ps/info");
Console.Error.WriteLine($"  GET  /ps/document");
Console.Error.WriteLine($"  GET  /ps/layers?fields=...");
Console.Error.WriteLine($"  POST /ps/layers/{{index}}/export");
Console.Error.WriteLine($"  POST /ps/layers/export-batch");

try
{
    app.Run($"http://localhost:{port}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[photoshop-unity-bridge] Fatal error: {ex}");
    Environment.Exit(1);
}
