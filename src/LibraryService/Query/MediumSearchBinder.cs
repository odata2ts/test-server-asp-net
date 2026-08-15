using System.Linq.Expressions;
using System.Reflection;
using Library.Catalog;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.OData.UriParser;

namespace LibraryService.Query;

/// <summary>
/// Makes <c>$search</c> actually search, over <see cref="Medium.Title" /> and <see cref="Medium.Keywords" />.
///
/// Without a binder the option is accepted and silently ignored - the request answers 200 with the
/// unfiltered set, which is worse than refusing it, because a client cannot tell the difference. The
/// reference model declares <c>Capabilities.SearchRestrictions</c> with <c>Searchable=true</c> on the
/// Media set, so the option has to mean something here.
/// </summary>
public class MediumSearchBinder : QueryBinder, ISearchBinder
{
    /// <summary>
    /// The one-argument overload, deliberately: <c>Contains(string, StringComparison)</c> has no SQL
    /// translation, so once the store became a real database it would have turned every <c>$search</c>
    /// into a 500. Case-insensitivity is instead expressed by lowering both sides in
    /// <see cref="MatchesTerm" />, which does translate.
    /// </summary>
    private static readonly MethodInfo StringContains =
        typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

    private static readonly MethodInfo StringToLower =
        typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    private static readonly MethodInfo EnumerableAny = typeof(Enumerable)
        .GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(string));

    public Expression BindSearch(SearchClause searchClause, QueryBinderContext context)
    {
        var parameter = context.CurrentParameter;
        var body = Bind(searchClause.Expression, parameter);
        return Expression.Lambda(body, parameter);
    }

    private static Expression Bind(SingleValueNode node, ParameterExpression parameter) =>
        node switch
        {
            SearchTermNode term => MatchesTerm(parameter, term.Text),
            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And } and_ =>
                Expression.AndAlso(Bind(and_.Left, parameter), Bind(and_.Right, parameter)),
            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Or } or_ =>
                Expression.OrElse(Bind(or_.Left, parameter), Bind(or_.Right, parameter)),
            UnaryOperatorNode { OperatorKind: UnaryOperatorKind.Not } not =>
                Expression.Not(Bind(not.Operand, parameter)),
            _ => throw new NotSupportedException($"Unsupported $search expression: {node.GetType().Name}"),
        };

    /// <summary>Matches the term against the title and against any keyword, case-insensitively.</summary>
    private static Expression MatchesTerm(ParameterExpression parameter, string term)
    {
        var medium = Expression.Convert(parameter, typeof(Medium));
        var needle = Expression.Constant(term.ToLowerInvariant(), typeof(string));

        var titleMatches = Expression.Call(
            Lowered(Expression.Property(medium, nameof(Medium.Title))),
            StringContains,
            needle);

        var keyword = Expression.Parameter(typeof(string), "keyword");
        var keywordMatches = Expression.Call(
            EnumerableAny,
            Expression.Property(medium, nameof(Medium.Keywords)),
            Expression.Lambda<Func<string, bool>>(
                Expression.Call(Lowered(keyword), StringContains, needle),
                keyword));

        return Expression.OrElse(titleMatches, keywordMatches);
    }

    private static Expression Lowered(Expression value) => Expression.Call(value, StringToLower);
}
