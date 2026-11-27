using Hive.Server.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Hive.Server.Hubs;

/// <summary>
/// Real-time state-change notification hub. Group membership is assigned strictly on the
/// server from the authenticated user's identity; no client-callable JoinGroup method exists.
/// </summary>
[Authorize(Policy = HiveAuthPolicies.UserOnly)]
public sealed class StateHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Context.User is not null)
        {
            var actor = Context.User.ToActorIdentity();

            if (!string.IsNullOrWhiteSpace(actor.UserSid))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{actor.UserSid}");
            }

            if (actor.AllSitesAccess)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, "site:*");
            }
            else
            {
                foreach (var siteId in actor.AllowedSiteIds)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, $"site:{siteId}");
                }
            }
        }

        await base.OnConnectedAsync();
    }
}
