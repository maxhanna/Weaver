using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Weaver.Services;

// ═══════════════════════════════════════════════════════════════════════════════
//  EDIT STRATEGY ENUM  — single source of truth for "how this edit will happen"
// ═══════════════════════════════════════════════════════════════════════════════

public enum EditStrategy
{
    CreateFile,         // file does not exist — emit full content
    InsertMethod,       // AST insertAfter  — new method/endpoint/function
    ReplaceMethod,      // AST targetType/targetName — rewrite existing method body
    FillClassBody,      // oldString/newString anchored at end of class — add property/field
    DeleteLines,        // oldString/newString where newString is empty
    AnchoredEdit,       // small oldString/newString (default safe fallback)
    HtmlInsertBefore,   // HTML DOM insert before anchor
    HtmlInsertAfter,    // HTML DOM insert after anchor
    HtmlReplace,        // HTML DOM replace anchor
    FullFileRewrite,    // last-resort escalation only — not classified into directly
}

// ═══════════════════════════════════════════════════════════════════════════════
//  EDIT INTENT  — what the resolver/classifier exposes to callers
// ═══════════════════════════════════════════════════════════════════════════════

public enum EditIntentKind
{
    ReplaceSymbol,       // rewrite an existing method/class/property body
    InsertNearSymbol,    // add a new symbol near an existing anchor
    AddProperty,         // append a field/property to a class
    DeleteContent,       // remove lines/blocks
    TargetedEdit,        // small localised change — no reliable symbol
}

public sealed record EditIntent(EditIntentKind Kind, string? Symbol, string? PreferredKind);

// ═══════════════════════════════════════════════════════════════════════════════
//  EDIT PLAN DECISION  — strategy + pre-resolved symbol info
// ═══════════════════════════════════════════════════════════════════════════════

public sealed record EditPlanDecision(
    EditStrategy Strategy,
    string? TargetType,       // "method","class","property" etc
    string? TargetName,       // resolved symbol name
    string? ResolvedOldStr,   // server-resolved via AST — LLM never needs to author this
    string Reason);

// ═══════════════════════════════════════════════════════════════════════════════
//  EDIT STRATEGY RESOLVER  — deterministic state machine
// ═══════════════════════════════════════════════════════════════════════════════

public static class EditStrategyResolver
{
    /// <summary>
    /// Given a file path, its current content, and the resolved edit intent,
    /// returns the best strategy and any pre-resolved symbol text.
    /// This is the ONLY place that decides "how to apply this edit".
    /// </summary>
    public static EditPlanDecision Decide(
        string relPath,
        bool fileExists,
        string fileContent,
        string changeDescription,
        EditIntent intent)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();

        // ── File doesn't exist yet ───────────────────────────────────────────
        if (!fileExists)
            return new EditPlanDecision(EditStrategy.CreateFile, null, null, null,
                "File does not exist yet");

        // ── HTML/template family → delegate to HtmlDomEditor ─────────────────
        if (HtmlDomEditor.IsHtmlDomFile(relPath))
        {
            var htmlStrategy = intent.Kind == EditIntentKind.InsertNearSymbol
                ? EditStrategy.HtmlInsertAfter
                : intent.Kind == EditIntentKind.ReplaceSymbol
                    ? EditStrategy.HtmlReplace
                    : EditStrategy.HtmlInsertBefore;
            return new EditPlanDecision(htmlStrategy, null, intent.Symbol, null,
                $"HTML/template file — using DOM edit strategy {htmlStrategy}");
        }

        // ── Deletion ─────────────────────────────────────────────────────────
        if (intent.Kind == EditIntentKind.DeleteContent)
            return new EditPlanDecision(EditStrategy.DeleteLines, null, null, null,
                "Deletion — oldString/newString with empty newString");

        // ── Whitespace-significant / non-AST languages → always anchored ────
        if (ext is ".css" or ".scss" or ".sass" or ".less"
                or ".json" or ".jsonc"
                or ".yaml" or ".yml"
                or ".xml" or ".svg"
                or ".md" or ".txt")
            return new EditPlanDecision(EditStrategy.AnchoredEdit, null, null, null,
                $"{ext} — anchored text edit (no AST)");

        var (_, supportsFormatC, _) = AgentUtilities.GetLanguageProfile(ext);

        // ── Property/field addition → never FORMAT C class-replace ───────────
        if (intent.Kind == EditIntentKind.AddProperty)
            return new EditPlanDecision(EditStrategy.FillClassBody, "class", intent.Symbol, null,
                "Adding property/field — anchored append, not full-class replace");

        // ── Symbol-level edits — try AST resolution ──────────────────────────
        if (supportsFormatC
            && intent.Kind is EditIntentKind.ReplaceSymbol or EditIntentKind.InsertNearSymbol
            && !string.IsNullOrWhiteSpace(intent.Symbol))
        {
            var (targetType, resolvedOld, _) = TryResolveSymbol(relPath, ext, fileContent, intent);

            if (resolvedOld != null)
            {
                var strategy = intent.Kind == EditIntentKind.ReplaceSymbol
                    ? EditStrategy.ReplaceMethod
                    : EditStrategy.InsertMethod;
                return new EditPlanDecision(strategy, targetType, intent.Symbol, resolvedOld,
                    $"AST-resolved '{intent.Symbol}' as {targetType}");
            }
        }

        // ── Safe fallback ─────────────────────────────────────────────────────
        return new EditPlanDecision(EditStrategy.AnchoredEdit, null, null, null,
            "No reliable AST symbol resolution — using small anchored text edit");
    }

    // ── Symbol resolution ────────────────────────────────────────────────────

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
                return (intent.PreferredKind ?? "method", astOldStr, null);
            return (null, null, astErr ?? "AST did not find symbol");
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
            "class"    => new[] { "class", "method", "property" },
            "property" => new[] { "property", "method", "class" },
            _          => new[] { "method", "constructor", "class", "property" }
        };

        foreach (var kind in order)
        {
            string? text = kind switch
            {
                "method" when root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == symbolName) is { } mtd
                    => (mtd.GetLeadingTrivia().ToFullString() + mtd.ToString())
                        .Replace("\r\n", "\n").Replace("\r", "\n"),
                "constructor" when root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
                    .FirstOrDefault(c => (c.Parent as TypeDeclarationSyntax)?.Identifier.Text == symbolName) is { } ctor
                    => (ctor.GetLeadingTrivia().ToFullString() + ctor.ToString())
                        .Replace("\r\n", "\n").Replace("\r", "\n"),
                "class" when root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault(c => c.Identifier.Text == symbolName) is { } cls
                    => (cls.GetLeadingTrivia().ToFullString() + cls.ToString())
                        .Replace("\r\n", "\n").Replace("\r", "\n"),
                "property" when root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                    .FirstOrDefault(p => p.Identifier.Text == symbolName) is { } prop
                    => (prop.GetLeadingTrivia().ToFullString() + prop.ToString())
                        .Replace("\r\n", "\n").Replace("\r", "\n"),
                _ => null
            };

            if (text != null) return (kind, text, null);
        }

        return (null, null, $"Symbol '{symbolName}' not found via Roslyn");
    }
}
