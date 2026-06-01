namespace MaestroBackend.ApiIntegrationTests;

public sealed class TestHttpMessageHandler(TestHttpResponder responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        responder.Requests.Add(CloneRequest(request));
        return Task.FromResult(responder.Responder(request));
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
