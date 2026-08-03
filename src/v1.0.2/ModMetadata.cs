using SPTarkov.Server.Core.Models.Spt.Mod;

namespace ForgeHQ.NemesisProtocol;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.forgehq.nemesisprotocol";
    public override string Name { get; init; } = "Nemesis Protocol";
    public override string Author { get; init; } = "ForgeHQ Labs";
    public override List<string>? Contributors { get; init; } = ["OpenAI"];
    public override SemanticVersioning.Version Version { get; init; } = new("1.0.2");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}
