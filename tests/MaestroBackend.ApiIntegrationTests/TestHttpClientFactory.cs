namespace MaestroBackend.ApiIntegrationTests;

public sealed class TestHttpClientFactory(TestHttpResponder responder) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient(new TestHttpMessageHandler(responder), disposeHandler: true);
    }
}
