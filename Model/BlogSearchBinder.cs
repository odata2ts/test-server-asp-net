
using System.Linq.Expressions;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.OData.UriParser;

namespace TestServer.Model;

/// <summary>
/// Add $search functionality.
/// This is a bit complex, dotnet Expressions are metadata which describes code, and can be compiled
/// into executable code or into other languages (like SQL)
/// </summary>
public class BlogSearchBinder : ISearchBinder
{
    public static readonly Dictionary<Type, Func<ParameterExpression, string, Expression>> _searchFunctions = new[]
    {
        Build<Blog>((b, t) => b.Name.Contains(t)),
        Build<BlogPost>((b, t) => b.Name.Contains(t) || b.Content.Contains(t)),
        Build<Comment>((b, t) => b.Title.Contains(t) || b.Text.Contains(t))
    }.ToDictionary(x => x.Key, x => x.Value);

    private static KeyValuePair<Type, Func<ParameterExpression, string, Expression>> Build<T>(Expression<Func<T, string, bool>> expr)
    {
        Func<ParameterExpression, string, Expression> cutExpr = (val, text) =>
        {
            return new SwapExpression(expr.Parameters[1], Expression.Constant(text))
                .Visit(new SwapTypedParam(val).Visit(expr.Body));
        };

        return KeyValuePair.Create(typeof(T), cutExpr);
    }

    public Expression BindSearch(SearchClause searchClause, QueryBinderContext context)
    {
        var input = Expression.Parameter(context.ElementClrType);
        var result = BindSearch(searchClause.Expression, input);
        return Expression.Lambda(result, input);
    }

    private Expression BindSearch(SingleValueNode expr, ParameterExpression input)
    {
        if (expr is SearchTermNode stn)
        {
            if (!_searchFunctions.TryGetValue(input.Type, out var e))
                throw new NotSupportedException(input.Type.ToString());

            return e(input, stn.Text);
        }

        if (expr is UnaryOperatorNode uon)
        {
            var innerNode = BindSearch(uon.Operand, input);
            if (uon.OperatorKind == UnaryOperatorKind.Not)
                return Expression.Not(innerNode);

            throw new NotSupportedException(uon.OperatorKind.ToString());
        }

        if (expr is BinaryOperatorNode bon)
        {
            var lhs = BindSearch(bon.Left, input);
            var rhs = BindSearch(bon.Right, input);

            if (bon.OperatorKind == BinaryOperatorKind.And)
                return Expression.And(lhs, rhs);

            if (bon.OperatorKind == BinaryOperatorKind.Or)
                return Expression.Or(lhs, rhs);

            throw new NotSupportedException(bon.OperatorKind.ToString());
        }

        throw new NotSupportedException(expr.GetType().ToString());
    }

    private class SwapTypedParam : ExpressionVisitor
    {
        private readonly ParameterExpression _param;

        public SwapTypedParam(ParameterExpression param)
        {
            _param = param;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return _param.Type == node.Type ? _param : node;
        }
    }

    private class SwapExpression : ExpressionVisitor
    {
        private readonly Expression from;
        private readonly Expression to;

        public SwapExpression(ParameterExpression from, Expression to)
        {
            this.from = from;
            this.to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == from ? to : node;
        }
    }
}