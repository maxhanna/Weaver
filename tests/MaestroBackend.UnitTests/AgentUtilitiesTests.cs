using MaestroBackend.Services;

namespace MaestroBackend.UnitTests;

public class AgentUtilitiesTests
{
    [Theory]
    [InlineData("rename src/app.js to main.js", PipelineType.CommandExecution)]
    [InlineData("check if localhost is reachable", PipelineType.CommandExecution)]
    [InlineData("add a save button to the settings panel", PipelineType.CodeEdit)]
    [InlineData("scan network for devices", PipelineType.CommandExecution)]
    [InlineData("docker compose up", PipelineType.CommandExecution)]
    [InlineData("copy appsettings.json backup.json", PipelineType.CommandExecution)]
    [InlineData("start service nginx", PipelineType.CommandExecution)]
    [InlineData("cat Program.cs", PipelineType.CommandExecution)]
    [InlineData("get latest package versions", PipelineType.CommandExecution)]
    [InlineData("check my email inbox", PipelineType.CommandExecution)]
    [InlineData("update navbar styles to match dark mode", PipelineType.CodeEdit)]
    [InlineData("fix null reference in ConfigController", PipelineType.CodeEdit)]
    public void ClassifyTask_returns_expected_pipeline(string prompt, PipelineType expected)
    {
        var actual = AgentUtilities.ClassifyTask(prompt);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("implement a login form", true)]
    [InlineData("remove deprecated endpoint", true)]
    [InlineData("check whether host is up", false)]
    [InlineData("verify service health endpoint", false)]
    public void TaskExpectsFileChanges_returns_expected_result(string prompt, bool expected)
    {
        var actual = AgentUtilities.TaskExpectsFileChanges(prompt);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryDetectSimpleIntent_inherits_source_directory_for_bare_rename_target()
    {
        var plan = AgentUtilities.TryDetectSimpleIntent("rename src/app.js to main.js");

        Assert.NotNull(plan);
        Assert.Single(plan!.Plan);
        Assert.Equal("_rename", plan.Plan[0].File);
        Assert.Equal("src/app.js → src/main.js", plan.Plan[0].Change);
    }

    [Theory]
    [InlineData("delete file src/obsolete.cs", "_delete_file", "src/obsolete.cs")]
    [InlineData("git pull latest", "_git", "pull all changes")]
    [InlineData("git commit \"ship it\"", "_git", "commit all changes with message \"ship it\"")]
    [InlineData("git push origin", "_git", "sync with remote (pull then push)")]
    [InlineData("git revert all", "_git", "revert all changes")]
    [InlineData("ping localhost", "_ping", "ping localhost")]
    [InlineData("npm install left-pad", "_package_install", "npm install left-pad")]
    public void TryDetectSimpleIntent_returns_expected_marker_step(string prompt, string expectedFile, string expectedChangeContains)
    {
        var plan = AgentUtilities.TryDetectSimpleIntent(prompt);

        Assert.NotNull(plan);
        Assert.NotEmpty(plan!.Plan);
        Assert.Equal(expectedFile, plan.Plan[0].File);
        Assert.Contains(expectedChangeContains, plan.Plan[0].Change);
    }

    [Fact]
    public void TryDetectSimpleIntent_returns_null_for_non_simple_prompt()
    {
        var plan = AgentUtilities.TryDetectSimpleIntent("refactor AgentController to use a new execution pipeline");
        Assert.Null(plan);
    }

    [Fact]
    public void ParsePlan_accepts_markdown_wrapped_json()
    {
        const string raw = """
            Here is the plan:

            ```json
            {
              "thinking": "Update the controller.",
              "summary": "Change one file.",
              "plan": [
                {
                  "file": "Controllers/ConfigController.cs",
                  "change": "Adjust save behavior.",
                  "priority": 1
                }
              ]
            }
            ```
            """;

        var plan = AgentUtilities.ParsePlan(raw);

        Assert.NotNull(plan);
        Assert.Equal("Change one file.", plan!.Summary);
        Assert.Single(plan.Plan);
        Assert.Equal("Controllers/ConfigController.cs", plan.Plan[0].File);
    }

    [Fact]
    public void ParsePlan_repairs_unquoted_json_keys()
    {
        const string raw = """
            {
              thinking: "Repair malformed JSON",
              summary: "Still parse the plan",
              plan: [
                {
                  file: "Program.cs",
                  change: "Add a test seam",
                  priority: 1
                }
              ]
            }
            """;

        var plan = AgentUtilities.ParsePlan(raw);

        Assert.NotNull(plan);
        Assert.Equal("Still parse the plan", plan!.Summary);
        Assert.Single(plan.Plan);
        Assert.Equal("Program.cs", plan.Plan[0].File);
    }

    [Fact]
    public void ParsePlan_returns_null_for_non_json_text()
    {
        var plan = AgentUtilities.ParsePlan("this is not valid json and has no object body");
        Assert.Null(plan);
    }

    [Theory]
    [InlineData("_git", true)]
    [InlineData("_rename", true)]
    [InlineData("_create_file", true)]
    [InlineData("Program.cs", false)]
    public void IsSpecialMarker_recognizes_control_markers(string file, bool expected)
    {
        var actual = AgentUtilities.IsSpecialMarker(file);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("HomeController.cs", "Controllers/")]
    [InlineData("TerminalService.cs", "Services/")]
    [InlineData("README.md", "Docs/")]
    [InlineData("wwwroot/app.js", "wwwroot/")]
    [InlineData("appsettings.json", "")]
    public void InferTargetFolder_returns_expected_folder(string fileName, string expected)
    {
        var actual = AgentUtilities.InferTargetFolder(fileName, Path.GetTempPath());
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsPathUnderRoot_blocks_prefix_escape_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "ws");
        var allowed = Path.Combine(root, "src", "Program.cs");
        var escaped = root + "-evil\\Program.cs";

        Assert.True(AgentUtilities.IsPathUnderRoot(allowed, root));
        Assert.False(AgentUtilities.IsPathUnderRoot(escaped, root));
    }

    [Theory]
    [InlineData("written", "done")]
    [InlineData("ok", "done")]
    [InlineData("running", "running")]
    [InlineData("whatever", "whatever")]
    public void NormalizeUiStatus_maps_known_statuses(string status, string expected)
    {
        var actual = AgentUtilities.NormalizeUiStatus(status);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BatchWasExplorationOnly_requires_non_empty_all_exploration_types()
    {
        var exploration = new List<AgentStep>
        {
            new() { Type = "read" },
            new() { Type = "grep" }
        };
        var mixed = new List<AgentStep>
        {
            new() { Type = "read" },
            new() { Type = "edit" }
        };

        Assert.True(AgentUtilities.BatchWasExplorationOnly(exploration));
        Assert.False(AgentUtilities.BatchWasExplorationOnly(mixed));
        Assert.False(AgentUtilities.BatchWasExplorationOnly(new List<AgentStep>()));
    }

    [Fact]
    public void FindSimilarFiles_finds_matches_by_filename()
    {
        var root = Path.Combine(Path.GetTempPath(), "maestro-find-similar", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var src = Path.Combine(root, "src");
            var ignored = Path.Combine(root, "node_modules", "pkg");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(ignored);

            File.WriteAllText(Path.Combine(src, "ConfigController.cs"), "// src");
            File.WriteAllText(Path.Combine(ignored, "ConfigController.cs"), "// ignored");

            var found = AgentUtilities.FindSimilarFiles("Controllers/ConfigController.cs", root);

            Assert.Contains("src/ConfigController.cs", found);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("rename src/app.js to main.js", "src/app.js", "D:\\ws", "src/main.js")]
    [InlineData("rename file to src/new/app.js", "src/app.js", "D:\\ws", "src/new/app.js")]
    [InlineData("rename file", "src/app.js", "D:\\ws", null)]
    public void ExtractTargetPath_handles_relative_absolute_and_invalid_cases(
        string change, string currentPath, string root, string? expected)
    {
        var actual = AgentUtilities.ExtractTargetPath(change, currentPath, root);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("src/app.js", true)]
    [InlineData("_git", false)]
    [InlineData("C:\\temp\\file.txt", false)]
    [InlineData("", false)]
    public void IsRelativePath_handles_markers_and_rooted_paths(string path, bool expected)
    {
        var actual = AgentUtilities.IsRelativePath(path);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExtractJsonBlocks_extracts_multiple_objects()
    {
        const string input = """prefix {"a":1} middle {"b":{"c":2}} suffix""";
        var blocks = AgentUtilities.ExtractJsonBlocks(input);
        Assert.Equal(2, blocks.Count);
        Assert.Equal("""{"a":1}""", blocks[0]);
        Assert.Equal("""{"b":{"c":2}}""", blocks[1]);
    }

    [Fact]
    public void TryParseReviewResponse_parses_malformed_unquoted_keys()
    {
        const string raw = """{complete: true, feedback: "looks good"}""";
        var (complete, feedback) = AgentUtilities.TryParseReviewResponse(raw);
        Assert.True(complete);
        Assert.Equal("looks good", feedback);
    }

    [Fact]
    public void GetReviewJsonCandidates_includes_repaired_candidate_for_unquoted_keys()
    {
        const string raw = """{complete: true, feedback: "ok"}""";
        var candidates = AgentUtilities.GetReviewJsonCandidates(raw).ToList();
        Assert.Contains(candidates, c => c.Contains("\"complete\"", StringComparison.Ordinal));
        Assert.Contains(candidates, c => c.Contains("\"feedback\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractPhoneNumbers_normalizes_and_deduplicates_matches()
    {
        const string text = "Call 555-123-4567 or (555) 123-4567 or +1 555 123 4567";
        var phones = AgentUtilities.ExtractPhoneNumbers(text);
        Assert.Contains("5551234567", phones);
        Assert.Contains("+15551234567", phones);
        Assert.Equal(2, phones.Count);
    }

    [Fact]
    public void ExtractMeaningfulKeywords_filters_stopwords_and_verbs()
    {
        var keywords = AgentUtilities.ExtractMeaningfulKeywords("make the dashboard layout spacing more compact with navbar branding");
        Assert.Contains("dashboard", keywords);
        Assert.Contains("layout", keywords);
        Assert.Contains("spacing", keywords);
        Assert.DoesNotContain("make", keywords);
        Assert.DoesNotContain("more", keywords);
    }

    [Fact]
    public void ApplyTaskTypeHeuristics_prioritizes_css_for_style_tasks()
    {
        var files = new List<string>
        {
            "wwwroot/styles/site.css",
            "wwwroot/app.js",
            "Controllers/AgentController.cs"
        };

        var ranked = AgentUtilities.ApplyTaskTypeHeuristics("update button color and spacing", files);
        Assert.NotEmpty(ranked);
        Assert.Equal("wwwroot/styles/site.css", ranked[0]);
    }

    [Fact]
    public void BuildDiffPreview_marks_removed_and_added_lines()
    {
        var diff = AgentUtilities.BuildDiffPreview("a\nb", "a\nc");
        Assert.Contains("- b", diff);
        Assert.Contains("+ c", diff);
    }

    [Fact]
    public void HasSuccessfulEdits_counts_rename_as_successful_edit()
    {
        var steps = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "rename", ["status"] = "done" }
        };
        Assert.True(AgentUtilities.HasSuccessfulEdits(steps));
    }

    [Fact]
    public void ExtractEditFromCodeGen_parses_markdown_wrapped_json()
    {
        const string raw = """
```json
{"oldString":"old value","newString":"new value"}
```
""";
        var (oldString, newString, error) = AgentUtilities.ExtractEditFromCodeGen(raw);
        Assert.Equal("old value", oldString);
        Assert.Equal("new value", newString);
        Assert.Null(error);
    }

    [Fact]
    public void ExtractEditPairs_recovers_from_unquoted_newString_key_variant()
    {
        const string raw = """{"oldString":"hello",newString":"world"}""";
        var edits = AgentUtilities.ExtractEditPairs(raw, "Program.cs");
        Assert.Single(edits);
        Assert.Equal("hello", edits[0].OldString);
        Assert.Equal("world", edits[0].NewString);
        Assert.Equal("Program.cs", edits[0].Path);
    }
}
