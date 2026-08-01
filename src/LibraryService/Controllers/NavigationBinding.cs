using System.Text.Json;

namespace LibraryService.Controllers;

/// <summary>
/// Reads navigation bindings straight out of the request body.
///
/// A payload may reference an existing entity instead of nesting one, in two notations:
///
/// <code>
/// "Location@odata.bind": "…/Branches(2)"      // OData 4.0
/// "Location": { "@id": "…/Branches(2)" }      // OData 4.01
/// </code>
///
/// Going through <see cref="Microsoft.AspNetCore.OData.Deltas.Delta{T}" /> for these is a trap that
/// corrupts data silently. The deserializer turns both notations into a *partial instance* of the target
/// type carrying only its key, and <c>Delta.Patch</c> then treats that as a value to patch **into the
/// currently linked instance**. Binding a copy to another branch does not re-point the reference - it
/// writes the new key into the branch that was linked before, leaving two entities with the same key
/// behind. The request answers 204 and nothing looks wrong until the next read.
///
/// Reading the raw body sidesteps the mechanism entirely. It needs <c>Request.EnableBuffering()</c>,
/// which the pipeline does for every request, so the body can be read again after model binding consumed
/// it.
/// </summary>
internal static class NavigationBinding
{
    /// <summary>
    /// The key referenced for <paramref name="navigation" />, or <c>null</c> if the body does not bind it.
    /// </summary>
    public static TKey? Read<TKey>(HttpRequest request, string navigation, Func<string, TKey?> parse)
        where TKey : struct =>
        ReadEntityId(request, navigation) is { } entityId ? parse(entityId) : null;

    /// <summary>Whether the body binds the navigation to <c>null</c>, i.e. asks for the link to be cleared.</summary>
    public static bool ClearsLink(HttpRequest request, string navigation)
    {
        if (ReadBody(request) is not { } root)
        {
            return false;
        }

        return (root.TryGetProperty($"{navigation}@odata.bind", out var bind) && bind.ValueKind == JsonValueKind.Null)
            || (root.TryGetProperty(navigation, out var nested) && nested.ValueKind == JsonValueKind.Null);
    }

    private static string? ReadEntityId(HttpRequest request, string navigation)
    {
        if (ReadBody(request) is not { } root)
        {
            return null;
        }

        // 4.0: "Nav@odata.bind": "<url>"
        if (root.TryGetProperty($"{navigation}@odata.bind", out var bind) && bind.ValueKind == JsonValueKind.String)
        {
            return bind.GetString();
        }

        // 4.01: "Nav": { "@id": "<url>" } - the prefixed spelling is accepted too, since a 4.0 client
        // talking to a 4.01 service may send it.
        if (root.TryGetProperty(navigation, out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "@id", "@odata.id" })
            {
                if (nested.TryGetProperty(name, out var id) && id.ValueKind == JsonValueKind.String)
                {
                    return id.GetString();
                }
            }
        }

        return null;
    }

    private static JsonElement? ReadBody(HttpRequest request)
    {
        if (!request.Body.CanSeek)
        {
            return null;
        }

        request.Body.Position = 0;
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    /// <summary>
    /// Pulls the key literal out of an entity id: <c>…/Branches(2)</c> → <c>2</c>. The spec allows an
    /// absolute or a relative URI, so only the last parenthesised segment is examined.
    /// </summary>
    private static string? KeyLiteral(string entityId)
    {
        var start = entityId.LastIndexOf('(');
        var end = entityId.LastIndexOf(')');
        return start >= 0 && end > start ? entityId[(start + 1)..end].Trim('\'') : null;
    }

    public static int? AsInt(string entityId) =>
        KeyLiteral(entityId) is { } literal && int.TryParse(literal, out var value) ? value : null;

    public static Guid? AsGuid(string entityId) =>
        KeyLiteral(entityId) is { } literal && Guid.TryParse(literal, out var value) ? value : null;
}
