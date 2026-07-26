using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace ForgeHQ.NemesisProtocol;

public sealed record NemesisCompatibilityConfig
{
    public bool UseApbsProgression { get; init; } = true;
    public bool PreserveOrbitPmcRole { get; init; } = true;
    public bool ExactLevelFallbackWithoutApbs { get; init; } = true;
    public bool LogDetection { get; init; } = true;
}

public sealed record NemesisConfig
{
    public bool Enabled { get; init; } = true;
    public double SpawnChancePercent { get; init; } = 42.0;
    public int MinimumRaidsBetweenAppearances { get; init; } = 1;
    public int MaximumBonusLevels { get; init; } = 16;
    public int LevelGainPerPlayerKill { get; init; } = 3;
    public int LevelGainPerEscape { get; init; } = 1;
    public double HealthBonusPerRank { get; init; } = 0.08;
    public bool RequireMatchingFaction { get; init; } = true;
    public bool SendMessages { get; init; } = true;
    public bool SendEscapeMessages { get; init; } = true;
    public string NameSuffix { get; init; } = " [NEMESIS]";
    public NemesisCompatibilityConfig Compatibility { get; init; } = new();
    public List<string> DisabledMaps { get; init; } = ["hideout", "develop"];
    public List<string> CreationMessages { get; init; } =
    [
        "You know my name now. I know yours too.",
        "That death was an introduction. The next one will be personal.",
        "You should have stayed out of my sector. I'll be seeing you again."
    ];
    public List<string> KillMessages { get; init; } =
    [
        "Again. You're making this easy.",
        "I told you this wasn't over.",
        "Another tag for the collection. Gear up. Come find me."
    ];
    public List<string> EscapeMessages { get; init; } =
    [
        "You were close. Close doesn't count.",
        "I walked out. Did you really think that was the end?",
        "Same map, same mistake. Next time one of us stays behind."
    ];
    public List<string> DeathMessages { get; init; } =
    [
        "Debt paid. Don't expect the next hunter to be as careless.",
        "You finally got me. Keep the tag.",
        "So that's how it ends. Enjoy the silence while it lasts."
    ];
}

public sealed record NemesisCompatibilityStatus
{
    public bool ApbsDetected { get; init; }
    public bool OrbitDetected { get; init; }
    public string ApbsAuthority { get; init; } = "SPT";
    public string BehaviorAuthority { get; init; } = "SPT/installed AI mods";
}

public sealed record NemesisProfileState
{
    public int SchemaVersion { get; set; } = 2;
    public int TotalPmcRaids { get; set; }
    public string? ActiveRivalId { get; set; }
    public List<NemesisRival> Rivals { get; set; } = [];
}

public sealed record NemesisRival
{
    public string RivalId { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalName { get; set; } = "Unknown";
    public string DisplayName { get; set; } = "Unknown [NEMESIS]";
    public string Faction { get; set; } = "Usec";
    public int BaseLevel { get; set; } = 1;
    public int LevelBonus { get; set; }
    public int LastRequestedLevel { get; set; } = 1;
    public int LastGeneratedLevel { get; set; } = 1;
    public int PlayerKills { get; set; }
    public int Escapes { get; set; }
    public int Encounters { get; set; }
    public int TimesSpawned { get; set; }
    public string Rank { get; set; } = "Initiate";
    public string LastLoadoutAuthority { get; set; } = "SPT";
    public string? LastApbsTier { get; set; }
    public string LastBehaviorAuthority { get; set; } = "SPT/installed AI mods";
    public string? PrimaryWeaponTemplateId { get; set; }
    public Customization? Appearance { get; set; }
    public string? LastSeenMap { get; set; }
    public int LastAppearedRaidNumber { get; set; } = -999;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastEncounterAtUtc { get; set; }
    public DateTimeOffset? DefeatedAtUtc { get; set; }
    public bool IsDefeated { get; set; }
}

public sealed class RaidNemesisState
{
    [JsonIgnore]
    public object Gate { get; } = new();

    public string SessionKey { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public bool IsPmcRaid { get; init; }
    public bool Scheduled { get; set; }
    public bool CandidateReserved { get; set; }
    public bool Transformed { get; set; }
    public string? RivalId { get; set; }
    public string? SpawnedBotId { get; set; }
}

public readonly record struct CandidateReservation(
    bool Reserved,
    string? RivalId,
    int RequestedLevel,
    string? OriginalRole,
    string? OriginalSide,
    bool ApbsActive,
    bool OrbitActive);

public sealed record GeneratedPmcSnapshot
{
    public string BotId { get; init; } = string.Empty;
    public string Name { get; init; } = "Unknown";
    public string Faction { get; init; } = "Usec";
    public int Level { get; init; } = 1;
    public string? ApbsTier { get; init; }
    public string? PrimaryWeaponTemplateId { get; init; }
    public Customization? Appearance { get; init; }
}
