using System.Collections;
using System.Text.Json;
using LibraryService.Data;
using Microsoft.EntityFrameworkCore.Metadata;

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

    /// <summary>
    /// Re-points every navigation the payload <em>bound</em> at the entity it names - through the whole
    /// graph that arrived, not only its root. The counterpart of <see cref="Read{TKey}" /> for a create.
    ///
    /// A binding and a deep insert occupy the same property, and the deserializer builds an instance for
    /// either: for a binding, a stub carrying nothing but the key. <c>Add</c> then tracks the graph it is
    /// handed as <c>Added</c> throughout, stub included, so the request tries to INSERT the very row it
    /// was supposed to link and dies on its primary key. Over the in-memory store this went unnoticed -
    /// there was no insert, only a reference being assigned.
    ///
    /// Which of the two arrived is a question about the payload, never about the store: only the body
    /// tells a stub apart from an entity that is genuinely new and brings its own key. So the JSON is
    /// walked alongside the graph and each bound navigation is replaced by the stored entity, which is
    /// tracked <c>Unchanged</c> and therefore linked rather than written.
    ///
    /// Returns <c>false</c> if a binding names an entity that does not exist.
    /// </summary>
    public static bool Resolve(LibraryContext db, HttpRequest request, object entity) =>
        ReadBody(request) is not { } root || Resolve(db, entity, root);

    private static bool Resolve(LibraryContext db, object entity, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || db.Model.FindEntityType(entity.GetType()) is not { } type)
        {
            return true;
        }

        foreach (var navigation in type.GetNavigations())
        {
            // An owned type is a navigation to EF and a complex property to OData. It has no identity of
            // its own, so there is nothing it could be bound to.
            if (navigation.TargetEntityType.IsOwned()
                || navigation.PropertyInfo is not { } property
                || property.GetValue(entity) is not { } value)
            {
                continue;
            }

            if (Binds(json, navigation.Name))
            {
                if (Stored(db, navigation.TargetEntityType, value) is not { } target)
                {
                    return false;
                }

                property.SetValue(entity, target);
                continue;
            }

            // Not bound, but present in the payload: a deep insert, whose nested entities may bind in
            // their turn - `Copies` carrying a `Location@odata.bind` is exactly that case.
            if (!json.TryGetProperty(navigation.Name, out var nested))
            {
                continue;
            }

            if (!navigation.IsCollection)
            {
                if (!Resolve(db, value, nested))
                {
                    return false;
                }

                continue;
            }

            if (nested.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            // Paired by position: the deserializer fills the collection in the order the array declares.
            foreach (var (item, element) in ((IEnumerable)value).Cast<object>().Zip(nested.EnumerateArray()))
            {
                if (!Resolve(db, item, element))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether the payload binds <paramref name="navigation" /> instead of nesting an entity.</summary>
    private static bool Binds(JsonElement json, string navigation) =>
        (json.TryGetProperty($"{navigation}@odata.bind", out var bind) && bind.ValueKind == JsonValueKind.String)
        || (json.TryGetProperty(navigation, out var nested)
            && nested.ValueKind == JsonValueKind.Object
            && (nested.TryGetProperty("@id", out _) || nested.TryGetProperty("@odata.id", out _)));

    /// <summary>
    /// The stored entity a stub stands for. Its key is read through EF's own metadata rather than parsed
    /// out of the entity id, so a composite key - <c>Copies(MediumId=…,InventoryNumber=…)</c> - needs
    /// nothing extra here.
    /// </summary>
    private static object? Stored(LibraryContext db, IEntityType type, object stub)
    {
        if (type.FindPrimaryKey() is not { } key)
        {
            return null;
        }

        var values = new object?[key.Properties.Count];
        for (var index = 0; index < values.Length; index++)
        {
            // A shadow key part is not on the stub, and no payload can have set it: the contained
            // entities that have one are addressable through their parent only and never bound.
            if (key.Properties[index].PropertyInfo is not { } property)
            {
                return null;
            }

            values[index] = property.GetValue(stub);
        }

        return db.Find(type.ClrType, values);
    }

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
