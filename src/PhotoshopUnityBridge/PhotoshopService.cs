using System.Text.Json;
using PhotoshopUnityBridge.Infrastructure;

namespace PhotoshopUnityBridge;

/// <summary>
/// High-level Photoshop operations exposed as REST endpoints.
/// Wraps ExtendScript generation and JSON parsing.
/// </summary>
public class PhotoshopService
{
    private readonly PhotoshopBridge _ps;
    private readonly ILogger<PhotoshopService> _logger;

    public PhotoshopService(PhotoshopBridge ps, ILogger<PhotoshopService> logger)
    {
        _ps = ps;
        _logger = logger;
    }

    // ==================================================================
    // Session / Document
    // ==================================================================

    public async Task<IResult> GetSessionInfo()
    {
        try
        {
            var version = await _ps.GetVersionAsync();
            var docName = await _ps.GetActiveDocumentNameAsync();

            return Results.Ok(new
            {
                version,
                hasActiveDocument = docName != null,
                activeDocumentName = docName,
                comHealthy = _ps.IsHealthy,
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    }

    public async Task<IResult> GetDocumentInfo()
    {
        var script = @"
(function() {
    var doc = app.activeDocument;
    if (!doc) return 'ERR|No active document';
    return 'OK|' + doc.name + '|' + doc.width.value + '|' + doc.height.value
        + '|' + doc.resolution + '|' + doc.mode.toString();
})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
                return Results.Json(new { error = raw[4..] }, statusCode: 404);

            var parts = raw.Split('|');
            return Results.Ok(new
            {
                name = parts[1],
                width = double.Parse(parts[2]),
                height = double.Parse(parts[3]),
                resolution = double.Parse(parts[4]),
                mode = parts[5],
            });
        }
        catch (TimeoutException)
        {
            return Results.Json(new { error = "Timeout" }, statusCode: 504);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    }

    // ==================================================================
    // Layers
    // ==================================================================

    public async Task<IResult> GetLayers(string? fields)
    {
        var script = JsHelpers.JsonPolyfill + @"
(function() {
    var doc = app.activeDocument;
    if (!doc) return 'ERR|No active document';

    function collectLayer(layer, idx, parentId) {
        var info = {
            index: idx,
            id: layer.id,
            name: layer.name,
            visible: layer.visible,
            type: 'layer',
        };
        if (parentId !== undefined && parentId !== null)
            info.parentId = parentId;
        else
            info.parentId = null;
        try { info.kind = layer.kind.toString(); } catch(e) { info.kind = 'Unknown'; }
        try {
            var b = layer.bounds;
            info.bounds = { left: b[0].value, top: b[1].value, right: b[2].value, bottom: b[3].value };
            info.width = b[2].value - b[0].value;
            info.height = b[3].value - b[1].value;
        } catch(e) { info.bounds = null; info.width = 0; info.height = 0; }
        try { info.opacity = layer.opacity; } catch(e) { info.opacity = 100; }
        try { info.blendMode = layer.blendMode.toString(); } catch(e) { info.blendMode = ''; }

        // Text layer properties via Action Manager
        try {
            var isText = false;
            try { isText = layer.textItem != null; } catch(e) {}
            if (isText) {
                var ti = layer.textItem;
                info.text = ti.contents;

                try {
                    var ref = new ActionReference();
                    ref.putIdentifier(stringIDToTypeID('layer'), layer.id);
                    var desc = executeActionGet(ref);
                    if (desc.hasKey(stringIDToTypeID('textKey'))) {
                        var td = desc.getObjectValue(stringIDToTypeID('textKey'));
                        if (td.hasKey(stringIDToTypeID('textStyleRange'))) {
                            var sl = td.getList(stringIDToTypeID('textStyleRange'));
                            if (sl.count > 0) {
                                var sr = sl.getObjectValue(0);
                                if (sr.hasKey(stringIDToTypeID('textStyle'))) {
                                    var ts = sr.getObjectValue(stringIDToTypeID('textStyle'));
                                    var sc = ts;
                                    while (sc) {
                                        if (info.font_name === undefined && sc.hasKey(stringIDToTypeID('fontName')))
                                            info.font_name = sc.getString(stringIDToTypeID('fontName'));
                                        if (info.font_size === undefined && sc.hasKey(stringIDToTypeID('impliedFontSize'))) {
                                            try { info.font_size = sc.getDouble(stringIDToTypeID('impliedFontSize')); }
                                            catch(e) { info.font_size = sc.getUnitDoubleValue(stringIDToTypeID('impliedFontSize')).value; }
                                        }
                                        if (info.font_size === undefined && sc.hasKey(stringIDToTypeID('size'))) {
                                            try {
                                                var ud = sc.getUnitDoubleValue(stringIDToTypeID('size'));
                                                info.font_size = ud.value;
                                            } catch(e) {
                                                info.font_size = sc.getDouble(stringIDToTypeID('size'));
                                            }
                                        }
                                        if (info.text_color === undefined && sc.hasKey(stringIDToTypeID('color'))) {
                                            var cd = sc.getObjectValue(stringIDToTypeID('color'));
                                            if (cd && cd.hasKey(stringIDToTypeID('red'))) {
                                                info.text_color = {
                                                    red: Math.round(cd.getDouble(stringIDToTypeID('red'))),
                                                    green: Math.round(cd.getDouble(stringIDToTypeID('green'))),
                                                    blue: Math.round(cd.getDouble(stringIDToTypeID('blue')))
                                                };
                                            }
                                        }
                                        if (info.font_name !== undefined &&
                                            info.font_size !== undefined &&
                                            info.text_color !== undefined)
                                            break;
                                        if (sc.hasKey(stringIDToTypeID('baseParentStyle')))
                                            sc = sc.getObjectValue(stringIDToTypeID('baseParentStyle'));
                                        else
                                            break;
                                    }
                                }
                            }
                        }
                        // Bounding box override
                        try {
                            if (desc.hasKey(stringIDToTypeID('boundingBox'))) {
                                var bb = desc.getObjectValue(stringIDToTypeID('boundingBox'));
                                info.bounds = {
                                    left: bb.getDouble(stringIDToTypeID('left')),
                                    top: bb.getDouble(stringIDToTypeID('top')),
                                    right: bb.getDouble(stringIDToTypeID('right')),
                                    bottom: bb.getDouble(stringIDToTypeID('bottom'))
                                };
                                info.width = info.bounds.right - info.bounds.left;
                                info.height = info.bounds.bottom - info.bounds.top;
                            }
                        } catch(e) {}
                        // Transform scale
                        try {
                            if (td.hasKey(stringIDToTypeID('transform'))) {
                                var tf = td.getObjectValue(stringIDToTypeID('transform'));
                                var xx = tf.getDouble(stringIDToTypeID('xx'));
                                var xy = tf.getDouble(stringIDToTypeID('xy'));
                                var yx = tf.getDouble(stringIDToTypeID('yx'));
                                var yy = tf.getDouble(stringIDToTypeID('yy'));
                                var scaleX = Math.sqrt(xx * xx + xy * xy);
                                var scaleY = Math.sqrt(yx * yx + yy * yy);
                                if (Math.abs(scaleX - 1.0) > 0.001 || Math.abs(scaleY - 1.0) > 0.001) {
                                    info.transform_scale_x = scaleX;
                                    info.transform_scale_y = scaleY;
                                    if (info.font_size !== undefined)
                                        info.font_size_raw = info.font_size / scaleY;
                                }
                            }
                        } catch(e) {}
                    }
                } catch(e) {}

                // DOM fallbacks
                if (info.font_name === undefined) {
                    try { info.font_name = ti.font; } catch(e) {}
                }
                if (info.font_size === undefined) {
                    try { info.font_size = parseFloat(String(ti.size)); } catch(e) {}
                }
                if (info.text_color === undefined) {
                    try {
                        var c = ti.color;
                        if (c && c.rgb) {
                            info.text_color = {
                                red: Math.round(c.rgb.red),
                                green: Math.round(c.rgb.green),
                                blue: Math.round(c.rgb.blue)
                            };
                        }
                    } catch(e) {}
                }
                try { info.alignment = ti.justification.toString(); } catch(e) {}
            }
        } catch(e) {}

        try { info.allLocked = layer.allLocked; } catch(e) { info.allLocked = false; }
        try { info.locked = layer.locked; } catch(e) { info.locked = false; }
        return info;
    }

    function collectAll(container, startIdx, parentId) {
        var result = [];
        var idx = startIdx || 0;
        for (var i = 0; i < container.artLayers.length; i++) {
            result.push(collectLayer(container.artLayers[i], idx, parentId));
            idx++;
        }
        for (var j = 0; j < container.layerSets.length; j++) {
            var ls = container.layerSets[j];
            var group = {
                index: idx,
                id: ls.id,
                name: ls.name,
                visible: ls.visible,
                type: 'group',
                kind: 'LayerSet',
            };
            if (parentId !== undefined && parentId !== null)
                group.parentId = parentId;
            else
                group.parentId = null;
            try { group.opacity = ls.opacity; } catch(e) { group.opacity = 100; }
            try { group.blendMode = ls.blendMode.toString(); } catch(e) { group.blendMode = ''; }
            try {
                var b = ls.bounds;
                group.bounds = { left: b[0].value, top: b[1].value, right: b[2].value, bottom: b[3].value };
                group.width = b[2].value - b[0].value;
                group.height = b[3].value - b[1].value;
            } catch(e) { group.bounds = null; group.width = 0; group.height = 0; }
            idx++;
            var children = collectAll(ls, idx, ls.id);
            group.children = children.layers;
            group.childrenCount = children.layers.length;
            result.push(group);
            idx = children.nextIdx;
        }
        return { layers: result, nextIdx: idx };
    }

    var collected = collectAll(doc, 0, null);
    return 'OK|' + _json({ layers: collected.layers, total_count: collected.layers.length });
})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
                return Results.Json(new { error = raw[4..] }, statusCode: 400);

            if (raw.StartsWith("OK|"))
            {
                var json = raw[3..];

                if (!string.IsNullOrWhiteSpace(fields))
                    json = JsHelpers.FilterLayerFields(json, fields);

                var obj = JsonSerializer.Deserialize<object>(json);
                return Results.Ok(obj);
            }

            return Results.Json(new { error = $"Unexpected result: {raw}" }, statusCode: 500);
        }
        catch (TimeoutException)
        {
            return Results.Json(new { error = "Timeout" }, statusCode: 504);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    }

    // ==================================================================
    // Export
    // ==================================================================

    /// <summary>
    /// Request body for single layer export.
    /// </summary>
    public record ExportRequest(
        string OutputPath,
        double Scale = 1.0,
        bool Trim = false
    );

    /// <summary>
    /// Request body for batch layer export.
    /// </summary>
    public record BatchExportRequest(
        List<BatchExportItem> Exports
    );

    public record BatchExportItem(
        int LayerIndex,
        string OutputPath,
        double Scale = 1.0,
        bool Trim = false
    );

    public async Task<IResult> ExportLayer(int layerIndex, ExportRequest req)
    {
        if (string.IsNullOrEmpty(req.OutputPath))
            return Results.Json(new { error = "outputPath is required" }, statusCode: 400);

        if (req.Scale <= 0 || req.Scale > 1.0)
            return Results.Json(new { error = "scale must be > 0 and <= 1.0" }, statusCode: 400);

        var fullPath = Path.GetFullPath(req.OutputPath);
        try { Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!); }
        catch (Exception ex) { return Results.Json(new { error = $"Cannot create directory: {ex.Message}" }, statusCode: 400); }

        var script = BuildExportScript(layerIndex, fullPath, req.Scale, req.Trim);

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script, timeoutMs: 60_000);
            if (raw.StartsWith("ERR|"))
                return Results.Json(new { error = raw[4..] }, statusCode: 400);

            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
                return Results.Json(new { error = "Export failed — no output file generated" }, statusCode: 500);

            var parts = raw.Split('|');
            return Results.Ok(new
            {
                success = true,
                outputPath = fullPath,
                width = int.TryParse(parts.ElementAtOrDefault(1), out var w) ? w : 0,
                height = int.TryParse(parts.ElementAtOrDefault(2), out var h) ? h : 0,
            });
        }
        catch (TimeoutException)
        {
            return Results.Json(new { error = "Timeout" }, statusCode: 504);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    }

    public async Task<IResult> ExportLayersBatch(BatchExportRequest req)
    {
        if (req.Exports == null || req.Exports.Count == 0)
            return Results.Json(new { error = "exports array is required" }, statusCode: 400);

        var results = new List<object>();
        int succeeded = 0, failed = 0;

        foreach (var item in req.Exports)
        {
            if (item.Scale <= 0 || item.Scale > 1.0)
            {
                results.Add(new { index = item.LayerIndex, success = false, error = "Invalid scale" });
                failed++;
                continue;
            }

            var fullPath = Path.GetFullPath(item.OutputPath);
            try { Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!); }
            catch (Exception ex)
            {
                results.Add(new { index = item.LayerIndex, success = false, error = $"Directory error: {ex.Message}" });
                failed++;
                continue;
            }

            var script = BuildExportScript(item.LayerIndex, fullPath, item.Scale, item.Trim);

            try
            {
                var raw = await _ps.ExecuteJavaScriptAsync(script, timeoutMs: 60_000);
                if (raw.StartsWith("ERR|"))
                {
                    results.Add(new { index = item.LayerIndex, outputPath = fullPath, success = false, error = raw[4..] });
                    failed++;
                }
                else if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
                {
                    results.Add(new { index = item.LayerIndex, outputPath = fullPath, success = false, error = "No output file" });
                    failed++;
                }
                else
                {
                    var parts = raw.Split('|');
                    results.Add(new
                    {
                        index = item.LayerIndex,
                        outputPath = fullPath,
                        success = true,
                        width = int.TryParse(parts.ElementAtOrDefault(1), out var w) ? w : 0,
                        height = int.TryParse(parts.ElementAtOrDefault(2), out var h) ? h : 0,
                    });
                    succeeded++;
                }
            }
            catch (Exception ex)
            {
                results.Add(new { index = item.LayerIndex, outputPath = fullPath, success = false, error = ex.Message });
                failed++;
            }
        }

        return Results.Ok(new { total = req.Exports.Count, succeeded, failed, results });
    }

    // ==================================================================
    // ExtendScript builders
    // ==================================================================

    private static string BuildExportScript(int layerIndex, string outputPath, double scale, bool trim)
    {
        var escapedPath = outputPath.Replace("\\", "\\\\");

        return $@"
(function() {{
    var origDialogs = app.displayDialogs;
    app.displayDialogs = DialogModes.NO;
    var origDoc, origRulerUnits;
    try {{
        origDoc = app.activeDocument;
        if (!origDoc) return 'ERR|No active document';
        origRulerUnits = app.preferences.rulerUnits;
        app.preferences.rulerUnits = Units.PIXELS;

        function findByIndex(container, targetIdx, counter) {{
            if (counter === undefined) counter = {{v:0}};
            for (var i=0;i<container.artLayers.length;i++) {{ if(counter.v===targetIdx) return container.artLayers[i]; counter.v++; }}
            for (var j=0;j<container.layerSets.length;j++) {{ if(counter.v===targetIdx) return container.layerSets[j]; counter.v++; var f=findByIndex(container.layerSets[j],targetIdx,counter); if(f) return f; }}
            return null;
        }}
        var targetLayer = findByIndex(origDoc, {layerIndex});
        if(!targetLayer) return 'ERR|Layer not found at index {layerIndex}';
        if(targetLayer.typename==='LayerSet') return 'ERR|Cannot export a layer group';

        var bounds = targetLayer.bounds;
        var docW=origDoc.width.value, docH=origDoc.height.value;
        var left=Math.max(0,Math.floor(bounds[0].value)), top=Math.max(0,Math.floor(bounds[1].value));
        var right=Math.min(docW,Math.ceil(bounds[2].value)), bottom=Math.min(docH,Math.ceil(bounds[3].value));
        var w=right-left, h=bottom-top;
        if(w<=0||h<=0) return 'ERR|Layer has no renderable pixels';

        var tempDoc=app.documents.add(w,h,origDoc.resolution,'_ps_export',NewDocumentMode.RGB,DocumentFill.TRANSPARENT);
        try{{
            app.activeDocument = origDoc;
            var isBg = false;
            try {{ isBg = targetLayer.isBackgroundLayer; }} catch(e) {{}}
            if (isBg) targetLayer.isBackgroundLayer = false;
            origDoc.activeLayer = targetLayer;
            targetLayer.duplicate(tempDoc, ElementPlacement.PLACEATBEGINNING);
            app.activeDocument = tempDoc;
            var dupLayer = tempDoc.activeLayer;
            var db = dupLayer.bounds;
            dupLayer.translate(-db[0].value, -db[1].value);

            if({scale}>0 && {scale}<1.0){{ var sPct={scale}*100; tempDoc.resizeImage(new UnitValue(sPct,'%'),new UnitValue(sPct,'%'),undefined,ResampleMethod.BICUBICSHARPER); }}
            if({(trim ? "true" : "false")}){{ tempDoc.trim(TrimType.TRANSPARENT,true,true,true,true); }}
            var f=new File('{escapedPath}'); if(f.exists) f.remove();
            var opts=new PNGSaveOptions(); opts.compression=9;
            tempDoc.saveAs(f,opts,true);
            var rw=tempDoc.width.value, rh=tempDoc.height.value;
            tempDoc.close(SaveOptions.DONOTSAVECHANGES);
            return 'OK|'+rw+'|'+rh;
        }}catch(e){{ tempDoc.close(SaveOptions.DONOTSAVECHANGES); throw e; }}
    }}catch(e){{ return 'ERR|'+e.toString(); }}
    finally {{
        try{{ if(origRulerUnits!==undefined) app.preferences.rulerUnits=origRulerUnits; app.displayDialogs=origDialogs;
            if(origDoc) app.activeDocument=origDoc; }}catch(e){{}}
    }}
}})();
";
    }
}
