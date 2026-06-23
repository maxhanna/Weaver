using Microsoft.AspNetCore.Mvc;
using Weaver.Services;

namespace Weaver.Controllers;

[ApiController]
[Route("api/email")]
public class EmailController : ControllerBase
{
    private readonly EmailService _emailService;
    private readonly ConfigFileService _configFile;

    public EmailController(EmailService emailService, ConfigFileService configFile)
    {
        _emailService = emailService;
        _configFile = configFile;
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestConnection([FromBody] EmailTestRequest request)
    {
        if (request == null)
            return BadRequest(new EmailTestResult { Success = false, Message = "No request body" });

        var result = await _emailService.TestConnectionInlineAsync(
            request.ImapServer ?? "",
            request.ImapPort,
            request.UseSsl,
            request.Username ?? "",
            request.Password ?? "");

        return Ok(result);
    }

    [HttpPost("test-saved")]
    public async Task<IActionResult> TestSavedConnection([FromBody] EmailTestSavedRequest request)
    {
        var result = await _emailService.TestConnectionAsync(request.AccountIndex);
        return Ok(result);
    }
}

public class EmailTestRequest
{
    public string? ImapServer { get; set; }
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public class EmailTestSavedRequest
{
    public int AccountIndex { get; set; }
}
