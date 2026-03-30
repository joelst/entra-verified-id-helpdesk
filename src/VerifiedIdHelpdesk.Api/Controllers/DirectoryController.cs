using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph;
using CoreConstants = VerifiedIdHelpdesk.Core.Constants;

namespace VerifiedIdHelpdesk.Api.Controllers;

[ApiController]
[Route("api/directory")]
[Authorize]
public class DirectoryController : ControllerBase
{
    private readonly GraphServiceClient _graph;
    private readonly ILogger<DirectoryController> _logger;

    public DirectoryController(GraphServiceClient graph, ILogger<DirectoryController> logger)
    {
        _graph = graph;
        _logger = logger;
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Ok(Array.Empty<object>());

        // SECURITY: Sanitize search input — strip double quotes to prevent Graph search syntax injection.
        var sanitizedQuery = q.Replace("\"", "");

        try
        {
            var users = await _graph.Users.GetAsync(req =>
            {
                req.QueryParameters.Search = $"\"displayName:{sanitizedQuery}\" OR \"mail:{sanitizedQuery}\"";
                req.QueryParameters.Select = ["id", "displayName", "mail", "department", "jobTitle"];
                req.QueryParameters.Top = 10;
                req.QueryParameters.Orderby = ["displayName"];
                req.Headers.Add("ConsistencyLevel", "eventual");
                req.QueryParameters.Count = true;
            });

            var results = users?.Value?.Select(u => new
            {
                entraId = u.Id,
                displayName = u.DisplayName,
                email = u.Mail,
                department = u.Department,
                jobTitle = u.JobTitle
            }) ?? [];

            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Directory search failed for query {Query}", q);
            return StatusCode(500, "Directory search failed.");
        }
    }
}
