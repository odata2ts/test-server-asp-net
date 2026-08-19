using Microsoft.OData.Edm;

namespace LibraryService.Annotations;

/// <summary>
/// The half of the enforcement a filter cannot do: <c>POST</c> and <c>PUT</c> bind the whole entity, so
/// there is no set of changed properties to narrow — the client's instance simply arrives with values in
/// every slot, whether it wrote them or not.
///
/// "Ignore the value" therefore has to mean something different for each:
///
/// <list type="bullet">
///   <item><c>POST</c> — put the property back to its default, so the server (or the database) supplies
///     it as it would have anyway.</item>
///   <item><c>PUT</c> — put back what is stored, so a replace leaves the property standing. The
///     specification exempts exactly these from the reset a <c>PUT</c> otherwise performs: "Missing
///     non-key, <em>updatable</em> structural properties […] MUST be set to their default values".</item>
/// </list>
///
/// Both work on the incoming instance before it reaches the change tracker, which keeps the controllers
/// to one line and their existing <c>SetValues</c> call unchanged.
/// </summary>
public static class ManagedPropertyExtensions
{
    /// <summary>
    /// Clears the properties a client may not write on insert, so a value it sent is not stored.
    /// </summary>
    public static T IgnoreManagedOnInsert<T>(this T incoming, IEdmModel model)
        where T : class
    {
        foreach (var name in ManagedProperties.NotWritable(model, typeof(T), WriteOperation.Insert))
        {
            if (typeof(T).GetProperty(name) is { CanWrite: true } property)
            {
                property.SetValue(incoming, property.PropertyType.IsValueType
                    ? Activator.CreateInstance(property.PropertyType)
                    : null);
            }
        }

        return incoming;
    }

    /// <summary>
    /// Copies the properties a client may not change onto the incoming instance from
    /// <paramref name="stored" />, so replacing the entity with it leaves them as they were.
    /// </summary>
    public static T IgnoreManagedOnUpdate<T>(this T incoming, T stored, IEdmModel model)
        where T : class
    {
        foreach (var name in ManagedProperties.NotWritable(model, typeof(T), WriteOperation.Update))
        {
            if (typeof(T).GetProperty(name) is { CanWrite: true } property)
            {
                property.SetValue(incoming, property.GetValue(stored));
            }
        }

        return incoming;
    }
}
