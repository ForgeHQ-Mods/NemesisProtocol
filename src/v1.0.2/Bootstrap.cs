using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Dialogue;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace ForgeHQ.NemesisProtocol;

#pragma warning disable CS0618 // ConfigServer is required to mutate SPT 4.0.x's active chatbot configuration.
[Injectable(TypePriority = OnLoadOrder.PreSptModLoader)]
public sealed class NemesisBootstrap(
    ISptLogger<NemesisBootstrap> logger,
    NemesisRuntime runtime,
    ConfigServer configServer,
    DialogueController dialogueController,
    IEnumerable<IDialogueChatBot> dialogueChatBots,
    NemesisChatBot nemesisChatBot) : IOnLoad
{
#pragma warning restore CS0618
    public Task OnLoad()
    {
        logger.Success("[Nemesis Protocol] v1.0.2 bootstrap loaded.");

        runtime.Initialize();
        ConfigureNemesisNetwork();

        new StartLocalRaidPatch().Enable();
        new GenerateNemesisCandidatePatch().Enable();
        new PrioritizeNemesisWavePatch().Enable();
        new EndLocalRaidPatch().Enable();
        logger.Success("[Nemesis Protocol] Patches enabled for SPT 4.0.x.");
        return Task.CompletedTask;
    }

    private void ConfigureNemesisNetwork()
    {
        logger.Info("[Nemesis Protocol] Nemesis Network setup starting.");

        if (!runtime.Config.EnableNemesisNetwork)
        {
            logger.Warning("[Nemesis Protocol] Nemesis Network is disabled in config.jsonc.");
            return;
        }

        try
        {
#pragma warning disable CS0618 // SPT 4.0.x compatibility. Replace with direct CoreConfig injection when targeting SPT 4.1+.
            var coreConfig = configServer.GetConfig<CoreConfig>();
#pragma warning restore CS0618

            var networkId = new MongoId(NemesisChatBot.NetworkId);
            var chatbotFeatures = coreConfig.Features.ChatbotFeatures;

            chatbotFeatures.Ids["nemesisNetwork"] = networkId;
            chatbotFeatures.EnabledBots[networkId] = true;
            logger.Success($"[Nemesis Protocol] Nemesis Network enabled in Messenger. Contact ID: {networkId}.");

            var discoveredByDi = dialogueChatBots.Any(chatBot => chatBot.GetChatBot().Id == networkId);
            if (discoveredByDi)
            {
                logger.Success("[Nemesis Protocol] Nemesis Network chatbot discovered by SPT dependency injection.");
            }
            else
            {
                dialogueController.RegisterChatBot(nemesisChatBot);
                logger.Success("[Nemesis Protocol] Nemesis Network chatbot registered manually.");
            }

            logger.Success("[Nemesis Protocol] Nemesis Network READY. Commands: status, rivals, history, rival <name>, compatibility, help.");
        }
        catch (Exception exception)
        {
            logger.Error($"[Nemesis Protocol] Nemesis Network setup FAILED: {exception}");
        }
    }
}
