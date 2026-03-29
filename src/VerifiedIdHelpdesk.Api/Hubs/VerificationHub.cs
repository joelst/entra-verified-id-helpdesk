using Microsoft.AspNetCore.SignalR;

namespace VerifiedIdHelpdesk.Api.Hubs;

public class VerificationHub : Hub
{
    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }
}
