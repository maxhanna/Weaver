using Microsoft.AspNetCore.Mvc;
using MaestroBackend.Services;

[ApiController]
[Route("api/terminal")]
public class TerminalController : ControllerBase
{
    private readonly TerminalService _terminal;

    public TerminalController(TerminalService terminal) => _terminal = terminal;

    [HttpPost("start")]
    public IActionResult Start()
    {
        _terminal.Start();
        return Ok(new { running = true });
    }

    public class ExecRequest { public string command { get; set; } = ""; }

    [HttpPost("exec")]
    public async Task<IActionResult> Exec([FromBody] ExecRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.command)) return BadRequest("command required");
        await _terminal.SendCommandAsync(req.command);
        await Task.Delay(100);
        return Ok(new { output = _terminal.ReadLastLines(200) });
    }

    [HttpGet("output")]
    public IActionResult Output([FromQuery]int lines = 200)
    {
        return Ok(new { output = _terminal.ReadLastLines(lines) });
    }
}
