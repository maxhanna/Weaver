using System.Text.RegularExpressions;
using Weaver.Services;

namespace Weaver;

// ═══════════════════════════════════════════════════════════════════════════════
//  EDIT CLASSIFIER  — single place that maps (step, file, ext) → EditStrategy
//
//  This is the ONLY place change-description regex lives.
//  Delete every duplicate copy in AgentController.cs:
//    isNewMethodInsertion, classIsNewCsMethod, isNewMethodInsert,
//    rawMethodCodeDetect, isClassPropertyFill, changeLowerForFormat, isActualDeletion
// ═══════════════════════════════════════════════════════════════════════════════

public static class EditClassifier
{
    // ── Primary entry point ──────────────────────────────────────────────────

    /// <summary>
    /// Classify a plan step into an EditStrategy based on the file's existence,
    /// extension, and the change description. Call once per step — all downstream
    /// logic (prompt builder, response parser, applier, escalation) switches on
    /// the returned strategy. Never re-derives it from scratch.
    /// </summary>
    public static EditStrategy Classify(PlanStep step, bool fileExists, string ext)
    {
        if (!fileExists) return EditStrategy.CreateFile;

        if (HtmlDomEditor.IsHtmlDomFile(step.File))
            return ClassifyHtml(step.Change ?? "");

        var change = (step.Change ?? "").ToLowerInvariant();
        var (_, supportsFormatC, _) = AgentUtilities.GetLanguageProfile(ext);

        if (IsDeletion(change))           return EditStrategy.DeleteLines;
        if (supportsFormatC && IsNewMethodOrEndpoint(change, step.TargetSymbol))
                                           return EditStrategy.InsertMethod;
        if (ext == ".cs" && IsClassPropertyFill(change))
                                           return EditStrategy.FillClassBody;
        if (supportsFormatC && IsFullMethodRewrite(change, step.TargetSymbol))
                                           return EditStrategy.ReplaceMethod;

        return EditStrategy.AnchoredEdit; // safe default
    }

    /// <summary>
    /// Classify and produce a full <see cref="EditIntent"/> — richer than just strategy,
    /// used by <see cref="EditStrategyResolver.Decide"/> for AST-assisted resolution.
    /// </summary>
    public static EditIntent ClassifyIntent(PlanStep step, string ext)
    {
        var change = (step.Change ?? "").ToLowerInvariant();

        if (IsDeletion(change))
            return new EditIntent(EditIntentKind.DeleteContent, null, null);

        if (IsClassPropertyFill(change))
            return new EditIntent(EditIntentKind.AddProperty, step.TargetSymbol, "property");

        if (IsNewMethodOrEndpoint(change, step.TargetSymbol))
            return new EditIntent(EditIntentKind.InsertNearSymbol, step.TargetSymbol, "method");

        if (IsFullMethodRewrite(change, step.TargetSymbol))
            return new EditIntent(EditIntentKind.ReplaceSymbol, step.TargetSymbol, "method");

        return new EditIntent(EditIntentKind.TargetedEdit, step.TargetSymbol, null);
    }

    // ── HTML subclassification ────────────────────────────────────────────────

    private static EditStrategy ClassifyHtml(string change)
    {
        var lower = change.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"\b(replace|update|modify|change)\b"))
            return EditStrategy.HtmlReplace;
        if (Regex.IsMatch(lower, @"\b(after|below|append|append after)\b"))
            return EditStrategy.HtmlInsertAfter;
        return EditStrategy.HtmlInsertBefore; // safe default for HTML additions
    }

    // ── Change-description predicates ────────────────────────────────────────

    /// <summary>True when the step is removing lines/blocks with nothing replacing them.</summary>
    public static bool IsDeletion(string changeLower) =>
        Regex.IsMatch(changeLower,
            @"^\s*(remove|delete|strip|erase|drop)\b") &&
        !Regex.IsMatch(changeLower,
            @"\b(add|insert|replace|implement|return|and add|then add)\b");

    /// <summary>
    /// True when the step adds a brand-new method, endpoint, function, or handler
    /// that does not yet exist in the file.
    /// </summary>
    public static bool IsNewMethodOrEndpoint(string changeLower, string? targetSymbol = null)
    {
        // Explicit "add new method / create endpoint / implement function" phrasing
        if (Regex.IsMatch(changeLower,
            @"\b(add|create|implement|introduce|define|new)\b.{0,50}\b(method|function|endpoint|handler|action|route|api|async)\b"))
            return true;

        // "add a Get*/Post*/Put*/Delete* method" patterns
        if (Regex.IsMatch(changeLower,
            @"\b(add|create|implement)\b.{0,30}\b(get|post|put|delete|patch)[a-z]+"))
            return true;

        // Target symbol is explicitly named and change says "add" or "create"
        if (!string.IsNullOrWhiteSpace(targetSymbol) &&
            Regex.IsMatch(changeLower, @"\b(add|create|implement|introduce)\b"))
            return true;

        return false;
    }

    /// <summary>
    /// True when the step adds one or more properties or fields to an existing class —
    /// never appropriate for FORMAT C class-replace (data-loss risk).
    /// </summary>
    public static bool IsClassPropertyFill(string changeLower) =>
        Regex.IsMatch(changeLower,
            @"\b(add|append|include|insert)\b.{0,40}\b(property|field|attribute|column|prop)\b") ||
        Regex.IsMatch(changeLower,
            @"\b(new|additional)\b.{0,20}\b(property|field)\b");

    /// <summary>
    /// True when the step rewrites the body of an EXISTING method — appropriate for
    /// FORMAT C targetType/targetName when the symbol is resolvable.
    /// </summary>
    public static bool IsFullMethodRewrite(string changeLower, string? targetSymbol = null)
    {
        if (Regex.IsMatch(changeLower,
            @"\b(rewrite|refactor|overhaul|restructure|rebuild)\b.{0,50}\b(method|function|body|logic|implementation)\b"))
            return true;

        if (Regex.IsMatch(changeLower,
            @"\b(replace|update|modify|change)\b.{0,40}\b(entire|whole|full|complete)\b.{0,30}\b(method|function|body)\b"))
            return true;

        // Named symbol + update/modify phrasing → likely a full-method rewrite
        if (!string.IsNullOrWhiteSpace(targetSymbol) &&
            Regex.IsMatch(changeLower,
                @"\b(update|modify|change|fix|rewrite|refactor)\b") &&
            changeLower.Contains(targetSymbol.ToLowerInvariant()))
            return true;

        return false;
    }
}
