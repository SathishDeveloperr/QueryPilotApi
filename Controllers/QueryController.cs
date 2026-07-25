using Microsoft.AspNetCore.Mvc;
using QueryPilot.Api.Models;
using QueryPilot.Api.Services;

namespace QueryPilot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class QueryController : ControllerBase
{
    private readonly NlQueryService _service;

    public QueryController(NlQueryService service) => _service = service;

    /// <summary>Ask a question about the sales data in plain English.</summary>
    [HttpPost]
    public async Task<ActionResult<AskResponse>> Ask([FromBody] AskRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest("Question must not be empty.");
        return Ok(await _service.AskAsync(request.Question.Trim(), ct));
    }
}
