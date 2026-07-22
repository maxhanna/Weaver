using System.Text.Json;

namespace Weaver.Services;

public static class EditIntentClassifier
{
    public static async Task<EditIntent> ClassifyAsync(
        string changeDescription,
        string relPath,
        Func<string, string, CancellationToken, Task<(string raw, string? error)>> llmCaller,
        CancellationToken ct)
    {
        var sys =
            "Classify a single code-edit instruction. Output ONLY JSON: " +
            "{\"kind\":\"replace_symbol\"|\"insert_near_symbol\"|\"add_property\"|\"targeted_edit\"," +
            "\"symbol\":\"ExactSymbolName or null\",\"preferredKind\":\"method\"|\"class\"|\"property\"|null}\n" +
            "- replace_symbol: the instruction modifies/rewrites the body of an EXISTING method, class, or property. symbol = that name.\n" +
            "- insert_near_symbol: the instruction ADDS a brand-new method/function. symbol = the name of an EXISTING method to anchor near (e.g. the last method in the class), or null if unknown.\n" +
            "- add_property: the instruction adds one or more fields/properties to an existing class.\n" +
            "- targeted_edit: none of the above cleanly apply (small localized text change, config value, single line tweak).\n" +
            "Only set preferredKind if the instruction explicitly says 'class' or 'property' rather than 'method'.";

        var user = $"File: {relPath}\nInstruction: {changeDescription}";

        var (raw, err) = await llmCaller(sys, user, ct);
        if (string.IsNullOrWhiteSpace(raw) || err != null)
            return new EditIntent(EditIntentKind.TargetedEdit, null, null);

        try
        {
            var cleaned = AgentUtilities.ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var kindStr = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
            var symbol = root.TryGetProperty("symbol", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : null;
            var preferredKind = root.TryGetProperty("preferredKind", out var pk) && pk.ValueKind == JsonValueKind.String
                ? pk.GetString() : null;

            var kind = kindStr switch
            {
                "replace_symbol" => EditIntentKind.ReplaceSymbol,
                "insert_near_symbol" => EditIntentKind.InsertNearSymbol,
                "add_property" => EditIntentKind.AddProperty,
                _ => EditIntentKind.TargetedEdit
            };

            return new EditIntent(kind, symbol, preferredKind);
        }
        catch
        {
            return new EditIntent(EditIntentKind.TargetedEdit, null, null);
        }
    }
}