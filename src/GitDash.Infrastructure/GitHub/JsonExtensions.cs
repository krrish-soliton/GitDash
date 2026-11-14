using System.Text.Json;

namespace GitDash.Infrastructure.GitHub;

/// <summary>Null-tolerant readers for GitHub's JSON, where almost any object can be null.</summary>
internal static class JsonExtensions
{
    public static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static JsonElement? Prop(this JsonElement? element, string name) => element?.Prop(name);

    public static string Str(this JsonElement? element, string name, string fallback = "") =>
        element.Prop(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? fallback : fallback;

    public static string Str(this JsonElement element, string name, string fallback = "") =>
        ((JsonElement?)element).Str(name, fallback);

    public static string? StrOrNull(this JsonElement? element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static int Int(this JsonElement? element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : 0;

    public static int Int(this JsonElement element, string name) => ((JsonElement?)element).Int(name);

    public static bool Bool(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.True };

    public static DateTimeOffset? Date(this JsonElement? element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.String } v && v.TryGetDateTimeOffset(out var d) ? d : null;

    public static DateTimeOffset? Date(this JsonElement element, string name) => ((JsonElement?)element).Date(name);

    public static IEnumerable<JsonElement> Nodes(this JsonElement? element, string connection)
    {
        if (element.Prop(connection).Prop("nodes") is not { ValueKind: JsonValueKind.Array } nodes) yield break;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind == JsonValueKind.Object) yield return node;
        }
    }

    public static IEnumerable<JsonElement> Nodes(this JsonElement element, string connection) =>
        ((JsonElement?)element).Nodes(connection);

    /// <summary>Objects of a plain JSON array property (not a GraphQL "nodes" connection).</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement element, string name)
    {
        if (element.Prop(name) is not { ValueKind: JsonValueKind.Array } items) yield break;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
    }
}
