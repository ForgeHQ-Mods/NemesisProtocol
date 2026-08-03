using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace ForgeHQ.NemesisProtocol;

[Injectable(InjectionType.Scoped, TypePriority = int.MaxValue)]
public sealed class NemesisClientRouter : StaticRouter
{
    public NemesisClientRouter(
        JsonUtil jsonUtil,
        HttpResponseUtil httpResponseUtil,
        NemesisRuntime runtime)
        : base(
            jsonUtil,
            [
                new RouteAction<EmptyRequestData>(
                    "/nemesis/client/prepared",
                    (url, request, sessionId, output) =>
                        ValueTask.FromResult(httpResponseUtil.NoBody(runtime.GetPreparedClientState(sessionId)))),
                new RouteAction<NemesisClientConfirmationRequest>(
                    "/nemesis/client/confirm",
                    (url, request, sessionId, output) =>
                    {
                        runtime.RecordClientConfirmation(sessionId, request);
                        return ValueTask.FromResult(httpResponseUtil.NullResponse());
                    })
            ])
    {
    }
}
