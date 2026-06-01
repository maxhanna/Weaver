using System.Net;

namespace MaestroBackend.ApiIntegrationTests;

public sealed class TestHttpResponder
{
    public List<HttpRequestMessage> Requests { get; } = new();

    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        };

    public void Reset()
    {
        Requests.Clear();
        Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        };
    }
}
