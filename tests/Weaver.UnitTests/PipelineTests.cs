using Xunit;
using Weaver.Services;
using Weaver;

namespace Weaver.UnitTests;

public class PipelineTests
{
    [Fact]
    public void ClassifyTask_TerminalCommand_ScoresHighOnCommand()
    {
        // Arrange
        var prompt = "ping 8.8.8.8";

        // Act
        var (type, cmdScore, editScore) = AgentUtilities.ClassifyTask(prompt);

        // Assert
        Assert.Equal(PipelineType.CommandExecution, type);
        Assert.True(cmdScore > editScore);
    }

    [Fact]
    public void ClassifyTask_CodeEdit_ScoresHighOnEdit()
    {
        // Arrange
        var prompt = "add a new button to the navbar that alerts 'hello'";

        // Act
        var (type, cmdScore, editScore) = AgentUtilities.ClassifyTask(prompt);

        // Assert
        Assert.Equal(PipelineType.CodeEdit, type);
        Assert.True(editScore > cmdScore);
    }

    [Fact]
    public void ExtractRelevantExcerpt_SmallFile_ReturnsFullContent()
    {
        // Arrange
        var content = "using System;\n\npublic class Test {\n    public void Run() {}\n}";
        var desc = "fix something";

        // Act
        var result = AgentUtilities.ExtractRelevantExcerpt(content, desc, null);

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
        var result = AgentUtilities.GetSkeletonForRange(lines, 0, lines.Length);

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
        var result = AgentUtilities.GetSkeletonForRange(lines, 0, lines.Length);

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
        var result = AgentUtilities.ExtractRelevantExcerpt(content, desc, null);

        // Assert
        Assert.Contains("public class Test", result);
        Assert.Contains("Test", result);
        Assert.Contains("M1", result);
        Assert.Contains("M2", result);
    }

    [Theory]
    [InlineData("delete the file appsettings.json", PipelineType.CommandExecution)]
    [InlineData("show me the logs for the web service", PipelineType.CommandExecution)]
    [InlineData("refactor the login component to use hooks", PipelineType.CodeEdit)]
    [InlineData("fix the padding on the sidebar", PipelineType.CodeEdit)]
    public void ClassifyTask_VariousPrompts_CorrectPipeline(string prompt, PipelineType expected)
    {
        // Act
        var (type, _, _) = AgentUtilities.ClassifyTask(prompt);

        // Assert
        Assert.Equal(expected, type);
    }

    [Fact]
    public void ExtractMeaningfulKeywords_StripsCommonVerbs()
    {
        // Arrange
        var prompt = "Please add a new button to the user dashboard";

        // Act
        var keywords = AgentUtilities.ExtractMeaningfulKeywords(prompt.ToLowerInvariant());

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
        var result = AgentUtilities.GetSkeletonForRange(lines, 0, lines.Length);

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
        var result = AgentUtilities.ExtractRelevantExcerpt(content, "fix the SecretFunction", null);

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
        var result = AgentUtilities.ExtractRelevantExcerpt(content, "fix target", planOld);

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
        var result = AgentUtilities.TryRepairTruncatedPlanJson(truncated);
        Assert.NotNull(result);
        Assert.Contains("}]}", result);
    }

    [Fact]
    public void EstimateTokens_ReturnsApproximateCount()
    {
        var text = "Hello world"; // 11 chars
        var result = AgentUtilities.EstimateTokens(text);
        Assert.Equal(2, result); // 11 / 4 = 2.75 -> 2
    }

    [Theory]
    [InlineData("move file.txt to sub/file.txt", "file.txt", "sub/file.txt")]
    [InlineData("rename current.cs → renamed.cs", "current.cs", "renamed.cs")]
    public void ExtractTargetPath_HandlesArrowsAndTo(string desc, string current, string expected)
    {
        var result = AgentUtilities.ExtractTargetPath(desc, current, "/");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryDetectSimpleIntent_Delete_IdentifiesTarget()
    {
        // Act
        var plan = AgentUtilities.TryDetectSimpleIntent("delete the file src/temp.log");

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
        var result = AgentUtilities.GetSkeletonForRange(lines, 0, lines.Length);

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
        var result = AgentUtilities.GetSkeletonForRange(lines, 0, lines.Length);

        // Assert
        Assert.Contains("func", result);
        Assert.Contains("main", result);
    }

    [Fact]
    public void ExtractEditFromCodeGen_RobustParsing()
    {
        // Case 1: Markdown + JSON
        var raw1 = "Sure, here is the edit:\n```json\n{\n  \"oldString\": \"public void Old()\",\n  \"newString\": \"public void New()\"\n}\n```";
        var (os1, ns1, err1) = AgentUtilities.ExtractEditFromCodeGen(raw1);
        Assert.Null(err1);
        Assert.Equal("public void Old()", os1);
        Assert.Equal("public void New()", ns1);

        // Case 2: Malformed JSON (unquoted keys)
        var raw2 = "{ oldString: \"old\", newString: \"new\" }";
        var (os2, ns2, err2) = AgentUtilities.ExtractEditFromCodeGen(raw2);
        Assert.Null(err2);
        Assert.Equal("old", os2);
        Assert.Equal("new", ns2);
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
        var plan = AgentUtilities.ParseDelimitedPlan(raw);
        Assert.NotNull(plan);
        Assert.Equal(2, plan.Plan.Count);
        Assert.Equal("api.cs", plan.Plan[0].File);
        Assert.Equal("dto.cs", plan.Plan[1].File);
        Assert.Contains("void Get(int id) {}", plan.Plan[0].NewString);
    }

    [Fact]
    public void ClassifyTask_WeightedScoring_AmbiguousPrompts()
    {
        // Prompt with both command keywords (fetch, data) and edit keywords (update, component)
        var prompt = "fetch the latest weather data and update the WeatherComponent with the new values";
        
        var (type, cmdScore, editScore) = AgentUtilities.ClassifyTask(prompt);
        
        // Should favor CodeEdit because of 'component' and 'update' which are strong edit signals
        Assert.Equal(PipelineType.CodeEdit, type);
        Assert.True(editScore > 0);
    }
}
