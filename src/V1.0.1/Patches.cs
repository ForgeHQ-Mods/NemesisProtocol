using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Services;

namespace ForgeHQ.NemesisProtocol;

public sealed class StartLocalRaidPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.StartLocalRaid))
        ?? throw new MissingMethodException(nameof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaid));

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, StartLocalRaidRequestData request)
    {
        NemesisRuntime.Current?.OnRaidStarted(sessionId, request);
    }
}

public sealed class GenerateNemesisCandidatePatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotGenerator).GetMethod(nameof(BotGenerator.PrepareAndGenerateBot))
        ?? throw new MissingMethodException(nameof(BotGenerator), nameof(BotGenerator.PrepareAndGenerateBot));

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, BotGenerationDetails botGenerationDetails, out CandidateReservation __state)
    {
        __state = NemesisRuntime.Current?.TryReserveCandidate(sessionId, botGenerationDetails) ?? default;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId, CandidateReservation __state, BotBase __result)
    {
        NemesisRuntime.Current?.RecordGeneratedPmc(sessionId, __result);
        NemesisRuntime.Current?.TransformReservedCandidate(sessionId, __state, __result);
    }
}

public sealed class EndLocalRaidPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.EndLocalRaid))
        ?? throw new MissingMethodException(nameof(LocationLifecycleService), nameof(LocationLifecycleService.EndLocalRaid));

    // Prefix is deliberate: SPT clears MatchBotDetailsCacheService at the start of EndLocalRaid.
    [PatchPrefix]
    public static void Prefix(MongoId sessionId, EndLocalRaidRequestData request)
    {
        NemesisRuntime.Current?.OnRaidEnded(sessionId, request);
    }
}
