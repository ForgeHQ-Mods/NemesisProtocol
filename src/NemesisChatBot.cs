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
    public UserDialogInfo GetChatBot() => new()
    {
        Id = "660000000000000000000001",
        Aid = 77701337,
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
        var command = request.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        var response = command switch
        {
            "status" or "/status" => runtime.GetStatusText(sessionId),
            "history" or "/history" => runtime.GetHistoryText(sessionId),
            "compatibility" or "/compatibility" or "compat" or "/compat" => runtime.GetCompatibilityText(),
            "help" or "/help" or "" => "Commands: status, history, compatibility, help",
            _ => "Unknown command. Use: status, history, compatibility, help"
        };

        mailSendService.SendUserMessageToPlayer(sessionId, GetChatBot(), response);
        return ValueTask.FromResult(request.DialogId);
    }
}
