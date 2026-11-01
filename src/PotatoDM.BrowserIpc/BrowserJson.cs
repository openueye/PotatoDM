using System.Text.Json;
using System.Text.Json.Serialization;

namespace PotatoDM.BrowserIpc;

/// <summary>JSON settings shared by the native host, the app and the tests.</summary>
public static class BrowserJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
