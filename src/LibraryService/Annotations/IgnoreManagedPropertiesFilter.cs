using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Extensions;

namespace LibraryService.Annotations;

/// <summary>
/// Drops the managed properties out of every <see cref="Delta{T}" /> before an action sees it, so that
/// a <c>PATCH</c> carrying one applies everything else and ignores that value — which is what
/// <see cref="ManagedProperties" /> quotes the specification as requiring.
///
/// Registered once, globally: a controller cannot forget it, and one written next to these is covered
/// without knowing that any of this exists. The alternative would be a line in every <c>Patch</c>
/// action, which is exactly the kind of thing that gets left out of the next one.
///
/// It only reaches the delta-shaped payloads. <c>POST</c> and <c>PUT</c> bind the whole entity, where
/// there is nothing to un-mark and "ignore" means "keep the value the server already has" — a decision
/// only the action can make, since it is the one holding the stored entity. Those call
/// <see cref="ManagedPropertyExtensions" /> instead.
/// </summary>
public sealed class IgnoreManagedPropertiesFilter : IActionFilter
{
    /// <summary>
    /// <c>UpdatableProperties</c> lives on the generic <see cref="Delta{T}" />, not on
    /// <see cref="IDelta" />, and a filter sees its arguments as <see cref="object" />. One cached
    /// lookup per closed delta type is cheaper than making every action generic to avoid it.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> UpdatablePropertiesOf = new();

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var model = context.HttpContext.ODataFeature().Model;
        var operation = OperationOf(context.HttpContext.Request.Method);
        if (model is null || operation is null)
        {
            return;
        }

        foreach (var argument in context.ActionArguments.Values)
        {
            foreach (var delta in DeltasIn(argument))
            {
                if (delta is not ITypedDelta typed || typed.StructuredType is null)
                {
                    continue;
                }

                var notWritable = ManagedProperties.NotWritable(model, typed.StructuredType, operation.Value);

                if (notWritable.Count == 0)
                {
                    continue;
                }

                if (UpdatablePropertiesOf.GetOrAdd(delta.GetType(), type => type.GetProperty("UpdatableProperties"))
                        ?.GetValue(delta) is IList<string> updatable)
                {
                    // "When the list is modified, any modified properties that were removed from the list
                    // are no longer considered to be changed" - so Patch() simply passes them by.
                    foreach (var name in notWritable)
                    {
                        updatable.Remove(name);
                    }
                }
            }
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

    private static WriteOperation? OperationOf(string method) =>
        method switch
        {
            "POST" => WriteOperation.Insert,
            "PATCH" or "PUT" => WriteOperation.Update,
            _ => null,
        };

    /// <summary>
    /// The deltas an argument carries: one for a <see cref="Delta{T}" />, and every entry of a
    /// <see cref="DeltaSet{T}" />, whose members are the same thing again.
    /// </summary>
    private static IEnumerable<object> DeltasIn(object? argument) =>
        argument switch
        {
            IDeltaSet set => set.OfType<object>().SelectMany(DeltasIn),
            IDelta delta => [delta],
            _ => [],
        };
}
