using Xunit;
using Weaver.Services;
using Weaver;
using Weaver.Controllers;

namespace Weaver.UnitTests;

public class PipelineTests
{
    [Fact]
    public void ExtractRelevantExcerpt_SmallFile_ReturnsFullContent()
    {
        // Arrange
        var content = "using System;\n\npublic class Test {\n    public void Run() {}\n}";
        var desc = "fix something";

        // Act
        var result = AgentDiscovery.ExtractRelevantExcerpt(content, desc, null);

        // Assert
        // Small files aren't skeletonized the same way if they fit, but let's check it contains the core
        Assert.Contains("public class Test", result);
        Assert.Contains("public void Run()", result);
    }

    [Fact]
    public void GetSkeletonForRange_CSharp_IdentifiesSignatures()
    {
        // Arrange
        var lines = new[]
        {
            "    public class MyClass {",
            "        private int _field;",
            "        [HttpGet]",
            "        public async Task<IActionResult> GetData(int id) {",
            "            return Ok();",
            "        }",
            "    }"
        };

        // Act
        var result = AgentSkeleton.GetSkeletonForRange(lines, 0, lines.Length);

        // Assert
        Assert.Contains("public class MyClass", result);
        Assert.Contains("GetData", result);
        Assert.Contains("GetData", result);
    }

    [Fact]
    public void GetSkeletonForRange_TypeScript_IdentifiesSignatures()
    {
        // Arrange
        var lines = new[]
        {
            "export interface User { id: number; }",
            "export class UserService {",
            "    async getUser(id: string): Promise<User> {",
            "        return fetch(id);",
            "    }",
            "}"
        };

        // Act
        var result = AgentSkeleton.GetSkeletonForRange(lines, 0, lines.Length);

        // Assert
        Assert.Contains("User", result);
        Assert.Contains("UserService", result);
        Assert.Contains("getUser", result);
    }

    [Fact]
    public void ExtractRelevantExcerpt_NoTarget_ReturnsFullSkeleton()
    {
        // Arrange
        var content = "using System;\npublic class Test {\n    public void M1() {}\n    public void M2() {}\n}";
        var desc = "something unrelated";

        // Act
        var result = AgentDiscovery.ExtractRelevantExcerpt(content, desc, null);

        // Assert
        Assert.Contains("public class Test", result);
        Assert.Contains("Test", result);
        Assert.Contains("M1", result);
        Assert.Contains("M2", result);
    }

    [Fact]
    public void ExtractMeaningfulKeywords_StripsCommonVerbs()
    {
        // Arrange
        var prompt = "Please add a new button to the user dashboard";

        // Act
        var keywords = AgentDiscovery.ExtractMeaningfulKeywords(prompt.ToLowerInvariant());

        // Assert
        Assert.DoesNotContain("add", keywords);
        Assert.DoesNotContain("please", keywords);
        Assert.Contains("button", keywords);
        Assert.Contains("dashboard", keywords);
    }

    [Fact]
    public void GetSkeletonForRange_ComplexSignatures_IdentifiesThem()
    {
        // Arrange
        var lines = new[]
        {
            "    [ApiController]",
            "    [Route(\"api/[controller]\")]",
            "    public class MyController : ControllerBase {",
            "        private readonly ILogger<MyController> _logger;",
            "        ",
            "        [HttpGet(\"{id}\")]",
            "        public async Task<ActionResult<Data>> Get(int id, [FromQuery] bool extra) {",
            "            return Ok();",
            "        }",
            "    }"
        };

        // Act
        var result = AgentSkeleton.GetSkeletonForRange(lines, 0, lines.Length);

        // Assert
        Assert.Contains("MyController", result);
        Assert.Contains("Get", result);
    }

    [Fact]
    public void ExtractRelevantExcerpt_FindsTargetByKeyword()
    {
        // Arrange
        var lines = new List<string> { "using System;", "public class C {" };
        for (int i = 0; i < 100; i++) lines.Add($"    public void Method{i}() {{ }}");
        lines.Add("    public void SecretFunction() {");
        lines.Add("        Console.WriteLine(\"secret\");");
        lines.Add("    }");
        for (int i = 100; i < 200; i++) lines.Add($"    public void Method{i}() {{ }}");
        lines.Add("}");
        var content = string.Join("\n", lines);

        // Act
        var result = AgentDiscovery.ExtractRelevantExcerpt(content, "fix the SecretFunction", null);

        // Assert
        Assert.Contains("SecretFunction", result);
        Assert.Contains("secret", result);
        Assert.Contains("Method0", result);
        Assert.Contains("Method199", result);
    }

    [Fact]
    public void ExtractRelevantExcerpt_WithAnchor_CentersOnAnchor()
    {
        // Arrange
        var lines = new List<string> { "using System;", "public class C {" };
        for (int i = 0; i < 100; i++) lines.Add($"    public void M{i}() {{ }}");
        lines.Add("    public void Target() {");
        lines.Add("        // Anchor is here");
        lines.Add("        DoWork();");
        lines.Add("    }");
        for (int i = 100; i < 200; i++) lines.Add($"    public void M{i}() {{ }}");
        lines.Add("}");

        var content = string.Join("\n", lines);
        var planOld = "    public void Target() {\n        // Anchor is here";

        // Act
        var result = AgentDiscovery.ExtractRelevantExcerpt(content, "fix target", planOld);

        // Assert
        Assert.Contains("Target", result);
        Assert.Contains("Anchor is here", result);
        Assert.Contains("M0", result);
        Assert.Contains("M199", result);
    }

    [Fact]
    public void TryRepairTruncatedPlanJson_ClosesBrackets()
    {
        var truncated = "{\"plan\": [{\"file\": \"test.cs\", \"change\": \"add method\"";
        var result = AgentPlanParsing.TryRepairTruncatedPlanJson(truncated);
        Assert.NotNull(result);
        Assert.Contains("}]}", result);
    }

    [Fact]
    public void ParseStepExplorationResponse_MultipleJsonFragments_UsesLastCompleteObject()
    {
        // Arrange
        var raw = """
        {"ready": false, "filesToRead": ["src/app/app.component.ts"], "reasoning": "I need to see the component first"}
        {"ready": true, "refinedChange": "Update getTimedGreetingMessage with four new time ranges", "targetSymbol": "getTimedGreetingMessage", "estimatedLineRange": "~1084-1118", "confidence": 93}
        """;

        // Act
        var result = AgentPlanParsing.ParseStepExplorationResponse(raw);

        // Assert
        Assert.True(result.Ready);
        Assert.Equal("getTimedGreetingMessage", result.TargetSymbol);
        Assert.Contains("Update getTimedGreetingMessage", result.RefinedChange);
        Assert.Equal(93, result.Confidence);
    }

    [Fact]
    public void ExtractTargetSymbolFromChange_DoesNotPromoteGenericVerbAsMethodName()
    {
        // Arrange
        var task = "Add additional time-based greetings to handle early morning hours before 5AM";

        // Act
        var result = AgentMethodInventory.ExtractTargetSymbolFromChange(task);

        // Assert
        Assert.NotEqual("handle", result);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractTargetSymbolFromChange_DottedClassMethod_ReturnsDottedSymbol()
    {
        // The benchmark-4 case verbatim: the class name alone resolved the WHOLE class and the
        // focused replacement returned just the method → IndentationError. A dotted symbol lets
        // the AST resolver extract only the method inside the class.
        var result = AgentMethodInventory.ExtractTargetSymbolFromChange(
            "In MyHTTPRequestHandler.do_GET method (around line 23): replace `return super().do_get()` with `super().do_GET()`");
        Assert.Equal("MyHTTPRequestHandler.do_GET", result);
    }

    [Fact]
    public void ExtractTargetSymbolFromChange_DottedDunder_ReturnsDottedSymbol()
    {
        var result = AgentMethodInventory.ExtractTargetSymbolFromChange(
            "Fix the Spider.__init__ method to accept legs");
        Assert.Equal("Spider.__init__", result);
    }

    [Fact]
    public void ExtractTargetSymbolFromChange_FilenameWithExtension_IsNotPromotedToDottedSymbol()
    {
        // "README.md" must never become a dotted `Class.method` symbol — the file-extension
        // guard rejects it (and the generic fallbacks may still pick a bare word like "README",
        // which is the pre-existing, harmless behavior — the point is no dotted symbol).
        var result = AgentMethodInventory.ExtractTargetSymbolFromChange(
            "Document the new endpoint in README.md");
        Assert.NotEqual("README.md", result);
        Assert.DoesNotContain(".md", result ?? "");
    }

    [Fact]
    public void SymbolExistsInContent_DottedSymbol_RequiresBothParts()
    {
        var content = """
        class MyHTTPRequestHandler(BaseHTTPRequestHandler):
            def do_GET(self):
                pass
        """;
        Assert.True(AgentMethodInventory.SymbolExistsInContent("MyHTTPRequestHandler.do_GET", content));
        Assert.False(AgentMethodInventory.SymbolExistsInContent("MyHTTPRequestHandler.do_POST", content));
        Assert.True(AgentMethodInventory.SymbolExistsInContent("MyHTTPRequestHandler", content));
    }

    [Fact]
    public void ExtractMethodBodiesByKeywords_PreservesExactTargetSymbolInVagueTask()
    {
        // Arrange
        var content = """
        class Demo {
            login() {
                return this.getTimedGreetingMessage(this.user?.username || '');
            }

            getTimedGreetingMessage(username: string): string {
                const hour = new Date().getHours();
                let greeting = '';

                if (hour >= 5 && hour < 12) {
                    greeting = `Morning, ${username}!`;
                } else if (hour >= 12 && hour < 17) {
                    greeting = `Afternoon, ${username}!`;
                } else {
                    greeting = `Night, ${username}!`;
                }

                return greeting;
            }

            cleanStoryText(text: string) {
                return text?.replace(/\[\/?[^\]]/g, '')?.replace(/https?:\/\/[^\s]+/g, '');
            }
        }
        """;

        // Act
        var result = AgentMethodInventory.ExtractMethodBodiesByKeywords(content, "Add more funny greeting messages to getTimedGreetingMessage");

        // Assert
        Assert.Contains("getTimedGreetingMessage", result);
        Assert.Contains("Morning, ${username}!", result);
        Assert.Contains("Afternoon, ${username}!", result);
    }

    [Fact]
    public void EstimateTokens_ReturnsApproximateCount()
    {
        var text = "Hello world"; // 11 chars
        var result = AgentTokenMetrics.EstimateTokens(text);
        Assert.Equal(2, result); // "Hello" + "world" (single space is free) = 2
    }

    [Theory]
    [InlineData("dotnet test", true)]
    [InlineData("cd maxhanna.client; npx ng g c recipe-menu --skip-tests", true)]
    [InlineData("Create a basic template structure that shows how we'll implement both components", false)]
    [InlineData("Explore app-title-bar.component.ts file", false)]
    public void LooksLikeShellCommand_RejectsPlanningProse(string command, bool expected)
    {
        Assert.Equal(expected, AgentProjectUtilities.LooksLikeShellCommand(command));
    }

    // ── Shell-command gate: the Set-Content / Out-File / Add-Content write cmdlets ──
    // A _command step whose change is not a recognizable shell command is rejected
    // (ValidateIncrementalStepAsync). The OS-filesystem discovery section and the
    // web-chain tool example TEACH desktop writes that lead with these three cmdlets
    // ("Set-Content -Path \"<desktop>\file.txt\" -Value \"<content>\" -Encoding UTF8").
    // If any of them ever drops out of the gate's knownCommands set, a planned desktop
    // write is rejected — these tests lock all three from the front of the command.

    [Theory]
    [InlineData("Set-Content -Path \"C:\\Users\\Saint\\Desktop\\ai_article.txt\" -Value \"hello world\" -Encoding UTF8")]
    [InlineData("Set-Content -Path C:\\Users\\Saint\\Desktop\\notes.txt -Value \"line\"")]
    [InlineData("Out-File -FilePath \"C:\\Users\\Saint\\Desktop\\results.txt\" -InputObject \"line one\"")]
    [InlineData("Out-File -FilePath C:\\Users\\Saint\\Desktop\\log.txt -Append")]
    [InlineData("Add-Content -Path \"C:\\Users\\Saint\\Desktop\\notes.txt\" -Value \"appended\"")]
    [InlineData("Add-Content -Path C:\\Users\\Saint\\Desktop\\journal.txt -Value \"entry\"")]
    public void LooksLikeShellCommand_WriteCmdletLeading_PlannedDesktopWritePasses(string command)
    {
        Assert.True(AgentProjectUtilities.LooksLikeShellCommand(command),
            $"planned desktop write was rejected by the shell-command gate: {command}");
    }

    // The exact write chains the prompts TEACH for OS/web tasks (discovery section +
    // web-chain tool-use example + CommandPipeline working example) — every one must
    // pass the gate so the planner's desktop write is never rejected mid-run.

    [Theory]
    [InlineData("Invoke-RestMethod https://example.com/api | Set-Content -Path \"C:\\Users\\Saint\\Desktop\\file.txt\"")]
    [InlineData("Invoke-RestMethod https://pokeapi.co/api/v2/pokemon?limit=1000 | Select-Object -ExpandProperty results | ForEach-Object { $_.name } | Set-Content C:\\Users\\Saint\\Desktop\\pokemon.csv")]
    [InlineData("Get-Content \"C:\\Users\\Saint\\Desktop\\data.txt\" | Add-Content -Path \"C:\\Users\\Saint\\Desktop\\notes.txt\"")]
    [InlineData("Get-ChildItem C:\\Users\\Saint\\Desktop\\*.log | Out-File -FilePath \"C:\\Users\\Saint\\Desktop\\log-list.txt\"")]
    [InlineData("Write-Output \"article summary\" | Set-Content -Path \"C:\\Users\\Saint\\Desktop\\ai_article.txt\" -Encoding UTF8")]
    [InlineData("Invoke-WebRequest https://example.com/page | Select-Object -ExpandProperty Content | Out-File -FilePath \"C:\\Users\\Saint\\Desktop\\page.html\"")]
    public void LooksLikeShellCommand_TaughtDesktopWriteChains_PassTheGate(string command)
    {
        Assert.True(AgentProjectUtilities.LooksLikeShellCommand(command),
            $"taught desktop-write chain was rejected by the shell-command gate: {command}");
    }

    // Precision guard: the additions must not turn prose that merely mentions the
    // write verbs into an accepted command.

    [Theory]
    [InlineData("Set the content of the desktop file to the article summary")]
    [InlineData("Write the fetched results into a text file on the desktop")]
    public void LooksLikeShellCommand_WriteVerbProse_StillRejected(string command)
    {
        Assert.False(AgentProjectUtilities.LooksLikeShellCommand(command),
            $"prose mentioning a write verb passed the shell-command gate: {command}");
    }

    // ── UnwrapWrappingQuotes (the "steered write never lands on Unix" regression) ──
    // ExecutePlan's _command handler previously ran Trim().Trim('`', '"', '\''), which
    // ate the TRAILING quote of a command that legitimately ends in a quoted path —
    // "echo 'repair data' > \"/tmp/x/report.txt\"" became an unterminated quote that
    // hung bash, so the steered desktop write never landed (Windows Set-Content commands
    // happened to end in a non-quote and never showed it). The unwrap must strip ONLY a
    // matching wrapping pair, never an unbalanced trailing quote.

    [Theory]
    [InlineData("echo 'repair data' > \"/tmp/x/report.txt\"", "echo 'repair data' > \"/tmp/x/report.txt\"")]
    [InlineData("echo v2.1 > release-version.txt", "echo v2.1 > release-version.txt")]
    [InlineData("\"Set-Content -Path C:\\x\\f.txt -Value \"data\" -Encoding UTF8\"", "Set-Content -Path C:\\x\\f.txt -Value \"data\" -Encoding UTF8")]
    [InlineData("'echo hi'", "echo hi")]
    [InlineData("`ls -la`", "ls -la")]
    [InlineData("\"unbalanced trailing quote", "\"unbalanced trailing quote")]
    [InlineData("unbalanced trailing quote\"", "unbalanced trailing quote\"")]
    [InlineData("  \"  padded  \"  ", "  padded  ")]
    public void UnwrapWrappingQuotes_OnlyStripsMatchingPairs(string input, string expected)
    {
        Assert.Equal(expected, AgentProjectUtilities.UnwrapWrappingQuotes(input));
    }

    // ── LooksLikeContentFetchCommand (the "api.current.ai" failure mode) ──
    // A _command step that fetches content from an http(s) URL with a download tool
    // must be steered to _web_search/_web_fetch. Legit URL-using commands (clone /
    // install / --source / git fetch) must never match.

    [Theory]
    [InlineData("Invoke-RestMethod https://api.current.ai/articles | Select-Object title, summary, url | ConvertTo-Csv -NoTypeInformation | Set-Content \"C:\\Users\\Saint\\Desktop\\ai_article_data.txt\"")]
    [InlineData("curl https://example.com/article -o article.txt")]
    [InlineData("wget https://example.com/data.json")]
    [InlineData("Invoke-WebRequest -Uri https://example.com/page -OutFile page.html")]
    [InlineData("irm https://api.github.com/repos/foo/bar")]
    [InlineData("python -c \"import urllib.request as u; open('out.txt','w').write(u.urlopen('https://example.com').read())\"")]
    [InlineData("python -c \"import requests; print(requests.get('https://example.com').text)\"")]
    [InlineData("node -e \"fetch('https://example.com/api').then(r => r.text()).then(console.log)\"")]
    [InlineData("(New-Object System.Net.WebClient).DownloadString(\"https://example.com\")")]
    public void LooksLikeContentFetchCommand_FlagsFetchingCommands(string command)
    {
        Assert.True(AgentProjectUtilities.LooksLikeContentFetchCommand(command));
    }

    [Theory]
    [InlineData("git clone https://github.com/foo/bar.git")]
    [InlineData("npm install https://github.com/foo/pkg")]
    [InlineData("dotnet add package Newtonsoft.Json --source https://api.nuget.org/v3/index.json")]
    [InlineData("git fetch origin")]
    [InlineData("git fetch https://github.com/foo/bar.git")]
    [InlineData("dotnet test")]
    [InlineData("curl --version")]
    [InlineData("Create a script that downloads files from a server")]
    public void LooksLikeContentFetchCommand_IgnoresLegitUrlAndLocalCommands(string command)
    {
        Assert.False(AgentProjectUtilities.LooksLikeContentFetchCommand(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksLikeContentFetchCommand_BlankCommandsNeverFlag(string? command)
    {
        Assert.False(AgentProjectUtilities.LooksLikeContentFetchCommand(command!));
    }

    [Fact]
    public void EditClassifier_TypeScriptPropertyAddition_UsesAnchoredEdit()
    {
        var step = new PlanStep
        {
            File = "src/app/recipe/recipe.component.ts",
            Change = "Add isMenuPanelOpen property declaration after the last existing property",
            TargetSymbol = "isMenuPanelOpen"
        };

        var result = EditClassifier.Classify(step, fileExists: true, ext: ".ts");

        Assert.Equal(EditStrategy.AnchoredEdit, result);
    }

    [Fact]
    public void EditStrategyResolver_InsertMethod_ResolvesAnchorWithoutReplacementIntent()
    {
        var source = """
        export class RecipeComponent {
          ngOnInit(): void {
            this.loadRecipes();
          }

          loadRecipes(): void {
          }
        }
        """;
        var intent = new EditIntent(EditIntentKind.InsertNearSymbol, "ngOnInit", "method");

        var decision = EditStrategyResolver.Decide(
            "src/app/recipe/recipe.component.ts",
            fileExists: true,
            fileContent: source,
            changeDescription: "Add showMenuPanel() method after ngOnInit()",
            intent);

        Assert.Equal(EditStrategy.InsertMethod, decision.Strategy);
        Assert.Equal("ngOnInit", decision.TargetName);
        Assert.Contains("ngOnInit", decision.ResolvedOldStr);
    }

    [Theory]
    [InlineData("move file.txt to sub/file.txt", "file.txt", "sub/file.txt")]
    [InlineData("rename current.cs → renamed.cs", "current.cs", "renamed.cs")]
    public void ExtractTargetPath_HandlesArrowsAndTo(string desc, string current, string expected)
    {
        var result = AgentDiscovery.ExtractTargetPath(desc, current, "/");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryDetectSimpleIntent_Delete_IdentifiesTarget()
    {
        // Act
        var plan = AgentPlanParsing.TryDetectSimpleIntent("delete the file src/temp.log");

        // Assert
        Assert.NotNull(plan);
        Assert.Equal("_delete_file", plan.Plan[0].File);
        Assert.Equal("src/temp.log", plan.Plan[0].Change);
    }

    [Fact]
    public void GetSkeletonForRange_Python_IdentifiesSignatures()
    {
        // Arrange
        var lines = new[]
        {
            "def global_func(a, b):",
            "    pass",
            "",
            "class MyClass(Base):",
            "    def method(self):",
            "        print('hi')"
        };

        // Act
        var result = AgentSkeleton.GetSkeletonForRange(lines, 0, lines.Length);

        // Assert
        Assert.Contains("def global_func() { ... }", result);
        Assert.Contains("def global_func() { ... }", result);
        Assert.Contains("MyClass", result);
        Assert.Contains("def method() { ... }", result);
    }

    [Fact]
    public void GetSkeletonForRange_GoAndRust_IdentifiesSignatures()
    {
        // Arrange
        var lines = new[]
        {
            "func (s *Server) Run(port int) error {",
            "    return nil",
            "}",
            "pub fn main() {",
            "    println!(\"hello\");",
            "}"
        };

        // Act
        var result = AgentSkeleton.GetSkeletonForRange(lines, 0, lines.Length);

        // Assert
        Assert.Contains("func", result);
        Assert.Contains("main", result);
    }
 

    [Fact]
    public void ParseDelimitedPlan_HandlesMultipleSteps()
    {
        var raw = @"
<<<THINKING>>>
I need to update two files.
<<<SUMMARY>>>
Update API and DTO
<<<SCORE>>> 85
<<<STEP 1>>>
FILE: api.cs
CHANGE: update get
<<<OLD>>>
void Get() {}
<<<NEW>>>
void Get(int id) {}
<<<STEP END>>>
<<<STEP 2>>>
FILE: dto.cs
CHANGE: add field
<<<OLD>>>
class Dto {}
<<<NEW>>>
class Dto { int id; }
<<<STEP END>>>
";
        var plan = AgentPlanParsing.ParseDelimitedPlan(raw);
        Assert.NotNull(plan);
        Assert.Equal(2, plan.Plan.Count);
        Assert.Equal("api.cs", plan.Plan[0].File);
        Assert.Equal("dto.cs", plan.Plan[1].File);
        Assert.Contains("void Get(int id) {}", plan.Plan[0].NewString);
    }

    // ─── Post-execution repair-loop churn circuit breaker ────────────────────

    [Fact]
    public void RepairChurnBreaker_TripsAfterMaxConsecutiveZeroChangePasses()
    {
        // Two consecutive passes that change nothing trip the breaker (max = 2).
        var (count1, tripped1) = AgentController.AdvanceRepairChurnBreaker(0, changedFiles: false, maxZeroChangeRepairs: 2);
        Assert.Equal(1, count1);
        Assert.False(tripped1);

        var (count2, tripped2) = AgentController.AdvanceRepairChurnBreaker(count1, changedFiles: false, maxZeroChangeRepairs: 2);
        Assert.Equal(2, count2);
        Assert.True(tripped2);
    }

    [Fact]
    public void RepairChurnBreaker_AnyRealChangeResetsTheCounter()
    {
        // One zero-change pass, then a pass that actually edits a file → counter resets to 0.
        var (count1, tripped1) = AgentController.AdvanceRepairChurnBreaker(0, changedFiles: false, maxZeroChangeRepairs: 2);
        Assert.Equal(1, count1);

        var (count2, tripped2) = AgentController.AdvanceRepairChurnBreaker(count1, changedFiles: true, maxZeroChangeRepairs: 2);
        Assert.Equal(0, count2);
        Assert.False(tripped2);

        // And the reset lets the breaker run the full window again before tripping.
        var (count3, tripped3) = AgentController.AdvanceRepairChurnBreaker(count2, changedFiles: false, maxZeroChangeRepairs: 2);
        Assert.Equal(1, count3);
        Assert.False(tripped3);
    }

    [Fact]
    public void RepairChurnBreaker_ThresholdOneTripsImmediately()
    {
        var (count, tripped) = AgentController.AdvanceRepairChurnBreaker(0, changedFiles: false, maxZeroChangeRepairs: 1);
        Assert.Equal(1, count);
        Assert.True(tripped);
    }

    [Fact]
    public void RepairChurnBreaker_NeverTripsWhileChangesLand()
    {
        var (count, tripped) = AgentController.AdvanceRepairChurnBreaker(0, changedFiles: true, maxZeroChangeRepairs: 2);
        Assert.Equal(0, count);
        Assert.False(tripped);
    }
}
