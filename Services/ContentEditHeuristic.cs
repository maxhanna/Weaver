using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weaver.Services;

using static Weaver.Services.AgentTextUtilities;
using static Weaver.Services.AgentMethodInventory;

/// <summary>Content-oriented edit guards extracted from the former heuristic bag.</summary>
public sealed class ContentEditHeuristic : IContentEditHeuristic
{
    public string Family => "content";

    public static readonly string[] UnsafeEditMarkers =
    {
        "…(truncated)", "â€¦(truncated)", "...(truncated)"
    };

    internal static readonly HashSet<string> ControlFlowKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "for", "while", "switch", "catch", "using", "lock", "foreach"
    };

    internal static readonly Regex[] PlaceholderPatterns = new[]
    {
        new Regex(@"\bMyMethod\b", RegexOptions.Compiled),
        new Regex(@"\bmyNewMethod\b", RegexOptions.Compiled),
        new Regex(@"\bMyNewMethod\b", RegexOptions.Compiled),
        new Regex(@"\bSomeMethod\b", RegexOptions.Compiled),
        new Regex(@"\bDoSomething\b", RegexOptions.Compiled),
        new Regex(@"\bNewMethod\b", RegexOptions.Compiled),
        new Regex(@"\bPlaceholderMethod\b", RegexOptions.Compiled),
        new Regex(@"\bTestMethod\b", RegexOptions.Compiled),
        new Regex(@"\bMyProperty\b", RegexOptions.Compiled),
        new Regex(@"\bSomeProperty\b", RegexOptions.Compiled),
    };

    public string? DetectHallucinatedProperties(string oldStr, string newStr, string fileContent, string relPath,
        string? relatedFileContent = null)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        // HTML is supported too: the guard scans property accesses inside {{ ... }} template
        // interpolations, so a hallucinated binding like `s.departure.ested` (typo of the real
        // `s.departure.estimated` present elsewhere in the file) is caught the same way a
        // hallucinated `.plularName` in TS is — the exact failure mode that produced a garbage
        // globe.component.html edit in a real run.
        if (ext is not (".ts" or ".tsx" or ".js" or ".jsx" or ".cs" or ".vb" or ".html" or ".htm")) return null;
        // HTML: scan property accesses inside interpolation regions ({{ ... }}), [property]
        // binding VALUES, (event) handler BODIES, and structural-directive EXPRESSIONS
        // (*ngIf="…", *ngFor="…", *ngSwitchCase="…") — so ngClass/ngStyle, click-handler and
        // structural-directive typos get the same treatment as interpolations. The binding
        // TARGET ([class.foo], [style.width], (click)) and the directive NAME (*ngIf) are
        // deliberately excluded: they name a class / style property / event / directive, NOT a
        // property access, so scanning them would false-positive on every `.foo` class token
        // (e.g. a class literally named 'activ' next to a real 'active' property).
        var scanTarget = ext is ".html" or ".htm"
            ? HtmlExpressionScanTarget(newStr)
            : newStr;
        var newProps = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(scanTarget, @"\.([A-Za-z_]\w*)", RegexOptions.Compiled))
        {
            var name = m.Groups[1].Value;
            if (!IsBuiltinIdentifier(name)) newProps.Add(name);
        }
        var oldScan = ext is ".html" or ".htm"
            ? HtmlExpressionScanTarget(oldStr)
            : oldStr;
        var oldProps = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(oldScan, @"\.([A-Za-z_]\w*)", RegexOptions.Compiled))
        {
            oldProps.Add(m.Groups[1].Value);
        }
        var introducedProps = newProps.Except(oldProps).ToList();
        var trulyInvented = new List<string>();
        // ':' must be a split character too — otherwise a property declaration like `items: string[]`
        // yields the token "items:" and the exact-contains check below misses the real word "items",
        // letting the typo heuristic false-positive on it ("did you mean 'items:'?").
        var fileWords = new HashSet<string>(fileContent.Split(new[] { ' ', '\n', '\r', '\t', '.', ';', ',', ':', '(', ')', '[', ']', '{', '}', '<', '>', '=', '!', '?', '|', '&', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries));
        // Resolve bound properties against the component's own file too (Angular: a template
        // references members declared in the sibling `.component.ts`, which the edited HTML
        // never contains). Merging the sibling content into the known-word set means a binding
        // referencing a GENUINELY DECLARED member is exempt (never flagged as a typo of some
        // similar template token — e.g. declaring `vm.item` while the template happens to
        // contain 'items'), while a typo of a real TS member (`vm.opnCard` vs `openCard`)
        // is caught even when the real name appears NOWHERE in the template. Tokens from the
        // related file never become "introduced" — only the edited file's own expressions do.
        if (!string.IsNullOrWhiteSpace(relatedFileContent))
        {
            foreach (var w in relatedFileContent.Split(new[] { ' ', '\n', '\r', '\t', '.', ';', ',', ':', '(', ')', '[', ']', '{', '}', '<', '>', '=', '!', '?', '|', '&', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries))
                fileWords.Add(w);
        }
        foreach (var prop in introducedProps)
        {
            if (Regex.IsMatch(newStr, $@"\b{Regex.Escape(prop)}\s*[:=]")) { continue; }
            if (fileWords.Contains(prop)) { continue; }
            var existingSimilar = fileWords.FirstOrDefault(w =>
                (w.Length > 3) &&
                ((w + "s" == prop) || (w + "es" == prop) ||
                 (prop + "s" == w) || (prop + "es" == w) ||
                 (w + "Array" == prop) || (w + "List" == prop) ||
                 (prop + "Array" == w) || (prop + "List" == w) ||
                 IsPlausibleTypo(prop, w)));
            if (existingSimilar != null)
            {
                trulyInvented.Add($"{prop} (did you mean '{existingSimilar}'?)");
            }
        }
        if (trulyInvented.Count > 0)
        {
            var preview = string.Join(", ", trulyInvented.Take(5));
            return $"HALLUCINATED PROPERTY — newString references [{preview}] which do NOT appear anywhere in {relPath}. " +
                   "The LLM invented properties by modifying the name of existing properties (e.g., pluralizing, or " +
                   "dropping letters from a real name like 'estimated' → 'ested'). " +
                   "Use ONLY properties that already appear in the file. If you need a collection, check if the existing singular property can be used, or explicitly declare the new property in the same edit.";
        }
        return null;
    }

    /// <summary>Collects the EXPRESSION text Angular evaluates in a template: {{ }} interpolation
    /// bodies, [property]="…" binding VALUES, (event)="…" handler BODIES, and structural-directive
    /// EXPRESSIONS (*ngIf="…", *ngFor="…", *ngSwitchCase="…", any *directive="…"). The binding
    /// TARGET ([class.foo], [style.width], (click)) and the directive NAME (*ngIf) are deliberately
    /// excluded — they name a class, style property, event, or directive, not a property access, so
    /// scanning them would false-positive on every `.foo`-style class token. Only the quoted value is
    /// scanned, so a hallucinated property inside ANY evaluated expression gets the same treatment as
    /// one inside {{ }}.</summary>
    private static string HtmlExpressionScanTarget(string s)
    {
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(s, @"\{\{(.*?)\}\}", RegexOptions.Singleline | RegexOptions.Compiled))
        {
            sb.Append('\n').Append(m.Groups[1].Value);
        }
        // The target pattern also admits the banana-in-a-box `[(ngModel)]="…"` shape (name
        // INSIDE the parens), so two-way binding values get scanned too. Only the VALUE is
        // captured — the target is deliberately dropped.
        var bindingRegex = new Regex(
            @"\[(?:\([A-Za-z_][A-Za-z0-9_.:-]*\)|[A-Za-z_][A-Za-z0-9_.:-]*)\]\s*=\s*(?:""([^""]*)""|'([^']*)')",
            RegexOptions.Compiled);
        foreach (Match m in bindingRegex.Matches(s))
        {
            var value = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
            sb.Append('\n').Append(value);
        }
        var eventRegex = new Regex(
            @"\([A-Za-z_][A-Za-z0-9_.:-]*\)\s*=\s*(?:""([^""]*)""|'([^']*)')",
            RegexOptions.Compiled);
        foreach (Match m in eventRegex.Matches(s))
        {
            var value = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
            sb.Append('\n').Append(value);
        }
        // Structural directives (*ngIf, *ngFor, *ngSwitchCase, *ngSwitchDefault, …) carry
        // EVALUATED expressions just like the surfaces above — `*ngIf="vm.isActive"`,
        // `*ngFor="let c of vm.cards"`, `*ngSwitchCase="vm.active"`. The `*`-prefixed name is
        // Angular-specific (Vue uses `v-`), so `*name="…"` can only be a directive; only the
        // quoted expression is captured, never the directive name.
        var structuralRegex = new Regex(
            @"\*[A-Za-z_][A-Za-z0-9_-]*\s*=\s*(?:""([^""]*)""|'([^']*)')",
            RegexOptions.Compiled);
        foreach (Match m in structuralRegex.Matches(s))
        {
            var value = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
            sb.Append('\n').Append(value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A conservative "letter-dropping typo" check: <paramref name="prop"/> is a subsequence of
    /// <paramref name="word"/> (same first character, at most 4 dropped letters, prop &gt;= 4 chars) —
    /// the shape of the real 'ested' vs 'estimated' hallucination. Never flags a prop that is
    /// longer than the known word (a genuine new property like 'deleted' next to 'delete').
    /// </summary>
    public bool IsPlausibleTypo(string prop, string word)
    {
        if (prop.Length < 4 || word.Length < 4) return false;
        if (prop.Length > word.Length || word.Length - prop.Length > 4) return false;
        if (prop[0] != word[0]) return false;
        // The word itself is NOT a typo of itself — dropping zero letters isn't a typo.
        // (Unreachable through DetectHallucinatedProperties, where identical props are
        // filtered earlier; kept honest so the fuzz corpus can assert identity is safe.)
        if (string.Equals(prop, word, StringComparison.Ordinal)) return false;
        var wi = 0;
        foreach (var c in prop)
        {
            wi = word.IndexOf(c, wi);
            if (wi < 0) return false;
            wi++;
        }
        return true;
    }


    /// <summary>Python block-header keywords that form a `word:` line — never object/dict
    /// keys. Without this, a FORMAT C insert of a method containing `else:`/`elif:`/`if:`
    /// lines trips the duplicate-property guard ("newString contains 2 occurrences of
    /// property 'else'"), killing otherwise-good Python edits.</summary>
    private static readonly HashSet<string> PythonBlockKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elif", "else", "for", "while", "with", "try", "except", "finally",
        "def", "class", "match", "case", "lambda", "yield", "async", "await"
    };

    /// <summary>
    /// Classifies a Python source block's declaration kind. The focused-replacement path uses
    /// this to reject a SCOPE MISMATCH deterministically: when the AST-resolved oldString is a
    /// whole <c>class</c> but the LLM returns a bare <c>def</c> method (the benchmark-4
    /// "replaced the class with a method → IndentationError" failure), applying it is guaranteed
    /// to break the file — so we reject BEFORE applying instead of burning verify rounds.
    /// Returns "class", "function", "decorated" (leading @ decorator), or null for fragments.
    /// </summary>
    public string? PythonDeclarationKind(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var trimmed = source.TrimStart();
        if (trimmed.StartsWith("class ", StringComparison.Ordinal) || trimmed.StartsWith("class\t", StringComparison.Ordinal))
            return "class";
        if (trimmed.StartsWith("async ", StringComparison.Ordinal) || trimmed.StartsWith("async\t", StringComparison.Ordinal))
            trimmed = trimmed[5..].TrimStart();
        if (trimmed.StartsWith("def ", StringComparison.Ordinal) || trimmed.StartsWith("def\t", StringComparison.Ordinal))
            return "function";
        if (trimmed.StartsWith('@'))
            return "decorated";
        return null;
    }

    public string? DetectDuplicatePropertyAddition(string oldStr, string newStr, string? relPath = null)
    {
        // Python has no brace-object `key: value` shape this guard understands — its `word:`
        // lines are block headers, not properties. Running the guard on .py just produces
        // false "DUPLICATE PROPERTY ADDITION" rejections on else/elif/def lines.
        if (relPath != null && Path.GetExtension(relPath).Equals(".py", StringComparison.OrdinalIgnoreCase))
            return null;
        string StripStrings(string s)
        {
            s = Regex.Replace(s, @"`[^`]*`", "``", RegexOptions.Singleline);
            s = Regex.Replace(s, @"""[^""]*""", "\"\"", RegexOptions.Singleline);
            s = Regex.Replace(s, @"'[^']*'", "''", RegexOptions.Singleline);
            return s;
        }
        var cleanOld = StripStrings(oldStr);
        var cleanNew = StripStrings(newStr);
        var keyRegex = new Regex(@"^\s*(?:'([^']+)'|""([^""]+)""|(\w+))\s*:", RegexOptions.Multiline);
        var oldCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in keyRegex.Matches(cleanOld))
        {
            // NOTE: never use ?? here — a non-participating group returns "" (not null), so
            // `??` collapses a BARE-key match (group 3) to "" and the key is silently dropped,
            // making the duplicate guard a no-op for the common `name:` shape. Empty-string
            // checks are required for the alternation to select the participating group.
            var g1 = m.Groups[1].Value;
            var g2 = m.Groups[2].Value;
            var key = (g1.Length > 0 ? g1 : g2.Length > 0 ? g2 : m.Groups[3].Value).Trim();
            if (string.IsNullOrEmpty(key)) continue;
            if (PythonBlockKeywords.Contains(key)) continue;
            if (!oldCounts.ContainsKey(key)) oldCounts[key] = 0;
            oldCounts[key]++;
        }
        var newCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in keyRegex.Matches(cleanNew))
        {
            // NOTE: never use ?? here — a non-participating group returns "" (not null), so
            // `??` collapses a BARE-key match (group 3) to "" and the key is silently dropped,
            // making the duplicate guard a no-op for the common `name:` shape. Empty-string
            // checks are required for the alternation to select the participating group.
            var g1 = m.Groups[1].Value;
            var g2 = m.Groups[2].Value;
            var key = (g1.Length > 0 ? g1 : g2.Length > 0 ? g2 : m.Groups[3].Value).Trim();
            if (string.IsNullOrEmpty(key)) continue;
            if (PythonBlockKeywords.Contains(key)) continue;
            if (!newCounts.ContainsKey(key)) newCounts[key] = 0;
            newCounts[key]++;
        }
        foreach (var kvp in newCounts)
        {
            oldCounts.TryGetValue(kvp.Key, out var oldVal);
            if (kvp.Value > oldVal && kvp.Value > 1)
            {
                return $"DUPLICATE PROPERTY ADDITION — newString contains {kvp.Value} occurrences of property '{kvp.Key}' " +
                       $"but oldString only had {oldVal}. You added a duplicate property instead of modifying the existing one. " +
                       "MODIFY the existing property value instead of adding a new one with the same name. Include the ENTIRE existing backtick string in oldString.";
            }
        }
        return null;
    }

    private static readonly Regex GroupingVerbRegex = new(
        @"\b(group|aggregate|bucket|categor|organiz|organis|cluster|partition|consolidat)\w*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Line-start `key: {` / `key: [{` group headers — the shape of a grouped output.
    /// A flat data file (entries on their own lines, `{ name: ... }`) never matches.</summary>
    private static readonly Regex GroupedHeaderRegex = new(
        @"^\s*(?:'[^']*'|""[^""]*""|\w+)\s*:\s*\[?\{",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>A brace-object literal containing a key: pair (no nested braces) — one per data
    /// entry in both the flat and grouped shapes. For a grouped output `key: [{ e1 }, { e2 }],`
    /// the group-array's opening brace pairs with the first entry's closing brace, so every
    /// entry still contributes exactly one match — the count is invariant to the grouping.</summary>
    private static readonly Regex EntryObjectRegex = new(
        @"\{[^{}]*:[^{}]*\}",
        RegexOptions.Compiled);

    /// <summary>
    /// Mirrors <see cref="DetectDuplicatePropertyAddition"/> for the OTHER aggregation
    /// hallucination: grouped output that DROPS entries. The duplicate guard rejects a group
    /// key declared twice (a merge artifact); this guard rejects a grouping whose total entry
    /// count fell below the input's (5 vs 6) — the "confident" merge that keeps every group
    /// key, reads perfectly well, and silently discards a row.
    /// Fires only for an aggregation edit, all four conditions required:
    ///   • the change description names a grouping/aggregation verb (an empty description
    ///     falls back to shape-only so direct callers without one still get the protection),
    ///   • the OLD content is flat (no group headers), the NEW content is grouped
    ///     (line-start `key: [{` headers) — the classic flat→grouped transform,
    ///   • the new string has FEWER object-literal entries than the old.
    /// A delete/remove edit (fewer entries, but not grouped and not described as grouping) and
    /// a regrouping of already-grouped data are deliberately out of scope.
    /// </summary>
    public string? DetectDroppedEntriesInGroupedOutput(string oldStr, string newStr, string? changeDesc = null)
    {
        if (!string.IsNullOrWhiteSpace(changeDesc) && !GroupingVerbRegex.IsMatch(changeDesc)) return null;
        string StripStrings(string s)
        {
            s = Regex.Replace(s, @"`[^`]*`", "``", RegexOptions.Singleline);
            s = Regex.Replace(s, @"""[^""]*""", "\"\"", RegexOptions.Singleline);
            s = Regex.Replace(s, @"'[^']*'", "''", RegexOptions.Singleline);
            return s;
        }
        var cleanOld = StripStrings(oldStr);
        var cleanNew = StripStrings(newStr);
        if (!GroupedHeaderRegex.IsMatch(cleanNew) || GroupedHeaderRegex.IsMatch(cleanOld)) return null;
        var oldEntries = EntryObjectRegex.Matches(cleanOld).Count;
        var newEntries = EntryObjectRegex.Matches(cleanNew).Count;
        if (newEntries < oldEntries)
        {
            return $"GROUPED OUTPUT DROPS ENTRIES — newString contains {newEntries} entries but oldString had {oldEntries}. " +
                   "Grouping/aggregation must preserve EVERY input entry exactly once — do not merge, collapse, or drop rows. " +
                   "Reproduce each input entry in the grouped structure.";
        }
        return null;
    }


    public string? GetUnsafeEditPayloadReason(string oldString, string newString)
    {
        foreach (var marker in UnsafeEditMarkers)
            if (oldString.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                newString.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return $"Edit contains placeholder marker '{marker}'.";
        return null;
    }

    /// <summary>
    /// Detects placeholder/stub implementations that do NOT implement real logic —
    /// e.g. a method body that is only a console.log call, contains a
    /// '// Placeholder implementation' or '// TODO: implement' comment, is an empty
    /// body, or throws NotImplementedException. Used to reject such edits
    /// deterministically instead of letting the LLM verifier score them as acceptable.
    /// </summary>

    /// <summary>
    /// Detects LLM-authored placeholder stubs in replacement code. <paramref name="preExisting"/>
    /// (normally the step's oldString) marks lines that are carried over UNCHANGED from the file —
    /// those lines were authored by the pre-edit code, not by the model, so they must never be
    /// counted as stubs (a plan edit that keeps an idiomatic empty `constructor() { }` while adding
    /// a real method is a perfect example: the constructor line used to trip the empty-body check).
    /// </summary>
    public bool LooksLikePlaceholderStub(string code, string? preExisting = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        // Carried-over lines (present verbatim in the old code) are pre-existing context, not
        // LLM-authored stubs — drop them before any line-based stub analysis. Comment-only
        // lines are carried over by their RAW form (their comment-stripped form is blank).
        var carriedOver = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(preExisting))
        {
            foreach (var l in preExisting.Split('\n'))
            {
                var raw = l.Trim();
                if (raw.Length == 0) continue;
                var t = Regex.Replace(raw, @"//[^\n]*", " ").Trim();
                if (t.Length > 0) carriedOver.Add(t);
                else carriedOver.Add(raw);
            }
        }
        // The raw-text stub regexes below (placeholder comments, NotImplemented throws) run on
        // the WHOLE code string — they must also see only MODEL-AUTHORED lines, so build the
        // carried-stripped view (original lines kept, comments intact) for them.
        static string NewOnly(string src, HashSet<string> carried) =>
            string.Join("\n", src.Split('\n').Where(l =>
            {
                var raw = l.Trim();
                if (raw.Length == 0) return false;
                var t = Regex.Replace(raw, @"//[^\n]*", " ").Trim();
                return !(t.Length > 0 && carried.Contains(t)) && !carried.Contains(raw);
            }));
        var newOnlyCode = NewOnly(code, carriedOver);
        static List<string> NewLines(string src, HashSet<string> carried) =>
            Regex.Replace(src, @"//[^\n]*", " ").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l != "{" && l != "}" && l != "{ }" && !carried.Contains(l))
                .ToList();

        // Explicit placeholder comments
        if (Regex.IsMatch(newOnlyCode,
            @"//\s*(placeholder\s*(implementation|stub)|TODO\s*:?\s*(implement|add|fill\s*in)|stub\s+implementation|will\s+be\s+wired\s+up|not\s+implemented\s+yet|dummy\s+implementation|temporary\s+implementation)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled))
            return true;

        // NotImplementedException / NotSupportedException stubs
        if (Regex.IsMatch(newOnlyCode, @"throw\s+new\s+(NotImplementedException|NotSupportedException)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled))
            return true;

        // Empty method body: `name(...): type { }` or arrow `(...) => { }`.
        // Whole-line anchored and dominance-guarded so legitimate code like
        // `if (x) { }`, `for (;;) { }`, `while (x) { }`, `new Foo() { }` or an object
        // literal containing one empty helper is NOT flagged — only a stub that is
        // essentially an empty declaration gets rejected.
        var meaningfulLines = NewLines(code, carriedOver);
        if (meaningfulLines.Count <= 3)
        {
            // Bare `() => { }` or a NAMED arrow (`const onSave = () => { };`) — an empty
            // arrow body is a stub no matter which side of the `=` it sits on. The trailing
            // `,?;?` admits array members and statement terminators.
            var arrowEmpty = meaningfulLines.Any(l =>
                Regex.IsMatch(l,
                    @"^(?:(?:const|let|var)\s+\w+\s*=\s*)?\([^)]*\)\s*=>\s*\{\s*\}\s*,?;?\s*$",
                    RegexOptions.Compiled));
            var methodEmpty = meaningfulLines.Any(l =>
            {
                var m = Regex.Match(l,
                    @"^(?:(?:public|private|protected|internal|readonly|static|async|export|default|function|def|const|let|var)\s+)*(?<name>\w+)\s*\([^)]*\)\s*(:\s*[^{}\r\n]{0,80})?\s*\{\s*\}\s*,?\s*$",
                    RegexOptions.Compiled);
                // Empty constructors/destructors are idiomatic (field-initializer classes, DI
                // shells) — they are NOT stubs. Only empty NEW methods get flagged.
                return m.Success &&
                       !ControlFlowKeywords.Contains(m.Groups["name"].Value) &&
                       !m.Groups["name"].Value.Equals("constructor", StringComparison.OrdinalIgnoreCase) &&
                       !m.Groups["name"].Value.Equals("destructor", StringComparison.OrdinalIgnoreCase);
            });
            if (arrowEmpty || methodEmpty)
                return true;
        }

        // Single-line console.log stub: `name(...): void { console.log('x'); }`
        if (Regex.IsMatch(Regex.Replace(code, @"//[^\n]*", " ").Trim(),
            @"^\w+\s*\([^)]*\)\s*(:\s*[^{\r\n]{0,60})?\s*\{\s*console\.(log|error|warn|info)\([^;]*\);?\s*\}\s*;?$",
            RegexOptions.Compiled))
            return true;

        // Console.log-only body: a block whose only meaningful statements are console.* calls.
        // Signature lines like `showMenuPanel(): void {` are stripped (they end with `{`),
        // so a body that only logs still gets caught.
        var strippedComments = Regex.Replace(code, @"//[^\n]*", " ");
        var meaningful = strippedComments.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l != "{" && l != "}" && l != "{ }" && !carriedOver.Contains(l))
            .Where(l => !(l.Contains('(') && l.EndsWith("{")) && !l.EndsWith("=>") && !l.EndsWith("=> {"))
            .ToList();
        if (meaningful.Count > 0 && meaningful.All(l =>
            Regex.IsMatch(l, @"^console\.(log|error|warn|info)\([^;]*\);?$", RegexOptions.Compiled)))
            return true;

        return false;
    }

}
