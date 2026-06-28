using System.Text.Json.Nodes;
using PhotoshopUnityBridge.Infrastructure;

namespace PhotoshopUnityBridge.Tests;

public class JsHelpersTests
{
    // ==================================================================
    // EscapeJs
    // ==================================================================

    [Theory]
    [InlineData(@"C:\Output\Bg.png", @"C:\\Output\\Bg.png")]
    [InlineData("hello'world", @"hello\'world")]
    [InlineData("line1\nline2", @"line1\nline2")]
    [InlineData("no special chars", "no special chars")]
    public void EscapeJs_Handles_SpecialCharacters(string input, string expected)
    {
        var result = JsHelpers.EscapeJs(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EscapeJs_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", JsHelpers.EscapeJs(""));
    }

    // ==================================================================
    // FilterLayerFields — single layer
    // ==================================================================

    [Fact]
    public void OnlyRequestedFields_ArePreserved()
    {
        var json = @"{
            ""layers"": [{
                ""index"": 0, ""name"": ""test"", ""kind"": ""LayerKind.TEXT"",
                ""bounds"": {""left"":0,""top"":0,""right"":100,""bottom"":50},
                ""text"": ""hello"", ""font_name"": ""Arial"", ""font_size"": 24,
                ""text_color"": {""red"":255,""green"":0,""blue"":0},
                ""type"": ""layer""
            }]
        }";
        var result = JsHelpers.FilterLayerFields(json, "font_size,font_name,text_color");
        var root = JsonNode.Parse(result);
        var layer = root!["layers"]![0]!;

        Assert.NotNull(layer["font_size"]);
        Assert.NotNull(layer["font_name"]);
        Assert.NotNull(layer["text_color"]);
        // Not in field list — should be stripped
        Assert.Null(layer["name"]);
        Assert.Null(layer["text"]);
        Assert.Null(layer["kind"]);
        // Structural field always preserved
        Assert.Equal("layer", layer["type"]!.GetValue<string>());
    }

    [Fact]
    public void CaseInsensitive_FieldMatching()
    {
        var json = @"{
            ""layers"": [{
                ""index"": 0, ""font_size"": 24, ""type"": ""layer""
            }]
        }";
        var result = JsHelpers.FilterLayerFields(json, "Font_Size");
        var root = JsonNode.Parse(result);
        var layer = root!["layers"]![0]!;

        Assert.NotNull(layer["font_size"]);
    }

    // ==================================================================
    // FilterLayerFields — recursive children
    // ==================================================================

    [Fact]
    public void Children_AreRecursivelyFiltered()
    {
        var json = @"{
            ""layers"": [{
                ""index"": 0, ""name"": ""group"", ""type"": ""group"",
                ""children"": [{
                    ""index"": 1, ""name"": ""child"", ""text"": ""hi"",
                    ""font_size"": 12, ""type"": ""layer""
                }],
                ""childrenCount"": 1
            }]
        }";
        var result = JsHelpers.FilterLayerFields(json, "font_size,text");
        var root = JsonNode.Parse(result);
        var group = root!["layers"]![0]!;

        Assert.Null(group["name"]);                      // stripped
        Assert.NotNull(group["children"]);               // structural
        Assert.Equal(1, group["childrenCount"]!.GetValue<int>()); // structural
        var child = group["children"]![0]!;
        Assert.NotNull(child["font_size"]);
        Assert.NotNull(child["text"]);
        Assert.Null(child["name"]);                      // stripped
    }

    [Fact]
    public void DeeplyNested_Children_AreAllFiltered()
    {
        var json = @"{
            ""layers"": [{
                ""index"": 0, ""name"": ""A"", ""type"": ""group"",
                ""children"": [{
                    ""index"": 1, ""name"": ""B"", ""type"": ""group"",
                    ""children"": [{
                        ""index"": 2, ""name"": ""C"", ""text"": ""deep"",
                        ""font_size"": 8, ""type"": ""layer""
                    }],
                    ""childrenCount"": 1
                }],
                ""childrenCount"": 1
            }]
        }";
        var result = JsHelpers.FilterLayerFields(json, "text,font_size");
        var root = JsonNode.Parse(result);
        var a = root!["layers"]![0]!;
        Assert.Null(a["name"]);                             // stripped
        var b = a["children"]![0]!;
        Assert.Null(b["name"]);                             // stripped
        var c = b["children"]![0]!;
        Assert.Equal("deep", c["text"]!.GetValue<string>());
        Assert.Equal(8, c["font_size"]!.GetValue<int>());
    }

    // ==================================================================
    // FilterLayerFields — structural fields
    // ==================================================================

    [Fact]
    public void Type_Children_ChildrenCount_AlwaysPreserved()
    {
        var json = @"{
            ""layers"": [{
                ""index"": 0, ""type"": ""group"",
                ""children"": [{""index"": 1, ""type"": ""layer""}],
                ""childrenCount"": 1
            }]
        }";
        // Requesting zero regular fields
        var result = JsHelpers.FilterLayerFields(json, "nonexistent_field");
        var root = JsonNode.Parse(result);
        var group = root!["layers"]![0]!;

        Assert.Equal("group", group["type"]!.GetValue<string>());
        Assert.NotNull(group["children"]);
        Assert.Equal(1, group["childrenCount"]!.GetValue<int>());
        Assert.Null(group["index"]); // structural? no — index is NOT in the protected set
    }

    // ==================================================================
    // FilterLayerFields — edge cases
    // ==================================================================

    [Fact]
    public void EmptyLayers_Array_ReturnsEmpty()
    {
        var json = @"{""layers"": []}";
        var result = JsHelpers.FilterLayerFields(json, "name");
        var root = JsonNode.Parse(result);
        Assert.Empty(root!["layers"]!.AsArray());
    }

    [Fact]
    public void FieldsParam_IsEmpty_ReturnsSameJson()
    {
        // Per GetLayers guard, empty fields skips filtering.
        // But if called anyway, empty HashSet means everything is stripped except structural.
        var json = @"{
            ""layers"": [{
                ""index"": 0, ""name"": ""test"", ""type"": ""layer""
            }]
        }";
        var result = JsHelpers.FilterLayerFields(json, "");
        var root = JsonNode.Parse(result);
        var layer = root!["layers"]![0]!;

        Assert.Equal("layer", layer["type"]!.GetValue<string>());
        Assert.Null(layer["name"]);
        Assert.Null(layer["index"]);
    }

    [Fact]
    public void Whitespace_InFieldsParam_IsTrimmed()
    {
        var json = @"{
            ""layers"": [{
                ""index"": 0, ""name"": ""test"", ""type"": ""layer""
            }]
        }";
        var result = JsHelpers.FilterLayerFields(json, "  name  ,  index  ");
        var root = JsonNode.Parse(result);
        var layer = root!["layers"]![0]!;

        Assert.NotNull(layer["name"]);
        Assert.NotNull(layer["index"]);
    }
}
