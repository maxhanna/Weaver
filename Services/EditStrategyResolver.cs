using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Weaver.Services;

public enum EditStrategy
{
    FullFileCreate,      // file doesn't exist
    FormatCReplace,      // AST-resolved symbol, LLM supplies newCode only, no oldString
    FormatCInsert,       // AST-resolved anchor symbol, LLM supplies newCode to insert after it
    ClassPropertyFill,   // add properties/fields to existing class body
    OldNewTargeted,      // small anchor (<=5 lines), used when no reliable AST symbol
}

public sealed record EditPlanDecision(
    EditStrategy Strategy,
    string? TargetType,      // "method","class","property" etc (AST-resolvable languages)
    string? TargetName,      // resolved symbol name
    string? ResolvedOldStr,  // pre-resolved by the SERVER via AST — never asked of the LLM
    string Reason);

/// <summary>
/// Deterministic state machine for choosing how a single edit step should be applied.
/// Language-specific AST resolution decides whether a symbol can be targeted directly;
/// the LLM is only ever asked for the OLD anchor text when no reliable symbol resolution
/// exists for the language/edit shape (e.g. whitespace-significant languages, CSS, JSON).
/// </summary>
public static class EditStrategyResolver
{
    public static EditPlanDecision Decide(
        string relPath,
        bool fileExists,
        string fileContent,
        string changeDescription,
        EditIntent intent)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();

        if (!fileExists)
            return new EditPlanDecision(EditStrategy.FullFileCreate, null, null, null, "File does not exist yet");

        if (fileExists && fileContent.Length < 500)
            return new EditPlanDecision(EditStrategy.FullFileCreate, null, null, null,
                $"Small file ({fileContent.Length} chars) — full file replacement");

        // Whitespace-significant / non-AST-friendly languages always use anchored text edits.
        if (AgentUtilities.IsWhitespaceSignificant(relPath) ||
            ext is ".css" or ".scss" or ".less" or ".json" or ".yaml" or ".yml")
        {
            return new EditPlanDecision(EditStrategy.OldNewTargeted, null, null, null,
                $"{ext} is whitespace/structure-significant — using anchored text edit");
        }

        // Property/field addition never goes through FORMAT C class-replace (data loss risk).
        if (intent.Kind == EditIntentKind.AddProperty)
        {
            return new EditPlanDecision(EditStrategy.ClassPropertyFill, "class", intent.Symbol, null,
                "Adding property/field — anchored append, not full-class replace");
        }

        // Try AST resolution for symbol-level edits (replace or insert-after).
        if (intent.Kind is EditIntentKind.ReplaceSymbol or EditIntentKind.InsertNearSymbol
            && !string.IsNullOrWhiteSpace(intent.Symbol))
        {
            var (targetType, resolvedOld, err) = TryResolveSymbol(relPath, ext, fileContent, intent);

            if (resolvedOld != null)
            {
                var strategy = intent.Kind == EditIntentKind.ReplaceSymbol
                    ? EditStrategy.FormatCReplace
                    : EditStrategy.FormatCInsert;

                return new EditPlanDecision(strategy, targetType, intent.Symbol, resolvedOld,
                    $"AST-resolved '{intent.Symbol}' as {targetType} — LLM will not see or author oldString");
            }

            // AST resolution failed (symbol not found, or language unsupported for this shape).
            // Fall through to anchored text edit rather than asking the LLM to hand-copy the whole method.
        }

        return new EditPlanDecision(EditStrategy.OldNewTargeted, null, null, null,
            "No reliable AST symbol resolution — using small anchored text edit");
    }

    /// <summary>
    /// Server-side symbol resolution. For C#, uses Roslyn exclusively (no tree-sitter, no
    /// native DLL dependency). For other AST-friendly languages, uses the existing regex-based
    /// resolver in AgentController.AstResolveEdit's style — kept here as a delegate injected
    /// by the caller to avoid duplicating that logic.
    /// </summary>
    private static (string? targetType, string? oldStr, string? error) TryResolveSymbol(
        string relPath, string ext, string fileContent, EditIntent intent)
    {
        if (ext == ".cs")
            return ResolveCSharpSymbol(fileContent, intent.Symbol!, intent.PreferredKind);

        if (AstCodeEditorService.IsSupportedExtension(ext))
        {
            var (astOldStr, _, astErr) = AstCodeEditorService.FindFunctionSource(
                fileContent, intent.Symbol!, ext);
            if (astOldStr != null)
            {
                var kind = intent.PreferredKind ?? "method";
                return (kind, astOldStr, null);
            }
            return (null, null, astErr ?? "Tree-sitter did not find symbol");
        }

        return (null, null, $"AST not supported for {ext}");
    }

    private static (string? targetType, string? oldStr, string? error) ResolveCSharpSymbol(
        string source, string symbolName, string? preferredKind)
    {
        Microsoft.CodeAnalysis.SyntaxTree tree;
        try { tree = CSharpSyntaxTree.ParseText(source); }
        catch (Exception ex) { return (null, null, $"Parse failed: {ex.Message}"); }

        var root = tree.GetRoot();

        var order = preferredKind switch
        {
            "class" => new[] { "class", "method", "property" },
            "property" => new[] { "property", "method", "class" },
            _ => new[] { "method", "constructor", "class", "property" }
        };

        foreach (var kind in order)
        {
            string? text = kind switch
            {
                "method" when root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == symbolName) is { } mtd
                    => (mtd.GetLeadingTrivia().ToFullString() + mtd.ToString()).Replace("\r\n", "\n").Replace("\r", "\n"),
                "constructor" when root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
                    .FirstOrDefault(c => (c.Parent as TypeDeclarationSyntax)?.Identifier.Text == symbolName) is { } ctor
                    => (ctor.GetLeadingTrivia().ToFullString() + ctor.ToString()).Replace("\r\n", "\n").Replace("\r", "\n"),
                "class" when root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault(c => c.Identifier.Text == symbolName) is { } cls
                    => (cls.GetLeadingTrivia().ToFullString() + cls.ToString()).Replace("\r\n", "\n").Replace("\r", "\n"),
                "property" when root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                    .FirstOrDefault(p => p.Identifier.Text == symbolName) is { } prop
                    => (prop.GetLeadingTrivia().ToFullString() + prop.ToString()).Replace("\r\n", "\n").Replace("\r", "\n"),
                _ => null
            };

            if (text != null)
                return (kind, text, null);
        }

        return (null, null, $"Symbol '{symbolName}' not found via Roslyn");
    }
}

public enum EditIntentKind
{
    ReplaceSymbol,      // rewrite an existing method/class/property body in place
    InsertNearSymbol,   // add a new method near an existing anchor method
    AddProperty,        // append a field/property to a class
    TargetedEdit        // small localized change, no reliable symbol
}

public sealed record EditIntent(EditIntentKind Kind, string? Symbol, string? PreferredKind);