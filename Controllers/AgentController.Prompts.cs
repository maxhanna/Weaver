using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Weaver.Services;

namespace Weaver.Controllers;

partial class AgentController
{
    private static string BuildEditSystemPrompt(string editFormat)
    {
        var intro = "You are a surgical code editor. Output ONLY a JSON object.\n\n";

        var formatSection = editFormat switch
        {
            "format_c_insert" =>
                "You MUST use FORMAT C to add a new method/function (insertAfter with an existing method as anchor):\n" +
                "{\n" +
                "  \"targetType\": \"method\",\n" +
                "  \"targetName\": \"ExistingMethodName\",\n" +
                "  \"insertAfter\": true,\n" +
                "  \"newCode\": [\"    async myNewMethod(param: string): Promise<Result> {\", \"        // body\", \"    }\"]\n" +
                "}\n" +
                "  - targetType MUST be \"method\".\n" +
                "  - targetName MUST be an EXISTING method name in the file (copy it VERBATIM from the file content).\n" +
                "  - insertAfter MUST be true.\n" +
                "  - newCode is the COMPLETE new method including signature and body. Can be a string or array of lines.\n" +
                "  - Do NOT use any other format. Do NOT return alreadyDone. Do NOT use fullFile.\n\n",

            "delete" =>
                "You are DELETING code. Use this format:\n" +
                "{\n" +
                "  \"oldString\": [\"  line to delete\", \"  another line\"],\n" +
                "  \"newString\": []\n" +
                "}\n" +
                "  - oldString = EXACT lines to remove (1-5 lines max). Copy verbatim from the file.\n" +
                "  - newString = empty array [].\n" +
                "  - NEVER include surrounding container lines — delete ONLY what's asked.\n\n",

            "full_file" =>
                "This is a NEW FILE (does not exist yet). Use full-file replacement:\n" +
                "{\n" +
                "  \"fullFile\": [\"...entire file content...\"]\n" +
                "}\n" +
                "  - fullFile MUST contain EVERY line of the new file.\n" +
                "  - Use array format for multi-line content.\n\n",

            "format_c_class_fill" =>
                "You MUST use FORMAT C to fill an EXISTING class body with new property/field declarations:\n" +
                "{\n" +
                "  \"targetType\": \"class\",\n" +
                "  \"targetName\": \"ExistingClassName\",\n" +
                "  \"newCode\": [\"    public string? Token { get; set; }\", \"    public string? Date { get; set; }\"]\n" +
                "}\n" +
                "  - targetType MUST be \"class\".\n" +
                "  - targetName MUST be the EXISTING class name copied VERBATIM from the file (e.g. the class already shown as empty `{ }` in the file content below).\n" +
                "  - newCode MUST contain ONLY the new property/field lines to insert inside the class body — one per line.\n" +
                "  - Do NOT repeat the class declaration or braces in newCode. Do NOT set insertAfter.\n" +
                "  - Do NOT use oldString/newString. Do NOT use fullFile. Do NOT touch any other method in the file.\n\n",

            _ =>
                "Use oldString/newString targeted edit format:\n" +
                "{\n" +
                "  \"oldString\": [\"  line1 EXACTLY as in file\", \"  line2\"],\n" +
                "  \"newString\": [\"  line1\", \"  replacement line2\"]\n" +
                "}\n" +
                "  CRITICAL: Each array element is ONE line of code. NEVER split a line across multiple elements.\n" +
                "  TRAILING WHITESPACE: You MAY omit trailing spaces at the END of a line of code (before the newline). " +
                "But LEADING whitespace (indentation) is REQUIRED.\n" +
                "  For single-line: {\"oldString\": \"line1\\nline2\", \"newString\": \"replacement line 1\\nreplacement line 2\"}\n\n"
        };

        var commonRules =
            "CRITICAL RULES:\n" +
            "1. oldString must exist VERBATIM in the file — copy character-for-character including EVERY leading space and tab (indentation).\n" +
            "2. oldString MUST be 1-5 lines. NEVER include surrounding containers.\n" +
            "3. NEVER use placeholders (... or [...] or /* ... */) in oldString or newString\n" +
            "4. oldString must NOT have blank first/last lines — trim any empty lines\n" +
            "5. Each line's meaningful content should be ≥ 8 characters — lines like `}`, `);`, `{` are too short.\n" +
            "6. Output ONLY the JSON — no markdown, no code fences, no introductory text\n" +
            "7. NO-OP PREVENTION (CRITICAL): oldString and newString MUST be DIFFERENT. " +
                "If the step asks you to ADD or INSERT code, the new code MUST appear in newString but MUST NOT appear in oldString.\n" +
            "8. INDENTATION: newString MUST use the EXACT SAME leading whitespace as oldString for every line.\n" +
            "9. MODIFY the existing, don't ADD new alongside the existing. If you see duplicate functionality in newString, REMOVE the old duplicate part.\n" +
            "10. NEVER INVENT type names or property names. Every type/property you reference MUST exist in the project.\n" +
            "11. SPACING — tokens concatenated without spaces are the #1 cause of bad edits. Verify EVERY token boundary.\n" +
            "12. ATOMIC STEPS: Execute EXACTLY what the CHANGE REQUIRED asks for — no more, no less.\n" +
            "13. If your change introduces a new SQL table, include a CREATE TABLE IF NOT EXISTS statement BEFORE any INSERT/UPDATE.\n" +
            "14. NEVER write `{{ex.Message}}` inside an interpolated string — use `{ex.Message}` with single braces.\n";

        return intro + formatSection + commonRules;
    }

    private static string BuildVerifyEditUserPrompt() =>
            "You are a meticulous code reviewer verifying a single edit step in a larger plan. " +
            "Your job is to decide whether to KEEP the edit (it correctly implements the step " +
            "without breaking existing functionality) or ABANDON it (it broke something, changed " +
            "the wrong thing, introduced syntax errors, deleted guards/caches, or otherwise " +
            "missed the intent of the step).\n\n" +
            "STRICT OUTPUT FORMAT — output ONLY a JSON object, no prose, no markdown fences:\n" +
            "{\"decision\":\"keep\"|\"abandon\", \"reason\":\"one short sentence\", \"score\": 0-100, \"needsExtraStep\": true|false}\n\n" +
            "SCORE GUIDELINES:\n" +
            "  90-100: Perfect — correctly implements the step, no issues\n" +
            "  70-89:  Good — mostly correct, minor issues that could be fixed in a follow-up\n" +
            "  40-69:  Poor — wrong approach or missing key functionality\n" +
            "  0-39:   Broken — signature change, deleted functionality, syntax errors\n\n" +
            "DECISION RULES:\n" +
            " * Return \"keep\" if the edit is structurally sound and implements the step. Score 85+.\n" +
            " * Set \"needsExtraStep\": true if the edit is CORRECT but references a method/property " +
            "that doesn't exist in any file yet and needs to be added in a follow-up step (e.g. the HTML " +
            "adds a button with ng-click=\"vm.foo()\" but vm.foo() doesn't exist in the .js/.ts file). " +
            "IMPORTANT: Do NOT flag built-in DOM/event APIs like $event.preventDefault(), " +
            "$event.stopPropagation(), console.log(), etc. — these are native JavaScript/DOM " +
            "methods that do NOT need component-level implementations.\n" +
            "When needsExtraStep is true, ALWAYS also include the missing method name in parentheses " +
            "in the reason, e.g. 'added button with ng-click calling missing method (vm.clearAll).'\n" +
            " * If needsExtraStep is true AND the edit is otherwise correct, set decision to \"keep\" " +
            "(do NOT abandon — the system will auto-generate the follow-up step).\n" +
            " * Return \"abandon\" if ANY of these are true:\n" +
            "    - The edit breaks the build (syntax errors, missing braces, malformed HTML like `({ {x}}`).\n" +
            "    - The edit is in the WRONG LOCATION (e.g., inserted inside the wrong div/section).\n" +
            "    - The edit uses incorrect variable names or syntax in the NEW code itself (e.g., typos, mismatched braces).\n" +
            "    - The edit deleted cache/state guard lines (e.g. `if (this.X) return ...`, " +
            "      `map.has(...)`, `map.get(...)`, `map.set(...)`).\n" +
            "    - The edit changed an existing method's signature (return type, name, or parameter list).\n" +
            "    - The edit is functionally a no-op (old and new do the same thing).\n" +
            "    - SECTION MISMATCH: For HTML/Angular templates with multiple *ngIf sections " +
            "      (e.g., *ngIf=\"activeDataTab === 'users'\" vs *ngIf=\"activeDataTab === 'general'\"), " +
            "      if the step says 'add X to the general tab' but the oldString comes from the 'users' " +
            "      tab (or any other section), ABANDON with reason 'edited wrong section'. " +
            "      This is critical — do NOT be fooled by sections that have similar structure. " +
            "      Check WHICH *ngIf section the oldString belongs to, not just whether the edit 'looks right'.\n" +
            " * IMPORTANT: Do NOT abandon an edit just because it 'radically changed the method' or " +
            "  'replaced existing logic'. If the step asked for a new feature or significant modification, " +
            "  a rewrite of the method body is EXPECTED and CORRECT. Only abandon if it breaks existing " +
            "  functionality that is UNRELATED to the requested change.\n" +
            " * SEQUENTIAL DEPENDENCIES (CRITICAL): Do NOT abandon an edit just because it references a method or property " +
            "  that doesn't exist in the current file yet. If the PLANNED FUTURE STEPS section indicates that a future " +
            "  step will add the missing method/property, OR if the system has auto-injected stubs, you MUST KEEP the current edit. " +
            "  For HTML files, assume that methods referenced in (click) or (menuClicked) handlers WILL BE or HAVE BEEN added to the .ts file. " +
            "  Do NOT abandon an HTML edit solely because you think the method might not exist in the .ts file.\n" +
            " * INSERTIONS: If the step asks to ADD a new method, property, or block of code, and the newString " +
            "  CONTAINS the entire oldString unchanged (usually at the beginning) followed by the new code, this is an INSERTION. " +
            "  This is the CORRECT behavior. Do NOT abandon it claiming it 'replaced' or 'failed to add' the new method. " +
            "  If the new code is present and the old code is preserved, keep the edit.\n" +
            " * If the step asks to modify specific values inside a method (e.g., change coordinates, update a config), " +
            "  it is acceptable to replace the entire method as long as the requested values are updated correctly " +
            "  and the rest of the method is preserved. Do NOT abandon just because the LLM rewrote the method.\n" +
            " * Be conservative: if you're unsure, return \"keep\" and let the build check catch any issues.\n" +
            " * Do NOT consider style/whitespace issues — those are handled by other passes.\n" +
            " * BLANK LINE SPAM: If the newString has a blank line between nearly every code line " +
            "  (alternating code/blank pattern), ABANDON with reason 'excessive blank lines'. " +
            "  Code should have consecutive lines within a block, with at most one blank line " +
            "  between logical sections.\n" +
            " * DO NOT SUGGEST MOVING CODE: Do not flag issues that require moving code blocks, reordering DOM, or restructuring files. " +
            "  Structural refactors are user decisions. Only flag functional bugs, missing methods, or syntax errors.\n" +
            " * DO NOT SECOND-GUESS STRUCTURE: Do not flag nesting, sibling placement, or container wrapping issues. " +
            "  Do not invent issues just to find something to do. If the requested feature is present and functional, KEEP the edit.\n" +
            " * IGNORE TRIVIAL CASING & NAMING: Do not flag variable casing differences (e.g., 'isSearchingImdb' vs 'isSearchingIMDB') or minor naming inconsistencies. " +
            "  Assume the system handles these. Only flag completely missing functionality or critical syntax errors.\n" +
            " * MISSING METHODS ARE NOT BUGS: If the HTML references a method like showMoreReddit() that doesn't exist yet, DO NOT ABANDON. " +
            "  Set needsExtraStep=true and KEEP the edit. The system will auto-generate the missing method. Only ABANDON if the edit deletes existing code or breaks syntax.\n" +
            " * SYNTAX ERRORS ARE FATAL: If the edit has malformed HTML (e.g., `({ {x}}`), mismatched braces, or incorrect Angular syntax in the NEW code itself, ABANDON immediately. " +
            "  Do not create a repair step for syntax errors; the system will automatically retry the edit with the failure context.\n";

    private static string BuildStepExplorationSystemPrompt() =>
        "You are a senior codebase navigation agent. Before a code change is applied, " +
        "your job is to understand exactly what needs to change, which existing code owns it, " +
        "and the smallest context needed to edit it safely.\n\n" +
        "You are given the original task, the full plan (so you understand what came before " +
        "and after), the specific step, and the files already read.\n\n" +
        "Work like a careful coding agent: inspect concrete files before inferring, follow names " +
        "from imports/call sites/types, and stop reading as soon as the edit is grounded.\n\n" +
        "Output ONLY valid JSON — exactly one of these two forms:\n\n" +
        "NEED MORE CONTEXT:\n" +
        "{\n" +
        "  \"ready\": false,\n" +
        "  \"filesToRead\": [\"relative/path/file.ext\"],\n" +
        "  \"reasoning\": \"I need to see X to understand how Y is wired\"\n" +
        "}\n\n" +
        "READY TO EDIT:\n" +
        "{\n" +
        "  \"ready\": true,\n" +
        "  \"refinedChange\": \"In [MethodName] (around line N): replace [exact old code description] " +
        "with [exact new code description]. [Full explanation of the change]\",\n" +
        "  \"targetSymbol\": \"methodOrFunctionName\",\n" +
        "  \"estimatedLineRange\": \"~150-175\",\n" +
        "  \"confidence\": 90\n" +
        "}\n\n" +
        "RULES:\n" +
        "1. filesToRead: only files DIRECTLY needed for THIS step — no tangential reads\n" +
        "2. Never request a file already listed under 'files already read'\n" +
        "3. Max 3 files per request; prefer exact project-relative paths. If you only know a symbol, " +
        "request the most likely file path from imports, filenames, or existing context; do not ask for broad directories.\n" +
        "4. Search strategy: target file first, then imported definitions, adjacent component/template/style files, " +
        "interface/model definitions, and tests only if they reveal expected behavior. Avoid generated, minified, bin/obj, and package files.\n" +
        "5. refinedChange MUST: name the exact method/function/component, describe the " +
        "exact code block being replaced, describe the replacement code — zero ambiguity\n" +
        "6. targetSymbol: the identifier of the specific method/function/class being changed\n" +
        "7. confidence 0-100: if < 70, request more files rather than guessing\n" +
        "8. If the target file already has enough context (small file, obvious location), " +
        "go ready=true on round 1 with a precise refinedChange\n" +
        "9. If the change involves a component, import, alias, or UI element, " +
        "request the import source files to verify the import path and alias are correct before proceeding\n" +
        "10. Memory discipline: do not ask for files just to be safe. Each requested file must answer a specific question " +
        "needed for THIS edit. If the question is already answered by the context, set ready=true.\n" +
        "11. SPACING in refinedChange: where you describe code snippets inline, verify every token is properly " +
        "separated by a space. 'INTERVAL15 MINUTE' is WRONG — it should be 'INTERVAL 15 MINUTE'. " +
        "Read through your output character-by-character before finalizing." +
        "12. TYPE CHAIN TRACING (CRITICAL): When the target file references a type (e.g., `FileEntry`), " +
        "you MUST read that type's definition file. If that type has properties referencing OTHER " +
        "custom types (e.g., `romMetadata?: RomMetadata`), you MUST read those type definitions too. " +
        "Do NOT assume you know the data structure — VERIFY it by reading the actual class/interface. " +
        "This is especially important when the change involves data that lives in nested type properties. " +
        "Example: if the task is about 'image previews' and the component uses FileEntry, you must read " +
        "FileEntry.ts, discover it has romMetadata?: RomMetadata, then read RomMetadata.ts to see " +
        "screenshotsJson, artworksJson, coverUrl — those are where image URLs actually live.\n" +
        "13. DATA SOURCE VERIFICATION: Before declaring ready=true, state explicitly in refinedChange " +
        "WHERE the data being modified comes from. Example: 'Images come from FileEntry.romMetadata." +
        "screenshotsJson (parsed from JSON string) and romMetadata.coverUrl, NOT from filtering " +
        "FileEntry objects by file type.' If you cannot state the data source, you are NOT ready.\n" +
        "14. SERVICE METHOD SIGNATURES (CRITICAL): If the change involves calling a service method (e.g., `this.myService.doSomething(data)`), you MUST read the service file to verify the exact method name and parameters. If the method accepts an interface/model (e.g., `UserEvent`), you MUST read that interface definition to know the exact properties required. Do NOT guess the method signature or model properties.\n" +
        "15. DEPENDENCY INJECTION SCOPE: If a service is injected into the constructor (e.g., `private userEventService: UserEventService`), you MUST call it using `this.userEventService.methodName()`. Do NOT access it via `this.parentRef?.userEventService` or other component references unless explicitly instructed.\n";

    private static string BuildStepExplorationPrompt(
        PlanStep step,
        string originalPrompt,
        AgentPlan? fullPlan,
        int stepIdx,
        string explorationContext,
        HashSet<string> alreadyRead,
        int round)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## ORIGINAL TASK");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();

        if (fullPlan?.Plan?.Count > 0)
        {
            sb.AppendLine("## FULL PLAN");
            for (var i = 0; i < fullPlan.Plan.Count; i++)
            {
                var p = fullPlan.Plan[i];
                var marker = i == stepIdx ? "→ [CURRENT]"
                           : i < stepIdx ? "✓ [DONE]"
                                          : "  [PENDING]";
                sb.AppendLine($"  {marker} Step {i + 1}: {p.File} — {p.Change}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## STEP TO IMPLEMENT");
        sb.AppendLine($"File:   {step.File}");
        sb.AppendLine($"Change: {step.Change}");
        sb.AppendLine();

        sb.AppendLine("## FILES ALREADY READ");
        if (alreadyRead.Count > 0)
            foreach (var f in alreadyRead) sb.AppendLine($"  - {f}");
        else
            sb.AppendLine("  (none yet)");
        sb.AppendLine();

        sb.AppendLine("## FILE CONTENTS");
        sb.AppendLine(string.IsNullOrWhiteSpace(explorationContext)
            ? "(no files read yet)"
            : explorationContext);

        sb.AppendLine();
        if (round == 0)
        {
            sb.AppendLine("ROUND 1 — you have read the target file.");
            sb.AppendLine("Question: can you precisely describe the edit from this file alone?");
            sb.AppendLine("  YES → ready=true + detailed refinedChange + targetSymbol");
            sb.AppendLine("  NO  → ready=false + list the specific related files needed " +
                          "(services, interfaces, imports, component class, etc.)");
        }
        else
        {
            sb.AppendLine($"ROUND {round + 1} — you have read {alreadyRead.Count} file(s).");
            sb.AppendLine("Do you now have enough context to produce an unambiguous edit description?");
            sb.AppendLine("  YES → ready=true + refinedChange naming the exact method and code change");
            sb.AppendLine("  NO  → ready=false + list remaining files (max 3, must not repeat)");
        }

        return sb.ToString();
    }

    private static string BuildIncrementalStepSystemPrompt() =>
    "You are a senior autonomous coding agent building a code-change plan ONE STEP AT A TIME.\n" +
    "You will be shown the task, the discovered file contents, and the PLAN SO FAR (steps already committed).\n" +
    "Your job on EACH turn is to propose exactly ONE new step — the next atomic action required — " +
    "or declare the plan complete if no further step is needed.\n\n" +
    "Output ONLY valid JSON — no markdown fences, no prose outside the JSON.\n\n" +
    "### DECISION ###\n" +
    "If the plan-so-far, together with the discovery context, already fully satisfies the task:\n" +
    "{\"planComplete\": true, \"completionReason\": \"one sentence: why nothing more is needed\"}\n\n" +
    "If you need to read a file not yet in discovery context before you can safely propose the next step:\n" +
    "{\"planComplete\": false, \"exploreFile\": \"{path/to/FILE_TO_EXPLORE.ext}\", \"thinking\": \"why you need this file\"}\n\n" +
    "Otherwise, propose exactly ONE next step:\n" +
    "{\n" +
    "  \"planComplete\": false,\n" +
    "  \"thinking\": \"1-2 sentences: why this is the correct NEXT step given what's already planned\",\n" +
    "  \"step\": {\n" +
    "    \"file\": \"{path/to/TARGET_FILE}.ext, or a marker: _create_file/_command/_web_search/_web_fetch/_git/_rename_file/_delete_file/_show/_checkpoint\",\n" +
    "    \"change\": \"precise, atomic description including the exact method/function name being changed (e.g., getTimedGreetingMessage, renderCards, constructor)\",\n" +
    "    \"targetSymbol\": \"getTimedGreetingMessage\",\n" +
    "    \"line\": 42,\n" +
    "    \"referenceFiles\": [\"{path/to/REFERENCE_FILE}.ext\"]\n" +
    "  },\n" +
    "  \"justification\": \"why this step must happen NOW relative to steps already committed " +
    "(e.g. 'this DTO property must exist before step 2's endpoint can reference it')\"\n" +
    "}\n\n" +
    "### RULES ###\n" +
    "1. ONE step per turn. Never propose multiple steps or a 'plan' array.\n" +
    "2. The step MUST be atomic: one coherent edit at one location in one file. If the natural next " +
    "   action touches two locations (e.g. add a field AND initialize it in a constructor), propose ONLY " +
    "   the first location now — the second location gets its own turn later.\n" +
    "   EXCEPTION: For small repetitive edits to the SAME file (e.g. removing priority tags from " +
    "   todo/doing/done columns, updating the same CSS pattern in multiple selectors), you MAY use " +
    "   the \"edits\" array to batch multiple oldString/newString pairs into ONE step:\n" +
    "   \"edits\": [\n" +
    "     {\"oldString\": \"line1\", \"newString\": \"line1\", \"line\": 10},\n" +
    "     {\"oldString\": \"line2\", \"newString\": \"line2\", \"line\": 20}\n" +
    "   ]\n" +
    "   Each pair is applied independently to the same file. Do NOT use edits for edits in different files — " +
    "   those still need separate steps.\n" +
    "3. NEVER repeat or restate a step already present in PLAN SO FAR.\n" +
    "4. NEVER propose a step that assumes a method/property/symbol exists unless it is already visible in " +
    "   the discovery context OR was introduced by an earlier committed step. NEVER explore files outside the " +
    "   attached files listed in the task or steering. Only explore to find a symbol definition when the symbol " +
    "   is referenced in an already-attached file.\n" +
    "5. Respect dependency order: DTOs/models before endpoints that use them, backend before frontend, " +
    "   services before UI code that calls them.\n" +
    "6. CREATE TABLE IF NOT EXISTS belongs INSIDE the method body that needs it — never its own step.\n" +
    "7. Prefer FEWER, more complete steps. If the whole task is one coherent edit, propose that one step, " +
    "   then declare planComplete=true on the next turn.\n" +
    "8. If your last proposal was REJECTED (see REJECTED ATTEMPTS), do not repeat the same mistake. " +
    "   Read the discovery context more carefully and fix the symbol references instead of trying to explore " +
    "   unrelated files.\n" +
     "9. Each edit step MUST include the \"targetSymbol\" field with the exact function/method/property/selector name being edited (e.g., \"getTimedGreetingMessage\", \"toolBtn\", \"_timer\").\n" +
    "10. Stop as soon as the task is fully satisfied — never propose steps the user did not ask for.\n" +
    "11. _create_file steps MUST come BEFORE any code-editing steps. If a new file is needed, propose it as the first step. " +
     "Never add a _create_file step after code edits have already been proposed — at that point it is too late.\n" +
    "12. When the task involves modifying an existing UI message or behavior (e.g. 'Instead of just X, do Y'), " +
    "you MUST examine ALL attached files in discovery context to find where that original message or behavior " +
    "originates. Then edit THAT file. Do NOT add new code in a different file than where the original lives.\n" +
    "13. NEVER propose a 'locate', 'find', 'examine', 'understand', 'read', 'explore', 'look at', 'inspect', 'review', 'check', 'see', 'search' step. " +
    "You already have the full file content in the discovery context. Every step MUST make an actual code change " +
    "(add, modify, delete, replace, rename, etc.). If you need to understand code before editing, do it in your thinking, not in a separate step.\n" +
    "14. For .html, .htm, .cshtml, .razor files: the 'change' field MUST be ONLY a short natural-language description " +
    "(e.g. 'Add IMDB section after YouTube results'). Do NOT include any HTML code in the 'change' field.\n";

    private static string BuildIncrementalStepUserPrompt(
        string originalPrompt, string discoveryContext, List<PlanStep> planSoFar,
        string? steeringContext, List<string> rejectionFeedback)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### TASK ###");
        sb.AppendLine(originalPrompt);
        if (!string.IsNullOrWhiteSpace(steeringContext))
        {
            sb.AppendLine();
            sb.AppendLine("### STEERING ###");
            sb.AppendLine(steeringContext);
        }
        sb.AppendLine();
        sb.AppendLine("### DISCOVERY CONTEXT (only reference paths/content shown here) ###");
        sb.AppendLine(BuildPlannerDiscoveryContext(discoveryContext));
        sb.AppendLine();
        sb.AppendLine("### PLAN SO FAR (already committed — do NOT repeat these) ###");
        if (planSoFar.Count == 0)
        {
            sb.AppendLine("(empty — this will be the first step)");
        }
        else
        {
            for (var i = 0; i < planSoFar.Count; i++)
                sb.AppendLine($"  Step {i + 1}: [{planSoFar[i].File}] {planSoFar[i].Change}");
        }
        if (rejectionFeedback.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### REJECTED ATTEMPTS FOR THE NEXT STEP (fix these issues) ###");
            foreach (var r in rejectionFeedback) sb.AppendLine($"  - {r}");
        }
        sb.AppendLine();
        sb.AppendLine("Propose the NEXT step now, or declare the plan complete. Output ONLY JSON.");
        return sb.ToString();
    }

    private static string BuildIncrementalSubPlanSystemPrompt() =>
    "You are a senior software architect building a MULTI-STAGE execution plan ONE STAGE AT A TIME.\n" +
    "Each stage ('sub-plan') is a self-contained deliverable — a concrete file (or small related set) with a " +
    "precise, atomic modification. Stages execute strictly in the order you produce them, and each later stage " +
    "can rely on symbols introduced by earlier ones.\n\n" +
    "Output ONLY valid JSON — no markdown fences, no prose outside the JSON.\n\n" +
    "If the STAGES SO FAR already fully cover everything the task requires:\n" +
    "{\"metaPlanComplete\": true, \"completionReason\": \"why nothing more is needed\"}\n\n" +
    "Otherwise, propose exactly ONE next stage:\n" +
    "{\n" +
    "  \"metaPlanComplete\": false,\n" +
    "  \"thinking\": \"1-2 sentences: why this is the correct NEXT deliverable given what's already staged\",\n" +
    "  \"subPlan\": {\n" +
    "    \"title\": \"Concrete deliverable with exact file path(s)\",\n" +
    "    \"description\": \"Exact files and exact modifications for THIS stage only\",\n" +
    "    \"files\": [\"relative/path.ext\"],\n" +
    "    \"contextNote\": \"Concrete symbol names/schemas THIS stage introduces, for later stages to reference\"\n" +
    "  }\n" +
    "}\n\n" +
    "### RULES ###\n" +
    "1. ONE sub-plan per turn.\n" +
    "2. Order dependencies correctly: data models/DTOs before endpoints, backend before frontend, services before UI.\n" +
    "3. ATOMICITY: table creation lives INSIDE the endpoint method that needs it, never its own stage. " +
    "   A method's signature and body are one stage, not two.\n" +
    "4. NEVER repeat a stage already listed in STAGES SO FAR.\n" +
    "5. If the whole task fits in ONE stage, propose that single stage, then declare metaPlanComplete=true next turn.\n" +
    "6. Only split into multiple stages when the task genuinely spans multiple files/layers — never manufacture " +
    "   stages for a single-file change.\n";

    private static string BuildIncrementalSubPlanUserPrompt(
        string originalPrompt, string discoveryContext, List<MetaPlanSubPlan> subPlansSoFar, List<string> rejectionFeedback)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### TASK ###");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("### DISCOVERY CONTEXT ###");
        var ctx = discoveryContext.Length > 8000 ? discoveryContext[..8000] + "\n...(truncated)" : discoveryContext;
        sb.AppendLine(ctx);
        sb.AppendLine();
        sb.AppendLine("### STAGES SO FAR (already committed, in execution order) ###");
        if (subPlansSoFar.Count == 0) sb.AppendLine("(none yet — this will be the first stage)");
        else for (var i = 0; i < subPlansSoFar.Count; i++)
            sb.AppendLine($"  Stage {i + 1}: {subPlansSoFar[i].Title} — {subPlansSoFar[i].Description}");
        if (rejectionFeedback.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### REJECTED ATTEMPTS (fix these) ###");
            foreach (var r in rejectionFeedback) sb.AppendLine($"  - {r}");
        }
        sb.AppendLine();
        sb.AppendLine("Propose the NEXT stage now, or declare the meta-plan complete. Output ONLY JSON.");
        return sb.ToString();
    }

    private static string BuildSubPlanPrompt(
  string originalPrompt,
  MetaPlanSubPlan subPlan,
  int index, int total,
  string? accumulatedResults = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### ORIGINAL TASK (FOR CONTEXT ONLY - DO NOT PLAN THIS) ###");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine($"### SUB-PLAN {index}/{total}: {subPlan.Title} ###");
        sb.AppendLine("⚠ CRITICAL: You MUST plan ONLY the changes described in this specific sub-plan. Do NOT plan the entire original task. Do NOT plan steps for other sub-plans.");
        sb.AppendLine(subPlan.Description);

        if (!string.IsNullOrWhiteSpace(subPlan.ContextNote))
        {
            sb.AppendLine();
            sb.AppendLine("### CONTEXT FROM PRIOR SUB-PLANS (planned) ###");
            sb.AppendLine(subPlan.ContextNote);
        }

        if (!string.IsNullOrWhiteSpace(accumulatedResults))
        {
            sb.AppendLine();
            sb.AppendLine("### ACTUAL RESULTS FROM PRIOR SUB-PLANS (executed) ###");
            sb.AppendLine("The following changes were ACTUALLY applied to files in prior sub-plans.");
            sb.AppendLine("You MUST use these exact symbol names and file paths in your plan:");
            sb.AppendLine(accumulatedResults);
        }

        sb.AppendLine();
        sb.AppendLine("### CONSTRAINTS ###");
        sb.AppendLine("1. Plan ONLY the changes described in this sub-plan — do NOT add steps for other sub-plans' work");
        sb.AppendLine("2. Use the EXACT symbol names, property names, and file paths from the context above");
        sb.AppendLine("3. If context says a prior sub-plan added 'OS, CPU, RAM, GPU properties to BenchmarkDataDTO',");
        sb.AppendLine("   your INSERT statement MUST reference those exact property names (benchmark.OS, benchmark.CPU, etc.)");
        sb.AppendLine("4. CREATE TABLE IF NOT EXISTS goes INSIDE the method body, before the INSERT — NOT as a separate step");
        sb.AppendLine("5. Do NOT re-describe or re-implement work from prior sub-plans");
        sb.AppendLine("6. Each step must be atomic: one coherent edit at one location in one file");

        return sb.ToString();
    }

    private static string BuildPlanningPrompt() =>
        "You are a senior autonomous coding agent. Plan the complete minimum set of steps needed to satisfy the user's request.\n" +
        "Think in this loop before writing JSON: understand the exact task, identify the owning files, decide what context is missing, then plan only the actionable delta.\n" +
        "Output ONLY valid JSON — no markdown fences, no extra text.\n\n" +
               "### STEP TYPES (the \"file\" field) ###\n" +
        "  \"relative/path.ext\"  — Edit an existing file (must be in discovery context). Do NOT include oldString/newString — they will be resolved at execution time. " +
            "For every edit step, include a \"line\" field with the 1-based line number of the target location " +
            "and a \"targetSymbol\" field with the exact method/function/class/selector name being edited. " +
            "Example: {\"file\": \"src/app.ts\", \"change\": \"description\", \"targetSymbol\": \"methodName\", \"priority\": 1, \"line\": 42}\n" +
        "  \"_explore\"            — Read a file NOT YET in the discovery context for REFERENCE only (no edits). Put the file path in \"change\". Do NOT use _explore for files whose content is already shown in the DISCOVERY CONTEXT section — they have already been read.\n" +
        "  \"_command\"            — Run a terminal command; put the full command in \"change\". SAFETY: only use _command if the task requires terminal operations. NEVER use mkdir/rmdir/del for project files — use _create_file instead.\n" +
        "  \"_create_file\"        — Create a new file: put full file content in \"newString\", leave \"oldString\" empty. If the directory does not exist, the system will create it automatically. Do NOT use mkdir.\n" +
        "  \"_web_search\"         — Search the web; put the query in \"change\"\n" +
        "  \"_web_fetch\"          — Fetch a URL; put the full URL in \"change\"\n" +
        "  \"_git\"                — Git operation (commit/pull/push/branch/revert)\n" +
        "  \"_rename_file\"        — Rename: put \"oldpath → newpath\" in \"change\"\n" +
        "  \"_delete_file\"        — Delete a file path in \"change\"\n" +
        "  \"_show\"               — Display text to the user (use last)\n" +
        "  \"_done\"               — Task is already complete; put reason in \"change\"\n" +
        "  \"_checkpoint\"         — Split large refactor into phases\n\n" +
        "### RULES ###\n" +
        "0. OUTPUT DISCIPLINE (CRITICAL): Do NOT write step-by-step reasoning, analysis, or exploratory prose before the JSON. " +
            "Any internal reasoning belongs ONLY inside the \"thinking\" field (max 1-2 sentences). " +
            "Your response MUST begin with '{' as the very first character — no preamble, no \"Looking at...\", no walkthrough.\n" +
        "1. Only reference files that exist in the discovery context. Files whose content is shown in the DISCOVERY CONTEXT have already been read — do NOT add _explore steps for them.\n" +
        "   If the right file is not in discovery context but its path is listed or strongly implied, use _explore with the exact project-relative path.\n" +
        "   If you are unsure which file owns a symbol, choose the most likely path from filenames/imports and use _explore before planning an edit.\n" +
        "2. Plan the COMPLETE set of steps needed to finish this task in ONE shot — usually 1-4 steps for simple tasks, but up to 10-12 steps for complex features. " +
        "Do NOT artificially limit yourself to 1-2 steps so you can be re-invoked later — under-planning " +
        "causes repeated re-invocations that tend to invent redundant or conflicting follow-up edits. " +
        "If the task is a single coherent code change (e.g. two related assignments in the same block, " +
        "or one method body), output exactly ONE step for it.\n" +
        "3. Tool choice: use _explore for repository source, _web_search/_web_fetch only for external current information, and _command only for terminal work that cannot be represented as an edit step.\n" +
        "4. WEB FIRST: add a _web_search step if you need current API docs or recent data.\n" +
        "5. COMMANDS BEFORE EDITS: if a generated/downloaded file must exist first, add _command BEFORE the edit step and write outputs inside the project.\n" +
        "6. SELF-STOP: emit a single _done step if the code already satisfies the requirement.\n" +
        "   The DISCOVERY CONTEXT section above shows the ACTUAL content of files that were read.\n" +
        "   Check that content BEFORE planning an edit step. If the property, method, or config\n" +
        "   already exists in the file shown in discovery context, do NOT create an edit step.\n" +
        "   Use _done instead.\n" +
        "7. Score precisely:\n" +
        "   90-100: Exact file + precise change description, no uncertainty\n" +
        "   70-89:  Correct file, good description, minor refinement possible\n" +
        "   40-69:  File identified but change is vague or approach is uncertain\n" +
        "   0-39:  Unsure which file or what to change.\n" +
        "   Be decisive. If you have the right file and a clear change, score 85+. Do NOT stay low when the plan is solid.\n" +
        "8. Step sizing: one step may cover one coherent edit in one file or one tightly coupled block. Split when changes touch different files, have different owners, need independent verification, OR target DIFFERENT LOCATIONS within the same file.\n" +
        "   CRITICAL — SAME-FILE MULTI-LOCATION: Even within a single file, if a step requires editing 2+ separate locations (e.g., add a field at the top of a class, initialize it in the constructor, AND add a new method at the bottom), that is MULTIPLE steps. Each location requires a different edit strategy and anchor, so combining them causes the editor to attempt full-class rewrites instead of targeted edits.\n" +
        "   BAD (over-combined, SAME FILE): \"Add a _timer field and initialize it in the constructor, then add a RunTimerTasks method\"\n" +
        "   GOOD (3 steps, SAME FILE): Step 1: \"Add _timer field declaration after the last existing timer field\", Step 2: \"Initialize _timer in the constructor after existing timer initializations\", Step 3: \"Add RunTimerTasks method after the last existing RunXxxTasks method\"\n" +
        "   BAD (over-combined, SAME FILE): \"Add a isMenuPanelOpen property and add showMenuPanel/closeMenuPanel methods\"\n" +
        "   GOOD (3 steps, SAME FILE): Step 1: \"Add isMenuPanelOpen property declaration after the last existing property\", Step 2: \"Add showMenuPanel() method after the last existing method\", Step 3: \"Add closeMenuPanel() method after showMenuPanel()\"\n" +
        "   BAD (too vague): \"Fix the dashboard\"\n" +
        "   GOOD: \"In Dashboard.renderCards(): include archived cards in the existing filteredCards calculation when showArchived is true\"\n" +
        "   RULE OF THUMB: If the change description contains 'and ... then ...' or mentions 2+ of {field, property, constructor, method, handler} in a single step, SPLIT IT.\n" +
         "9. CRITICAL: Each step's change field MUST include the exact method/function/variable name being changed (e.g., \"getTimedGreetingMessage\", \"renderCards\", \"constructor\"). " +
             "The execution pipeline uses this name to locate the correct code position in the file. " +
             "If the change description lacks a code identifier, the system cannot find the target location. " +
             "Also describe the old behavior and the new behavior.\n" +
             "   BAD (missing method name): \"Add additional specific hour-based greetings to cover early morning, late night, and midnight periods\"\n" +
             "   GOOD (includes method name): \"Add early morning, late night, and midnight greeting branches to getTimedGreetingMessage()\"\n" +
        "10. UI layout rule: if the request is about visual position/spacing/screen location (top right, under, overlay, mobile-only, etc.), plan a stylesheet/CSS step. Do NOT satisfy visual placement by reordering existing HTML nodes. Use HTML only to create a missing control or fix missing wiring, and use the component script when changing event handlers.\n" +
        "11. If the user stated any constraints (e.g. 'do not use x'), include them verbatim in the 'change' field.\n" +
        "12. If the file path contains \"\\\\\" escape it for JSON: use \"path/to/file.ext\"\n" +
        "13. For each edit step (relative path in \"file\"), also set \"referenceFiles\" to a list of file paths the edit pipeline should load as context. Include files that define types, methods, or patterns the edit needs to reference. This keeps the edit context small and focused.\n" +
        "14. When editing a component/UI file or making changes involving imports/aliases, first read the target file's imports. Include the import source files in \"referenceFiles\" so the edit pipeline can verify aliases are correct before making changes.\n" +
        "15. NEVER use _web_search to find, read, or understand code that exists inside this project's repository. " +
        "For reading project source files use _explore with the relative file path. " +
        "_web_search is ONLY for external resources (public docs, npm packages, Stack Overflow, API references). " +
        "If you don't know which file contains the code, add an _explore step first.\n" +
        "16. Context and memory discipline: do not ask to read everything. Prefer the smallest file set that proves names, signatures, imports, and local patterns. Use referenceFiles for narrow supporting context rather than extra edit steps.\n" +
        "17. Describe plan steps as the MINIMAL delta needed. The DISCOVERY CONTEXT section shows actual file content. " +
        "DO NOT re-describe existing functionality as something that needs to be built. " +
        "BAD: \"Modify GetUsersWithCalendarNotificationsEnabled to collect all events per user and send Firebase notifications\" " +
        "(the method already collects events — \"collect\" is wrong). " +
        "GOOD: \"After the existing usersWithEvents loop, send Firebase notification for each user with the events list\" " +
        "(describes only the missing logic). " +
        "Read the file body in DISCOVERY CONTEXT to understand what already exists, then describe ONLY what is missing.\n" +
         "18. CREATE TABLE MUST BE INLINE (CRITICAL): If the task involves creating a new database table and inserting/updating data into it, " +
         "the CREATE TABLE IF NOT EXISTS statement MUST be placed INSIDE the method body, BEFORE the INSERT/UPDATE statement. " +
         "Do NOT create a separate 'table creation' method or step — the table creation is an inline guard clause, not a separate concern. " +
         "BAD: Step 1: 'Add CreateBenchmarksTable method', Step 2: 'Add PostBenchmarks endpoint with INSERT' — WRONG, these should be ONE step. " +
         "GOOD: 'Add PostBenchmarks endpoint with inline CREATE TABLE IF NOT EXISTS and INSERT statement inside the method body'.\n\n" +
        "### OUTPUT FORMAT ###\n" +
        "{\n" +
        "  \"thinking\": \"1-2 lines: which file needs changing and why\",\n" +
        "  \"summary\": \"one sentence: what this step accomplishes\",\n" +
        "  \"score\": <0-100>,\n" +
        "  \"plan\": [\n" +
        "    {\n" +
        "      \"file\": \"wwwroot/app.js\",\n" +
        "      \"change\": \"Modify confirmFilePicker to append files to existing list\",\n" +
        "      \"targetSymbol\": \"confirmFilePicker\",\n" +
        "      \"line\": 42,\n" +
        "      \"referenceFiles\": [\"wwwroot/utils.js\", \"wwwroot/types.js\"]\n" +
        "    }\n" +
        "  ]\n" +
        "}" +
        "22. LINE NUMBERS (CRITICAL): Every line of file content above is PREFIXED with its line number " +
        "(e.g. \"104: <div class=\\\"card-tags\\\">\"). Use those EXACT numbers in the \"line\" field — do NOT guess or " +
        "estimate. The line prefix IS the correct line number in the file. If the change targets the section " +
        "at line 96, set \"line\": 96. These numbers are used during execution to find the right code location " +
        "in files with repeated sections.\n" +
        "18. DATA FLOW TRACING: Before planning an edit that modifies how data is displayed or accessed, " +
        "trace WHERE the data comes from. Read type definitions to understand the full data structure. " +
        "Example: if the task is about 'image preview navigation', don't assume images come from filtering " +
        "a file list — check the actual type definitions to see if there's a metadata field with " +
        "screenshot/artwork/cover URLs. Plan your edit based on the ACTUAL data structure, not assumptions.\n" +
        "19. When the DISCOVERY CONTEXT shows a type reference like `romMetadata?: RomMetadata`, and the " +
        "task involves data that might live in that nested type, add a _explore step to read the RomMetadata " +
        "type definition BEFORE planning the edit. You cannot plan correctly without understanding the " +
        "full data structure.\n" +
        "20. CROSS-FILE ENDPOINT WIRING: When the task involves creating a new backend endpoint (e.g., in a .cs controller), " +
        "and the frontend needs to call it, you MUST add a step to create the corresponding method in the frontend service file " +
        "(e.g., grandtheft.service.ts) BEFORE adding the UI code that calls it. " +
        "Do NOT reuse methods from unrelated services (e.g., enderService) just because they have similar names. " +
        "If the service method does not exist, plan a step to create it.\n" +
       "21. SCAFFOLDING (CRITICAL & MANDATORY): When the task asks to CREATE a new component... " +
        "your plan MUST contain the following steps in order:\n" +
        "   1. A `_command` step to run the framework's CLI generator. Use `;` to separate commands (e.g., `cd maxhanna.client; npx ng g c components/recipe --skip-tests`). NEVER use `&&` as it fails in PowerShell.\n" +
        "   2. An edit step for `app.module.ts` to register the new component in the declarations array. This is MANDATORY.\n" +
        "   3. Edit steps to modify the newly generated `.ts`, `.html`, and `.css` files.\n" +
        "   Do NOT manually create the files with `_create_file`. Do NOT bypass scaffolding by using `edit` with `fullFile` on a non-existent file. The system will inject the scaffolding command automatically if you forget.\n" +
        "22. COMPONENT TEMPLATE WIRING (CRITICAL): When the task involves adding UI elements (buttons, inputs) that trigger new " +
        "actions (e.g., (click)=\"doSomething()\"), you MUST plan the step to implement the method in the TypeScript " +
        "component file (e.g., .ts) BEFORE the step to edit the HTML template (e.g., .html) to reference it. " +
        "Do NOT plan HTML edits that depend on .ts methods if the .ts step comes later. The .ts step MUST come first.\n" +
        "actions (e.g., (click)=\"doSomething()\"), you MUST add a step to implement the method in the TypeScript " +
        "component file (e.g., .ts) BEFORE editing the HTML template (e.g., .html) to reference it. " +
        "Do NOT reference methods in the HTML template that do not exist in the component class yet. " +
        "If the component class does not have the method, plan a step to add it first.\n" +
        "23. SERVICE DEPENDENCIES (CRITICAL): When planning to call a method on a service (e.g., `this.userEventService.insertUserEvent(...)`) that is NOT already imported and injected into the constructor of the target file, you MUST add a separate step FIRST to import the service and add it to the constructor parameters. Do NOT assume the service is already available in the component. If the service method requires a specific model/interface (e.g., `UserEvent`), you MUST read that model's definition to know the exact properties required before writing the call. When describing the call in the step description, you MUST use the exact syntax `this.serviceName.methodName()` so the system can automatically detect the dependency.\n" +
        "24. MODEL CONSTRUCTION: When passing an object to a service method, you MUST match the exact properties of the target interface. Do NOT invent properties. If the interface requires `{ userId, eventType, eventText }`, do not pass `('wordler', guess)`. Read the interface definition first.\n" +
        "25. FEATURE IMPLEMENTATION STACK (CRITICAL): When the user asks to build a new feature (e.g., 'Create a paint component where users can paint, save, and view paintings'), you MUST plan the steps in the following strict architectural order. Do NOT combine these phases into fewer steps.\n" +
        "   a. Backend Data Model (if needed): Create the C# data contract (e.g., `Painting.cs`) with the required properties.\n" +
        "   b. Backend Controller/Service (if needed): Create the endpoint (e.g., `PaintController.cs`) to save and retrieve the data. \n" +
        "   c. Frontend Data Contract: Create the TypeScript interface (e.g., `painting.ts`) matching the backend model.\n" +
        "   d. Frontend Service: Create the Angular service (e.g., `paint.service.ts`) with methods to call the backend API (e.g., `savePainting`, `getPaintings`).\n" +
        "   e. Frontend Component Scaffolding: Run the `_command` step to generate the component.\n" +
        "   f. Component Logic (.ts): Inject the new service in the constructor and implement the methods to save/load paintings.\n" +
        "   g. Component Template (.html): Build the UI (canvas, buttons, list).\n" +
        "   h. Routing/Module: Register the new component in `app.module.ts` and routing if necessary.\n" +
        "   Do NOT skip steps. Do NOT combine the service creation and the component logic into one step. Each letter (a through h) should be its own step in the plan if required by the feature.\n" +
        "26. NUMBERED LIST ADHERENCE: If the user's request contains a numbered list of tasks (e.g., '1. Create folder, 2. Create file, 3. Add heading'), your plan MUST contain AT LEAST that many steps. Do NOT combine multiple numbered items into a single step. Follow the user's structure exactly.\n" +
        "27. FILE CREATION MARKER: When creating a new file that does not exist yet, you MUST use \"_create_file\" as the step type. Put the full file path in the \"file\" field and the complete file content in the \"newString\" field. NEVER use a relative path as the file marker for a new file — the executor will treat it as an edit to an existing file and fail.\n" +
        "28. PATTERN MIRRORING (CRITICAL): When the user asks to 'mirror', 'copy', or 'wire up exactly like' a pattern from another file, your plan steps MUST explicitly name the exact methods, properties, and HTML attributes to copy. " +
        "For each file type involved:\n" +
        "   * .ts steps: Explicitly list the exact property declarations and method signatures to add (e.g., 'Add X property, Y() method, and Z() method').\n" +
        "   * .html steps: Explicitly describe BOTH the new HTML structure to insert AND any attribute/event bindings to add to existing elements.\n" +
        "Do NOT invent new method names. Use the EXACT names from the source. " +
        "Do NOT invent custom wrapper tags if the source uses standard elements with classes. " +
        "Copy the EXACT DOM structure, CSS classes, and text content from the source. " +
         "Do NOT invent method calls inside the HTML that do not exist in the source pattern.\n" +
        "29. METHOD CREATION IS ONE STEP (CRITICAL): Creating a new method includes its signature, parameters, and body — " +
        "all in ONE step. Do NOT split into \"Create method signature\" and \"Implement method body\". " +
        "A method is ONE coherent block at ONE location. " +
        "If the method body needs inline SQL (CREATE TABLE IF NOT EXISTS + INSERT/UPDATE/SELECT), " +
        "that SQL belongs INSIDE the method body in the same step. " +
        "BAD: Step 1: \"Create Benchmarks table\", Step 2: \"Add PostBenchmarks method with INSERT\"\n" +
        "GOOD: \"Add PostBenchmarks method with CREATE TABLE IF NOT EXISTS and INSERT logic\"\n" +
        "BAD: Step 1: \"Add Benchmarks schema\", Step 2: \"Add INSERT endpoint\"\n" +
        "GOOD: \"Add PostBenchmarks endpoint method with inline CREATE TABLE IF NOT EXISTS and parameterized INSERT\"\n" +
        "RATIONALE: The CREATE TABLE IF NOT EXISTS runs at the START of the method body, " +
        "before any INSERT/UPDATE/SELECT. It is NOT a separate schema definition — " +
        "it is an inline guard clause that ensures the table exists. " +
        "Treating it as a separate step forces the editor to insert the CREATE TABLE into " +
        "a random unrelated location instead of inside the method where it belongs.\n" +
        "30. DO NOT CREATE SEPARATE SETUP ENDPOINTS: When a single new endpoint needs both " +
        "infrastructure setup (CREATE TABLE, connection check) and business logic (INSERT/UPDATE/SELECT), " +
        "put the setup INSIDE the endpoint method as inline code at the top. " +
        "Do NOT create a separate \"PostXxxTable\" or \"InitializeXxx\" endpoint as a separate step. " +
        "BAD: Step 1: \"Add PostBenchmarksTable endpoint (creates table)\", Step 2: \"Add AddBenchmark endpoint (inserts data)\"\n" +
        "GOOD: \"Add AddBenchmark endpoint with CREATE TABLE IF NOT EXISTS at the top and parameterized INSERT below\"\n" +
        "RATIONALE: A separate setup endpoint creates unnecessary public API surface. " +
        "Table creation is an implementation detail that belongs inside the method that needs it.\n";

    private static string BuildFailedEditHistory(List<object> allSteps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Failures from previous execution:");
        var failures = allSteps.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "edit" &&
                        (!s.TryGetValue("status", out var st) || st?.ToString() != "done"))
            .Take(8).ToList();
        if (failures.Count == 0) { sb.AppendLine("- No failed edits."); return sb.ToString(); }
        foreach (var f in failures)
        {
            sb.AppendLine($"- {f.GetValueOrDefault("path")}: {f.GetValueOrDefault("error") ?? f.GetValueOrDefault("status")}");
            if (f.TryGetValue("snippet", out var sn) && sn != null) sb.AppendLine($"  Nearby: {sn}");
        }
        return sb.ToString();
    }
     
    private async Task<string> BuildRequirementChecklistAsync(string prompt, CancellationToken ct)
    {
        var sys =
            "You extract a short checklist of literal, testable requirements from a coding task. " +
            "Output ONLY JSON: {\"requirements\": [\"...\", \"...\"]}. 3-6 items max. " +
            "Each item must be objectively checkable, not vague ('good code'), and must preserve the " +
            "task's specific wording (tone, style, exact behavior). " +
            "CRITICAL: if the task implies content must have a specific quality (funny, concise, matches " +
            "brand voice, etc.), that is a requirement. If the task implies the change must be VISIBLE/USED/" +
            "WIRED UP — not just exist as new code — that is always a requirement, even if not stated explicitly, " +
            "because 'add X so it does Y' always implies X actually gets called somewhere that produces Y.";

        var (raw, _, _) = await CallLlmRaw(sys, prompt, ct, requestTimeout: _infiniteTimeout, maxTokens: 400);
        if (string.IsNullOrWhiteSpace(raw)) return "";

        try
        {
            var cleaned = AgentUtilities.ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("requirements", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return "";

            var items = arr.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(6)
                .ToList();
            if (items.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("### EXPLICIT REQUIREMENTS CHECKLIST ###");
            sb.AppendLine("Verify EACH item individually against the actual code/content. A task is only complete " +
                           "if EVERY item below is satisfied — do not form one overall impression.");
            for (var i = 0; i < items.Count; i++) sb.AppendLine($"  {i + 1}. {items[i]}");
            return sb.ToString();
        }
        catch { return ""; }
    }
    private static string PreviewForPrompt(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "\n[truncated]";

    private static List<string> ParseBuildCommands(string buildCommands)
    {
        if (string.IsNullOrWhiteSpace(buildCommands)) return new List<string>();
        try { var arr = JsonSerializer.Deserialize<List<string>>(buildCommands); if (arr?.Count > 0) return arr.Where(c => !string.IsNullOrWhiteSpace(c)).ToList(); }
        catch { }
        return new List<string> { buildCommands.Trim() };
    }

    private static string BuildReplanPrompt(string originalPrompt, List<string> history, string? steeringContext = null,
    AgentPlan? existingPlan = null, List<object>? executedSteps = null,
    string qualityCheckReason = "", string fileContents = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine("Previous plan did not fully complete. You must ONLY plan the FEWEST new steps needed.");
        sb.AppendLine("IMPORTANT: Only plan steps that address specific failures below. Do NOT repeat existing steps.");
        sb.AppendLine("Only address concrete failures or work the user EXPLICITLY requested that is genuinely missing.");
        sb.AppendLine("Do NOT add new files, features, refactors, or improvements the user did not ask for.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(steeringContext)) { sb.AppendLine("## Steering"); sb.AppendLine(steeringContext); sb.AppendLine(); }

        if (existingPlan?.Plan?.Count > 0)
        {
            sb.AppendLine("## Existing plan with results");
            for (var i = 0; i < existingPlan.Plan.Count; i++)
            {
                var step = existingPlan.Plan[i];

                string? status = null;
                string? output = null;
                if (executedSteps != null)
                {
                    var result = executedSteps.OfType<Dictionary<string, object?>>()
                        .LastOrDefault(s =>
                            s.TryGetValue("planItemIndex", out var pIdxObj) &&
                            pIdxObj is int pIdx &&
                            pIdx == i);
                    if (result != null)
                    {
                        status = result.GetValueOrDefault("status")?.ToString();
                        var raw = result.GetValueOrDefault("output")?.ToString();
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            var trimmed = raw.Trim();
                            if (trimmed.Length > 2000) trimmed = trimmed[..2000] + "… (truncated)";
                            output = trimmed;
                        }
                    }
                }
                var tag = status switch
                {
                    "done" or "modified" or "created" => "✓ DONE",
                    "skipped" => "○ SKIPPED",
                    "error" => "✗ FAILED",
                    _ => "… PENDING"
                };
                sb.AppendLine($"  {tag} {step.File}: {step.Change}");
                if (output != null)
                    sb.AppendLine($"    stdout: {output}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Original task"); sb.AppendLine(originalPrompt); sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(qualityCheckReason))
        {
            sb.AppendLine("## Quality check assessment");
            sb.AppendLine(qualityCheckReason);
            sb.AppendLine();
            sb.AppendLine("CRITICAL: The quality check above identifies specific missing implementations. You MUST create steps to implement exactly what it asks for. Do not return an empty plan if the quality check identifies missing methods or properties that need to be added.");
        }

        sb.AppendLine("## What went wrong");
        foreach (var h in history) sb.AppendLine(h);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(fileContents))
        {
            sb.AppendLine("## Current file contents");
            sb.Append(fileContents);
            sb.AppendLine();
        }
        sb.AppendLine("⚠ CRITICAL: The plan may involve SEQUENTIAL steps (e.g. 'Create file with X, then modify it to Y').");
        sb.AppendLine("If the file content matches Y (the final state), do NOT report X as missing. X was intentionally replaced by Y.");
        sb.AppendLine("Do NOT generate steps to revert the file to an earlier state. Only generate steps if the FINAL requested state is missing.");
        sb.AppendLine();
        sb.AppendLine("STOP AND THINK: does the CURRENT FILE CONTENT shown above ALREADY satisfy the ORIGINAL TASK, even imperfectly?");
        sb.AppendLine("If the explicit request is met, return an EMPTY plan — do NOT propose restructuring, renaming, moving code");
        sb.AppendLine("between methods, or 'cleanup' the user did not ask for.");
        sb.AppendLine("Only add a step if you can name a SPECIFIC piece of the ORIGINAL TASK that is VERIFIABLY absent from the");
        sb.AppendLine("current file content above.");
        sb.AppendLine("⚠ CRITICAL: The Quality Check assessment below may claim something is missing. DO NOT blindly trust it.");
        sb.AppendLine("Read the file content yourself. If the code IS already there, return an EMPTY plan. The quality check LLM often hallucinates failures.");

        if (string.IsNullOrWhiteSpace(qualityCheckReason))
        {
            sb.AppendLine("NEVER introduce a property/variable name that does not already appear in the current file content above —");
            sb.AppendLine("reuse existing names exactly, character for character.");
        }
        else
        {
            sb.AppendLine("If the quality check requires a new method or property, you MAY introduce it, but it must be exactly named as requested by the task or quality check.");
        }

        sb.AppendLine();
        sb.AppendLine("SCOPE DISCIPLINE — the #1 replan failure mode is scope drift:");
        sb.AppendLine("  * Do NOT reinterpret the original task. Read '## Original task' literally and stay on that topic.");
        sb.AppendLine("  * Do NOT pivot to a different approach. If the EXISTING PLAN (✓ DONE steps) chose approach X,");
        sb.AppendLine("    your new step must EXTEND X, not replace it with approach Y. If you think X is wrong, return");
        sb.AppendLine("    an EMPTY plan — the user will steer, not the replanner.");
        sb.AppendLine("  * Do NOT add new files, features, refactors, or improvements the user did not ask for.");
        sb.AppendLine("  * Do NOT invent new helper methods in service files. If you need to call a service, use the EXISTING methods. For example, if UserEventService has `insertUserEvent`, call it directly. Do NOT create `insertUserEventWithPlantName`.");
        sb.AppendLine("  * If a step in the EXISTING PLAN added a property/variable/CSS-rule/method, that name NOW EXISTS");
        sb.AppendLine("    in the file. Reuse it. Do NOT introduce a parallel mechanism.");
        sb.AppendLine("  * If the verification gaps can be closed by EDITING the code that step 1 already added, do that —");
        sb.AppendLine("    do not add a second step that lives in a different file.");
        sb.AppendLine("  * CROSS-FILE DEPENDENCIES: If a step failed because it referenced methods or properties that don't exist in the target file (e.g., an HTML template referencing a method not yet implemented in the .ts component), you MUST add a step to implement the missing method/property in the correct file BEFORE retrying the failed step.");
        sb.AppendLine();
        sb.AppendLine("Add at most 1 new step. If everything is done or you are unsure, return an EMPTY plan with no steps.");
        return sb.ToString();
    }

    private static string BuildLowScoreSteering(AgentPlan plan, string? prior)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Your previous plan scored {plan.Score}/100, below the confidence threshold of {PLAN_SCORE_THRESHOLD}.");
        sb.AppendLine("Do NOT explore more files. The discovery context already has everything you need.");
        sb.AppendLine("Raise your score by making each step's change description more precise:");
        sb.AppendLine("  * Name the exact method, property, or line range (e.g. \"In getUser() around line 42:…\")");
        sb.AppendLine("  * Describe the exact old → new behavior clearly");
        sb.AppendLine("  * If the plan is already correct, simply increase your score to 85+ and re-output it");
        sb.AppendLine("Do NOT change the file paths or add steps. Do NOT add _explore steps.");
        if (!string.IsNullOrWhiteSpace(prior)) { sb.AppendLine(); sb.AppendLine(prior); }
        return sb.ToString();
    }

    private static string AppendExploreSteering(string? prior)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You have exhausted exploration rounds. Produce the final edit plan NOW — no more _explore steps.");
        sb.AppendLine("Be decisive: keep the same file paths and give each step a precise change description. Score the plan 85+ if it's correct.");
        if (!string.IsNullOrWhiteSpace(prior)) { sb.AppendLine(); sb.AppendLine(prior); }
        return sb.ToString();
    }

    private static string BuildPlannerDiscoveryContext(string fullDiscovery)
    {
        if (string.IsNullOrWhiteSpace(fullDiscovery)) return fullDiscovery;
        var result = new StringBuilder();
        var sections = Regex.Split(fullDiscovery, @"(?=### \S)");
        var fileCount = 0;
        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section)) continue;
            var trimmed = section.TrimStart();
            if (!trimmed.StartsWith("### read") && !trimmed.StartsWith("### list"))
            {
                result.AppendLine(section.TrimEnd());
                continue;
            }
            if (fileCount >= MAX_DISCOVERY_FILES)
            {
                result.AppendLine("...(additional files omitted from planner context)");
                break;
            }
            var lines = section.Split('\n');
            result.AppendLine(lines[0]);
            var body = lines.Skip(1).ToArray();
            var numbered = body.Select((line, idx) => $"{idx + 1}: {line}").ToArray();
            if (numbered.Length <= MAX_LINES_PER_DISCOVERY_FILE)
            {
                result.AppendLine(string.Join('\n', numbered));
            }
            else
            {
                var head = numbered.Take(MAX_LINES_PER_DISCOVERY_FILE / 2).ToArray();
                var tail = numbered.Skip(numbered.Length - MAX_LINES_PER_DISCOVERY_FILE / 2).ToArray();
                result.AppendLine(string.Join('\n', head));
                result.AppendLine($"...({numbered.Length - MAX_LINES_PER_DISCOVERY_FILE} lines omitted — head and tail shown)...");
                result.AppendLine(string.Join('\n', tail));
                result.AppendLine($"...(truncated — full content used during execution)");
            }
            result.AppendLine();
            fileCount++;
        }
        return result.ToString();
    }
}
