using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;

namespace TestServer.Controllers;

/// <summary>
/// Some non functional requirements via Http
/// </summary>
[ApiController]
public class PingController : ControllerBase
{
    private readonly ILogger<PingController> _logger;

    public PingController(ILogger<PingController> logger)
    {
        _logger = logger;
    }

    [HttpGet("ping")]
    public ActionResult Ping()
    {
        return Ok();
    }

    /// <summary>
    /// Stops the http server
    /// </summary>
    [HttpGet("kill")]
    [HttpPost("kill")]
    public ActionResult Kill()
    {
        Program.App.StopAsync();
        return Ok();
    }
}
