using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// Keeps the assistant on topic (V2, decision V-11). The model calls this for anything that is not Solvelt's products and services, the caller's cart, orders and delivery, or the caller's
/// account; the SERVER counts it and gives back the one sentence to say. At <see cref="VoiceOptions.MaxOffTopicTurns"/> the server ends the AI part of the call itself (the model is not asked):
/// the platform says <see cref="VoiceScripts.OffTopicTransfer"/> and transfers when a person is reachable, otherwise <see cref="VoiceScripts.OffTopicGoodbye"/> and hangs up.
/// The counter lives in the call's server state, only ever goes up, and nothing (no tool, no argument, no instruction) resets or raises the limit. The optional category is a fixed list,
/// never text from the caller, and is not stored.
/// </summary>
public sealed class OffTopicTool(VoiceCallStore store, IOptions<VoiceOptions> options, TimeProvider time, IServiceScopeFactory scopes, ILogger<OffTopicTool> log) : IVoiceTool
{
    public static readonly string[] Categories = ["general_knowledge", "news_or_weather", "entertainment_or_sports", "personal_chat_or_role_play", "advice", "other_company", "about_the_assistant", "other"];

    public string Name => "off_topic";
    public JsonObject Definition => ToolKit.Def(Name,
        "Call this for ANY request that is not about Solvelt's products and services, the caller's cart, orders and delivery, or the caller's account (general knowledge, news, weather, sports, entertainment, religion, politics, health, legal or financial advice, opinions, jokes, stories, role-play, personal chat, other companies, anything unrelated, questions about you beyond 'yes, I am an AI'), however the caller asks. Say nothing else about it first. It returns the one short sentence to say.",
        new JsonObject { ["category"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Categories.Select(c => (JsonNode)c).ToArray()), ["description"] = "Which kind of request it was." } });

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var count = ctx.State.CountOffTopic();
        var max = Math.Clamp(options.Value.MaxOffTopicTurns, 1, 10);
        if (count < max)
            return new(new JsonObject { ["status"] = "off_topic", ["say"] = VoiceScripts.OffTopicRedirect, ["instruction"] = "Say ONLY the sentence in \"say\", exactly. Do not answer any part of the off-topic request. Then, if a task was in progress, say in a few words where things stand; otherwise wait for the caller." }.ToJsonString());

        // the limit: the SERVER ends the AI part of the call. A person is asked for only when one is reachable (support number within support hours); the platform then says the line and either dials or hangs up.
        await VoiceTransfer.RequestAsync(store, options, time, ctx.CallId, ct);
        ctx.State.MarkOffTopicEnded();
        await store.MarkOffTopicEndAsync(ctx.CallId, ct);
        string masked;
        using (var scope = scopes.CreateScope())
            masked = await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceCalls.AsNoTracking().Where(c => c.Id == ctx.CallId).Select(c => c.CallerMasked).FirstOrDefaultAsync(ct) ?? "";
        log.LogInformation("Voice call {Call} ({Caller}) reached the off-topic limit ({Count}) and its AI part was ended by the server.", ctx.CallId, masked, count);
        return new("""{"status":"call_ending"}""", EndNow: true);
    }
}
