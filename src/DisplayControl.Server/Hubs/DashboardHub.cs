using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DisplayControl.Server.Hubs;

[Authorize]
public sealed class DashboardHub : Hub;
