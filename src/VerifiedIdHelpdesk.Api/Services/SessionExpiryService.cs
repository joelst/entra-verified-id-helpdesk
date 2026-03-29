using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VerifiedIdHelpdesk.Core.Interfaces;

namespace VerifiedIdHelpdesk.Api.Services;

public class SessionExpiryService : BackgroundService
{
    private readonly ISessionStore _sessionStore;
    private readonly ILogger<SessionExpiryService> _logger;

    public SessionExpiryService(ISessionStore sessionStore, ILogger<SessionExpiryService> logger)
    {
        _sessionStore = sessionStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _sessionStore.ExpireOldSessionsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during session expiry sweep");
            }

            await Task.Delay(TimeSpan.FromMinutes(2), ct);
        }
    }
}
