using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Il2cppConsoleMod.Inspector;

/// <summary>
/// Member / identifier completion for the web console, from the Roslyn semantic model of
/// the code as a continuation of the live session (so session variables are known).
/// Pure compiler work: runs off the Unity main thread.
/// </summary>
public static class ScriptCompletion
{
    private const int MaxItems = 300;

    private static readonly string[] Keywords =
    {
        "var", "new", "null", "true", "false", "typeof", "nameof", "default", "this", "is", "as",
        "if", "else", "for", "foreach", "in", "while", "do", "return", "break", "continue", "switch", "case",
        "try", "catch", "finally", "throw", "using", "static", "public", "private", "class", "struct", "void",
        "int", "float", "double", "bool", "string", "object", "long", "byte", "char",
    };

    public sealed class Item
    {
        public string Label { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Detail { get; set; }
    }

    public sealed class Result
    {
        public int From { get; set; }
        public int To { get; set; }
        public List<Item> Items { get; set; } = new();
    }

    public static Result Complete(CSharpScriptSession session, string code, int position)
    {
        position = Math.Clamp(position, 0, code.Length);
        var start = position;
        while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_'))
            start--;
        var prefix = code.Substring(start, position - start);
        var result = new Result { From = start, To = position };

        // Only complete up to the cursor so trailing text does not confuse the parser.
        var rewritten = HandleRewriter.Rewrite(code.Substring(0, position), out var shifts);
        var mappedStart = HandleRewriter.MapPosition(start, shifts);

        var compilation = session.ContinueWith(rewritten).GetCompilation();
        var tree = compilation.SyntaxTrees.Last();
        var model = compilation.GetSemanticModel(tree);

        IEnumerable<ISymbol> symbols;
        var dot = mappedStart - 1;
        while (dot >= 0 && char.IsWhiteSpace(rewritten[dot]))
            dot--;

        if (dot >= 0 && rewritten[dot] == '.')
        {
            var left = LeftOfDot(tree.GetRoot().FindToken(dot));
            if (left == null)
                return result;
            symbols = MemberSymbols(model, left, mappedStart);
        }
        else
        {
            symbols = model.LookupSymbols(mappedStart);
            result.Items.AddRange(Keywords
                .Where(k => Matches(k, prefix))
                .Select(k => new Item { Label = k, Kind = "keyword" }));
        }

        var items = symbols
            .Where(s => s.CanBeReferencedByName && Matches(s.Name, prefix))
            .GroupBy(s => s.Name)
            .Select(g => ToItem(g.First(), g.Count()))
            .OrderBy(i => Rank(i, prefix))
            .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxItems);

        result.Items.InsertRange(0, items);
        return result;
    }

    private static ExpressionSyntax? LeftOfDot(SyntaxToken token) => token.Parent switch
    {
        MemberAccessExpressionSyntax access => access.Expression,
        QualifiedNameSyntax qualified => qualified.Left,
        MemberBindingExpressionSyntax binding => (binding.Parent?.Parent as ConditionalAccessExpressionSyntax)?.Expression
                                                ?? (binding.Parent as ConditionalAccessExpressionSyntax)?.Expression,
        _ => null,
    };

    private static IEnumerable<ISymbol> MemberSymbols(SemanticModel model, ExpressionSyntax left, int position)
    {
        var symbol = model.GetSymbolInfo(left).Symbol;
        if (symbol is INamespaceSymbol ns)
            return model.LookupNamespacesAndTypes(position, ns);

        // `Type.` lists statics and nested types; `value.` lists instance members and extension methods.
        if (symbol is ITypeSymbol typeSymbol)
            return model.LookupStaticMembers(position, typeSymbol);

        var type = model.GetTypeInfo(left).Type;
        if (type == null || type.TypeKind == TypeKind.Error)
            return Enumerable.Empty<ISymbol>();

        return model.LookupSymbols(position, type, includeReducedExtensionMethods: true)
            .Where(s => !s.IsStatic || s is IMethodSymbol { ReducedFrom: not null })
            .Where(s => s is not INamedTypeSymbol);
    }

    private static bool Matches(string name, string prefix)
    {
        if (prefix.Length == 0)
            return true;
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return true;

        // camel humps
        var p = 0;
        for (var i = 0; i < name.Length && p < prefix.Length; i++)
        {
            if ((i == 0 || char.IsUpper(name[i])) && char.ToLowerInvariant(name[i]) == char.ToLowerInvariant(prefix[p]))
                p++;
        }

        return p == prefix.Length;
    }

    private static int Rank(Item item, string prefix)
    {
        if (prefix.Length > 0 && item.Label.StartsWith(prefix, StringComparison.Ordinal))
            return 0;
        if (prefix.Length > 0 && item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return 1;
        return item.Kind switch
        {
            "local" => 2,
            "prop" or "field" or "method" or "event" or "const" => 3,
            _ => 4,
        };
    }

    private static Item ToItem(ISymbol symbol, int overloads)
    {
        var item = new Item { Label = symbol.Name };
        switch (symbol)
        {
            case IMethodSymbol method:
                item.Kind = "method";
                item.Detail = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                              + (overloads > 1 ? $"  (+{overloads - 1})" : "");
                break;
            case IPropertySymbol property:
                item.Kind = "prop";
                item.Detail = property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                break;
            case IFieldSymbol field:
                // Console variables are fields of earlier submission classes.
                item.Kind = field.ContainingType?.IsScriptClass == true ? "local"
                    : field.IsConst || field.ContainingType?.TypeKind == TypeKind.Enum ? "const" : "field";
                item.Detail = field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                break;
            case IEventSymbol ev:
                item.Kind = "event";
                item.Detail = ev.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                break;
            case ILocalSymbol local:
                item.Kind = "local";
                item.Detail = local.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                break;
            case IParameterSymbol parameter:
                item.Kind = "local";
                item.Detail = parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                break;
            case INamespaceSymbol:
                item.Kind = "ns";
                break;
            case INamedTypeSymbol type:
                item.Kind = type.TypeKind switch
                {
                    TypeKind.Enum => "enum",
                    TypeKind.Interface => "interface",
                    TypeKind.Struct => "struct",
                    TypeKind.Delegate => "delegate",
                    _ => "class",
                };
                item.Detail = type.ContainingNamespace?.IsGlobalNamespace == false ? type.ContainingNamespace.ToDisplayString() : null;
                break;
            default:
                item.Kind = "other";
                break;
        }

        return item;
    }
}
