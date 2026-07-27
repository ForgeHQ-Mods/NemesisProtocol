using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Dialogue;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Dialog;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services;

namespace ForgeHQ.NemesisProtocol;

[Injectable]
public sealed class NemesisChatBot(
    MailSendService mailSendService,
    NemesisRuntime runtime) : IDialogueChatBot
{
    public const string NetworkId = "660000000000000000000001";
    public const int NetworkAid = 77701337;

    public UserDialogInfo GetChatBot() => new()
    {
        Id = NetworkId,
        Aid = NetworkAid,
        Info = new UserDialogDetails
        {
            Nickname = "Nemesis Network",
            Side = "Usec",
            Level = 60,
            MemberCategory = MemberCategory.Sherpa,
            SelectedMemberCategory = MemberCategory.Sherpa
        }
    };

    public ValueTask<string> HandleMessage(MongoId sessionId, SendMessageRequest request)
    {
        var command = request.Text?.Trim() ?? string.Empty;
        var normalized = command.ToLowerInvariant();

        string response;
        if (normalized.StartsWith("rival ", StringComparison.Ordinal))
        {
            response = runtime.GetRivalDetailText(sessionId, command[6..].Trim());
        }
        else
        {
            response = normalized switch
            {
                "status" or "/status" or "active" or "/active" => runtime.GetStatusText(sessionId),
                "history" or "/history" or "rivals" or "/rivals" => runtime.GetHistoryText(sessionId),
                "compatibility" or "/compatibility" or "compat" or "/compat" => runtime.GetCompatibilityText(),
                "help" or "/help" or "" =>
                    "Commands:\nstatus\nrivals (or history)\nrival <number or name>\ncompatibility (or compat)\nhelp",
                _ => "Unknown command. Send 'help' for the Nemesis Network command list."
            };
        }

        mailSendService.SendUserMessageToPlayer(sessionId, GetChatBot(), response);
        return ValueTask.FromResult(request.DialogId);
    }
}
