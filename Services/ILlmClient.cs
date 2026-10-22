using System.Net.Http;

namespace Weaver.Services;

/// <summary>Low-level LLM transport contract used by the agent orchestration layer.</summary>
public interface ILlmClient
{
    Task<string> GetBaseUrlAsync(string? overrideBaseUrl = null);
    Task<string> GetModelAsync(string? overrideModel = null);
    Task<(string baseUrl, string model, string name)> ResolveEndpointAsync(string? endpointId);
    Task PollProgressAsync(string baseUrl, CancellationToken ct);
    Task<(string raw, AgentResponse? parsed, string? error)> CallNonStreamingAsync(
        HttpClient client, string target, string model, object messages,
        CancellationToken ct = default, int? maxTokens = null);
    Task<(string raw, AgentResponse? parsed, string? error)> CallStreamingAsync(
        HttpClient client, string target, string model, object messages,
        CancellationToken ct = default, int? maxTokens = null, bool emitSse = false);
    Task<(string raw, string? error)> CallRawTextOnceAsync(
        string systemPrompt, string userMessage, bool emitSse, CancellationToken ct,
        int? maxTokens = null, bool appendTruncationMarker = false);
}
