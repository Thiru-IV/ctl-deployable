using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;

namespace Cascade.CTL.Agent.Api.Teams;

/// <summary>
/// Bot Framework messaging endpoint. Teams (and the Bot Framework Emulator) POST
/// inbound activities here. The activity is dispatched to <see cref="HitlNotifierBot"/>
/// which captures the user's <see cref="Microsoft.Bot.Schema.ConversationReference"/>
/// for later proactive notifications.
/// </summary>
[ApiController]
[Route("api/messages")]
public sealed class BotMessagesController : ControllerBase
{
    private readonly IBotFrameworkHttpAdapter _adapter;
    private readonly IBot _bot;

    public BotMessagesController(IBotFrameworkHttpAdapter adapter, IBot bot)
    {
        _adapter = adapter;
        _bot = bot;
    }

    [HttpPost]
    public Task PostAsync() => _adapter.ProcessAsync(Request, Response, _bot);
}
