using System.Collections.Concurrent;
using Microsoft.AspNetCore.OData.Edm;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Vocabularies;

namespace LibraryService.Annotations;

/// <summary>The write for which a property's writability is being asked.</summary>
public enum WriteOperation
{
    /// <summary>A <c>POST</c> creating the entity.</summary>
    Insert,

    /// <summary>A <c>PATCH</c> or <c>PUT</c> against one that already exists.</summary>
    Update,
}

/// <summary>
/// Which properties a client may not write, read off the very annotations the service publishes in
/// <c>$metadata</c>. Stating a term and then not honouring it would make this server a bad reference:
/// a client generated from the metadata leaves such a property out of its payload precisely because we
/// said it is managed.
///
/// What "not writable" means is the specification's, not ours
/// ([OData Protocol §11.4.3](https://docs.oasis-open.org/odata/odata/v4.01/os/part1-protocol/odata-v4.01-os-part1-protocol.html#sec_UpdateanEntity)):
///
/// > Key and other properties marked as read-only in metadata (including computed properties) […] can
/// > be omitted from the request. If the request contains a value for one of these properties, the
/// > service MUST ignore that value when applying the update.
///
/// So the value is **dropped, not rejected**. Answering <c>400</c> would be a deviation of its own, and
/// a caller has no way to tell a discarded value from an applied one — which is the whole reason a
/// generated client keeps it out of the payload.
///
/// The terms and what they say about each operation:
///
/// <list type="bullet">
///   <item><c>Core.Computed</c> — generated on insert and update alike, so writable in neither.</item>
///   <item><c>Core.Permissions</c> without <c>Write</c> — read-only, so writable in neither.</item>
///   <item><c>Core.Immutable</c> — "can be provided by the client on insert and remains unchanged on
///     update", hence writable on insert only.</item>
///   <item><c>Core.ComputedDefaultValue</c> — the client may supply one whenever it likes; nothing to
///     enforce.</item>
/// </list>
///
/// Keys are not listed here although the same sentence covers them: they come from the URL, and the
/// controllers already set them from the route.
/// </summary>
internal static class ManagedProperties
{
    private const string Ns = "Org.OData.Core.V1.";
    private const string Computed = Ns + "Computed";
    private const string Immutable = Ns + "Immutable";
    private const string Permissions = Ns + "Permissions";

    /// <summary>
    /// The model is built once at startup and never changes, so the answer per (type, operation) is
    /// stable — and this sits in the path of every write request.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type, WriteOperation), IReadOnlySet<string>> Cache = new();

    /// <summary>
    /// The names of the properties a client may not write in <paramref name="operation" />, for the entity
    /// or complex type <paramref name="clrType" /> maps to — inherited ones included. Empty for a type the
    /// model does not know.
    ///
    /// Keyed by the CLR type because that is what every caller holds: an action has its parameter type,
    /// and a <c>Delta</c> reports the type it tracks. The model resolves it to the EDM type once.
    /// </summary>
    public static IReadOnlySet<string> NotWritable(IEdmModel model, Type clrType, WriteOperation operation) =>
        Cache.GetOrAdd(
            (clrType, operation),
            key => model.GetTypeMapper().GetEdmType(model, key.Item1) is IEdmStructuredType type
                ? type.StructuralProperties()
                    .Where(property => !IsWritable(model, property, key.Item2))
                    .Select(property => property.Name)
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal)
        );

    private static bool IsWritable(IEdmModel model, IEdmProperty property, WriteOperation operation)
    {
        if (IsTagSet(model, property, Computed) || !MayWrite(model, property))
        {
            return false;
        }

        // settable while the entity is being created, fixed from then on
        return operation == WriteOperation.Insert || !IsTagSet(model, property, Immutable);
    }

    /// <summary>
    /// A <c>Core.Permissions</c> value which does not include <c>Write</c> takes the property away from
    /// the client. No annotation at all says nothing, and grants everything.
    /// </summary>
    private static bool MayWrite(IEdmModel model, IEdmProperty property)
    {
        var value = model
            .FindVocabularyAnnotations<IEdmVocabularyAnnotation>(property, Permissions)
            .Select(annotation => annotation.Value)
            .OfType<IEdmEnumMemberExpression>()
            .SelectMany(expression => expression.EnumMembers ?? [])
            .Select(member => member.Name)
            .ToList();

        return value.Count == 0 || value.Any(name => name is "Write" or "ReadWrite");
    }

    /// <summary>
    /// Whether a tag term is set to true. A tag holds a boolean, and the term stated without a value
    /// means <c>true</c> — the same reading <c>AnnotationEmitter</c> writes.
    /// </summary>
    private static bool IsTagSet(IEdmModel model, IEdmProperty property, string term) =>
        model
            .FindVocabularyAnnotations<IEdmVocabularyAnnotation>(property, term)
            .Select(annotation => annotation.Value)
            .Any(value => value is not IEdmBooleanConstantExpression boolean || boolean.Value);
}
