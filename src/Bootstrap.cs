using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;

namespace ForgeHQ.NemesisProtocol;

[Injectable(TypePriority = OnLoadOrder.PreSptModLoader)]
public sealed class NemesisBootstrap(
    ISptLogger<NemesisBootstrap> logger,
    NemesisRuntime runtime) : IOnLoad
{
    public Task OnLoad()
    {
        runtime.Initialize();
        new StartLocalRaidPatch().Enable();
        new GenerateNemesisCandidatePatch().Enable();
        new EndLocalRaidPatch().Enable();
        logger.Success("[Nemesis Protocol] Patches enabled for SPT 4.0.x.");
        return Task.CompletedTask;
    }
}
