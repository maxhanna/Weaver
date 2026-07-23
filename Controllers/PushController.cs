using Microsoft.AspNetCore.Mvc;
using WebPush;
using Weaver.Services;

namespace Weaver.Controllers;

[ApiController]
[Route("api/push")]
public class PushController : ControllerBase
{
    private readonly PushNotificationService _push;

    public PushController(PushNotificationService push) => _push = push;

    [HttpGet("vapid-public-key")]
    public IActionResult GetVapidPublicKey()
    {
        return Ok(_push.GetVapidPublicKey());
    }

    [HttpPost("subscribe")]
    public IActionResult Subscribe([FromBody] PushSubscriptionDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Endpoint)) return BadRequest();
        var sub = new PushSubscription(dto.Endpoint, dto.Keys?.P256dh, dto.Keys?.Auth);
        _push.SetSubscription(sub);
        return Ok();
    }
}

public class PushSubscriptionDto
{
    public string Endpoint { get; set; } = "";
    public PushSubscriptionKeysDto? Keys { get; set; }
}

public class PushSubscriptionKeysDto
{
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
}
