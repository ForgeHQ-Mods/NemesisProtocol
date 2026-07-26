using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using IOPath = System.IO.Path;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Cloners;

namespace ForgeHQ.NemesisProtocol;

[Injectable(InjectionType.Singleton)]
public sealed class NemesisRuntime(
    ISptLogger<NemesisRuntime> logger,
    ModHelper modHelper,
    MailSendService mailSendService,
    MatchBotDetailsCacheService botCache,
    ICloner cloner)
{
    private readonly ConcurrentDictionary<string, NemesisProfileState> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RaidNemesisState> _raids = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _profileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GeneratedPmcSnapshot> _generatedPmcs = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private string _modPath = string.Empty;
    private string _profileDataPath = string.Empty;
    private readonly Random _random = new();

    public static NemesisRuntime? Current { get; private set; }
    public NemesisConfig Config { get; private set; } = new();
    public NemesisCompatibilityStatus Compatibility { get; private set; } = new();

    public void Initialize()
    {
        Current = this;
        _modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        _profileDataPath = IOPath.Combine(_modPath, "data", "profiles");
        Directory.CreateDirectory(_profileDataPath);

        try
        {
            Config = modHelper.GetJsonDataFromFile<NemesisConfig>(_modPath, "config.jsonc") ?? new NemesisConfig();
        }
        catch (Exception exception)
        {
            logger.Error($"[Nemesis Protocol] Failed to read config.jsonc, using defaults: {exception.Message}");
            Config = new NemesisConfig();
        }

        Compatibility = DetectCompatibility();
        logger.Success($"[Nemesis Protocol] Runtime initialized. Spawn chance: {Config.SpawnChancePercent:0.#}%");
        if (Config.Compatibility.LogDetection)
        {
            logger.Info($"[Nemesis Protocol] APBS integration: {(Compatibility.ApbsDetected && Config.Compatibility.UseApbsProgression ? "ACTIVE" : "not active")} — loadout/actual level authority: {Compatibility.ApbsAuthority}.");
            logger.Info($"[Nemesis Protocol] ORBIT compatibility: {(Compatibility.OrbitDetected && Config.Compatibility.PreserveOrbitPmcRole ? "ACTIVE" : "passive/default")} — behavior authority: {Compatibility.BehaviorAuthority}.");
        }
    }

    public void OnRaidStarted(MongoId sessionId, StartLocalRaidRequestData request)
    {
        if (!Config.Enabled)
        {
            return;
        }

        var sessionKey = SessionKey(sessionId);
        var location = request.Location?.Trim().ToLowerInvariant() ?? "unknown";
        var isPmcRaid = string.Equals(request.PlayerSide, "pmc", StringComparison.OrdinalIgnoreCase);
        var state = new RaidNemesisState
        {
            SessionKey = sessionKey,
            Location = location,
            IsPmcRaid = isPmcRaid
        };

        if (!isPmcRaid || Config.DisabledMaps.Contains(location, StringComparer.OrdinalIgnoreCase))
        {
            _raids[sessionKey] = state;
            return;
        }

        var profile = GetProfile(sessionId);
        var rival = GetActiveRival(profile);
        if (rival is null)
        {
            _raids[sessionKey] = state;
            return;
        }

        var raidsSinceAppearance = profile.TotalPmcRaids - rival.LastAppearedRaidNumber;
        var eligible = raidsSinceAppearance > Config.MinimumRaidsBetweenAppearances;
        state.Scheduled = eligible && NextChance(Config.SpawnChancePercent);
        state.RivalId = rival.RivalId;
        _raids[sessionKey] = state;

        if (state.Scheduled)
        {
            logger.Info($"[Nemesis Protocol] {rival.DisplayName} is eligible to enter {location}.");
        }
    }

    public CandidateReservation TryReserveCandidate(MongoId sessionId, BotGenerationDetails details)
    {
        if (!Config.Enabled || !details.IsPmc)
        {
            return default;
        }

        var sessionKey = SessionKey(sessionId);
        if (!_raids.TryGetValue(sessionKey, out var raid) || !raid.Scheduled || raid.Transformed)
        {
            return default;
        }

        var profile = GetProfile(sessionId);
        var rival = GetActiveRival(profile);
        if (rival is null || !string.Equals(rival.RivalId, raid.RivalId, StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        if (Config.RequireMatchingFaction && !SidesMatch(details.Side, rival.Faction))
        {
            return default;
        }

        lock (raid.Gate)
        {
            if (raid.CandidateReserved || raid.Transformed)
            {
                return default;
            }

            raid.CandidateReserved = true;
            var requestedLevel = GetRequestedLevel(rival);
            var apbsActive = Compatibility.ApbsDetected && Config.Compatibility.UseApbsProgression;
            var orbitActive = Compatibility.OrbitDetected && Config.Compatibility.PreserveOrbitPmcRole;
            var originalRole = details.Role;
            var originalSide = details.Side;

            // APBS reads PlayerLevel inside its BotLevelGenerator patch and then owns the final
            // generated level, tier, inventory and equipment. Nemesis only supplies the target.
            details.PlayerLevel = requestedLevel;
            if (!apbsActive && Config.Compatibility.ExactLevelFallbackWithoutApbs)
            {
                details.BotRelativeLevelDeltaMin = 0;
                details.BotRelativeLevelDeltaMax = 0;
            }

            details.BotDifficulty = RankIndex(rival.Rank) switch
            {
                >= 4 => "impossible",
                >= 2 => "hard",
                _ => details.BotDifficulty
            };

            return new CandidateReservation(
                true,
                rival.RivalId,
                requestedLevel,
                originalRole,
                originalSide,
                apbsActive,
                orbitActive);
        }
    }

    public void RecordGeneratedPmc(MongoId sessionId, BotBase? bot)
    {
        if (!Config.Enabled || bot?.Id is null || bot.Info is null)
        {
            return;
        }

        var role = bot.Info.Settings?.Role ?? string.Empty;
        var isPmc = bot.IsPmc == true
            || role.Contains("usec", StringComparison.OrdinalIgnoreCase)
            || role.Contains("bear", StringComparison.OrdinalIgnoreCase);
        if (!isPmc)
        {
            return;
        }

        var primaryWeapon = bot.Inventory?.Items?
            .FirstOrDefault(item => string.Equals(item.SlotId, "FirstPrimaryWeapon", StringComparison.OrdinalIgnoreCase))?
            .Template.ToString();

        _generatedPmcs[GeneratedBotKey(sessionId, bot.Id.Value.ToString())] = new GeneratedPmcSnapshot
        {
            BotId = bot.Id.Value.ToString(),
            Name = bot.Info.Nickname ?? "Unknown",
            Faction = bot.Info.Side ?? "Usec",
            Level = Math.Max(1, bot.Info.Level ?? 1),
            ApbsTier = GetExtensionDataValue(bot.Info, "Tier"),
            PrimaryWeaponTemplateId = primaryWeapon,
            Appearance = bot.Customization is null ? null : cloner.Clone(bot.Customization)
        };
    }

    public void TransformReservedCandidate(MongoId sessionId, CandidateReservation reservation, BotBase? bot)
    {
        if (!reservation.Reserved || bot?.Info is null)
        {
            ReleaseReservation(sessionId);
            return;
        }

        var sessionKey = SessionKey(sessionId);
        if (!_raids.TryGetValue(sessionKey, out var raid))
        {
            return;
        }

        var profile = GetProfile(sessionId);
        var rival = profile.Rivals.FirstOrDefault(candidate =>
            string.Equals(candidate.RivalId, reservation.RivalId, StringComparison.OrdinalIgnoreCase));
        if (rival is null || rival.IsDefeated)
        {
            ReleaseReservation(sessionId);
            return;
        }

        bot.Info.Nickname = rival.DisplayName;
        bot.Info.LowerNickname = rival.DisplayName.ToLowerInvariant();
        if (rival.Appearance is not null)
        {
            bot.Customization = cloner.Clone(rival.Appearance);
        }

        // Do not change Role, Side, IsPmc or the generated brain. That keeps the profile a normal
        // PMC for ORBIT, SAIN, BigBrain and Waypoints. Do not overwrite Info.Level after generation;
        // APBS/SPT has already used that exact level to build the matching equipment and loot tier.
        var actualGeneratedLevel = Math.Max(1, bot.Info.Level ?? reservation.RequestedLevel);
        var apbsTier = GetExtensionDataValue(bot.Info, "Tier");
        var apbsApplied = reservation.ApbsActive && !string.IsNullOrWhiteSpace(apbsTier);
        bot.Info.Settings ??= new BotInfoSettings();
        bot.Info.Settings.BotDifficulty = RankIndex(rival.Rank) switch
        {
            >= 4 => "impossible",
            >= 2 => "hard",
            _ => bot.Info.Settings.BotDifficulty
        };

        ScaleHealth(bot, 1.0 + (RankIndex(rival.Rank) * Config.HealthBonusPerRank));

        lock (raid.Gate)
        {
            raid.Transformed = true;
            raid.SpawnedBotId = bot.Id?.ToString();
            rival.TimesSpawned++;
            rival.LastRequestedLevel = reservation.RequestedLevel;
            rival.LastGeneratedLevel = actualGeneratedLevel;
            rival.LastLoadoutAuthority = apbsApplied ? "APBS" : "SPT";
            rival.LastApbsTier = apbsApplied ? apbsTier : null;
            rival.LastBehaviorAuthority = reservation.OrbitActive ? "ORBIT" : "SPT/installed AI mods";
            rival.LastAppearedRaidNumber = profile.TotalPmcRaids;
            rival.LastSeenMap = raid.Location;
            SaveProfile(sessionId, profile);
        }

        var rolePreserved = string.Equals(bot.Info.Settings.Role, reservation.OriginalRole, StringComparison.OrdinalIgnoreCase);
        var sidePreserved = SidesMatch(bot.Info.Side, reservation.OriginalSide ?? string.Empty);
        logger.Success(
            $"[Nemesis Protocol] Injected {rival.DisplayName} into {raid.Location}. " +
            $"Requested level {reservation.RequestedLevel}; {rival.LastLoadoutAuthority} generated level {actualGeneratedLevel}" +
            (string.IsNullOrWhiteSpace(rival.LastApbsTier) ? ". " : $" (tier {rival.LastApbsTier}). ") +
            $"PMC role preserved: {rolePreserved}; side preserved: {sidePreserved}; behavior: {rival.LastBehaviorAuthority}.");
    }

    public void OnRaidEnded(MongoId sessionId, EndLocalRaidRequestData request)
    {
        if (!Config.Enabled || request.Results is null)
        {
            return;
        }

        var sessionKey = SessionKey(sessionId);
        _raids.TryGetValue(sessionKey, out var raid);
        var profile = GetProfile(sessionId);
        if (raid?.IsPmcRaid == true)
        {
            profile.TotalPmcRaids++;
        }

        var active = GetActiveRival(profile);
        var spawnedId = raid?.SpawnedBotId;
        var victims = request.Results.Profile?.Stats?.Eft?.Victims ?? [];
        var playerKilledNemesis = active is not null
            && raid?.Transformed == true
            && victims.Any(victim => IdEquals(victim.ProfileId?.ToString(), spawnedId)
                                     || string.Equals(victim.Name, active.DisplayName, StringComparison.OrdinalIgnoreCase));

        if (playerKilledNemesis && active is not null)
        {
            active.Encounters++;
            active.IsDefeated = true;
            active.DefeatedAtUtc = DateTimeOffset.UtcNow;
            active.LastEncounterAtUtc = DateTimeOffset.UtcNow;
            profile.ActiveRivalId = null;
            SendRivalMessage(sessionId, active, Pick(Config.DeathMessages));
            logger.Success($"[Nemesis Protocol] {active.DisplayName} was defeated by the player.");
        }
        else if (active is not null && raid?.Transformed == true)
        {
            active.Encounters++;
            active.LastEncounterAtUtc = DateTimeOffset.UtcNow;
            active.LastSeenMap = raid.Location;

            if (IdEquals(request.Results.KillerId?.ToString(), spawnedId))
            {
                active.PlayerKills++;
                RecalculateThreat(active);
                SendRivalMessage(sessionId, active, Pick(Config.KillMessages));
                logger.Info($"[Nemesis Protocol] {active.DisplayName} killed the player. Rank: {active.Rank}.");
            }
            else
            {
                active.Escapes++;
                RecalculateThreat(active);
                if (Config.SendEscapeMessages)
                {
                    SendRivalMessage(sessionId, active, Pick(Config.EscapeMessages));
                }
            }
        }

        if (!playerKilledNemesis && GetActiveRival(profile) is null && request.Results.KillerId is not null)
        {
            TryCreateRivalFromKiller(sessionId, request, profile, raid?.Location);
        }

        SaveProfile(sessionId, profile);
        _raids.TryRemove(sessionKey, out _);
        ClearGeneratedPmcs(sessionId);
    }

    public string GetStatusText(MongoId sessionId)
    {
        var profile = GetProfile(sessionId);
        var active = GetActiveRival(profile);
        if (active is null)
        {
            var defeated = profile.Rivals.Count(rival => rival.IsDefeated);
            return defeated == 0
                ? "No active rival has identified you yet. A PMC must kill you before the protocol can begin."
                : $"No active rival. Confirmed defeated rivals: {defeated}.";
        }

        return $"ACTIVE NEMESIS\n" +
               $"Name: {active.DisplayName}\n" +
               $"Faction: {active.Faction}\n" +
               $"Rank: {active.Rank}\n" +
               $"Next requested level: {GetRequestedLevel(active)}\n" +
               $"Last actual level: {Math.Max(1, active.LastGeneratedLevel)}\n" +
               $"Loadout authority: {active.LastLoadoutAuthority}\n" +
               $"Last APBS tier: {active.LastApbsTier ?? "N/A"}\n" +
               $"Behavior authority: {active.LastBehaviorAuthority}\n" +
               $"Encounters: {active.Encounters}\n" +
               $"Kills against you: {active.PlayerKills}\n" +
               $"Escapes: {active.Escapes}\n" +
               $"Last seen: {active.LastSeenMap ?? "Unknown"}";
    }

    public string GetCompatibilityText()
    {
        var apbsState = Compatibility.ApbsDetected && Config.Compatibility.UseApbsProgression
            ? "ACTIVE — APBS owns generated level, tier, inventory and equipment"
            : "NOT ACTIVE — SPT owns generated level and inventory";
        var orbitState = Compatibility.OrbitDetected && Config.Compatibility.PreserveOrbitPmcRole
            ? "ACTIVE — ORBIT receives an unchanged PMC role/side/brain"
            : "NOT DETECTED — normal installed AI behavior remains in control";

        return $"NEMESIS COMPATIBILITY\nAPBS: {apbsState}\nORBIT: {orbitState}";
    }

    public string GetHistoryText(MongoId sessionId)
    {
        var profile = GetProfile(sessionId);
        if (profile.Rivals.Count == 0)
        {
            return "Nemesis archive is empty.";
        }

        return string.Join("\n", profile.Rivals
            .OrderByDescending(rival => rival.CreatedAtUtc)
            .Take(10)
            .Select(rival => $"{(rival.IsDefeated ? "DEFEATED" : "ACTIVE")} — {rival.DisplayName}, {rival.Rank}, {rival.PlayerKills} kills"));
    }

    private void TryCreateRivalFromKiller(
        MongoId sessionId,
        EndLocalRaidRequestData request,
        NemesisProfileState profile,
        string? location)
    {
        var killerId = request.Results?.KillerId?.ToString();
        if (string.IsNullOrWhiteSpace(killerId))
        {
            return;
        }

        _generatedPmcs.TryGetValue(GeneratedBotKey(sessionId, killerId), out var snapshot);
        var cachedKiller = botCache.GetBotById(request.Results?.KillerId);
        if (snapshot is null && cachedKiller is null)
        {
            return;
        }

        var aggressor = request.Results?.Profile?.Stats?.Eft?.Aggressor;
        var fallbackName = snapshot?.Name ?? cachedKiller?.Nickname ?? "Unknown";
        var killerName = !string.IsNullOrWhiteSpace(aggressor?.Name) ? aggressor.Name.Trim() : fallbackName.Trim();
        var fallbackFaction = snapshot?.Faction ?? cachedKiller?.Side.ToString() ?? "Usec";
        var faction = !string.IsNullOrWhiteSpace(aggressor?.Side) ? aggressor.Side.Trim() : fallbackFaction;
        var rival = new NemesisRival
        {
            OriginalName = killerName,
            DisplayName = BuildDisplayName(killerName),
            Faction = faction,
            BaseLevel = Math.Max(1, snapshot?.Level ?? cachedKiller?.Level ?? 1),
            LastRequestedLevel = Math.Max(1, snapshot?.Level ?? cachedKiller?.Level ?? 1),
            LastGeneratedLevel = Math.Max(1, snapshot?.Level ?? cachedKiller?.Level ?? 1),
            LastLoadoutAuthority = !string.IsNullOrWhiteSpace(snapshot?.ApbsTier) ? "APBS" : "SPT",
            LastApbsTier = snapshot?.ApbsTier,
            LastBehaviorAuthority = Compatibility.OrbitDetected && Config.Compatibility.PreserveOrbitPmcRole ? "ORBIT" : "SPT/installed AI mods",
            PrimaryWeaponTemplateId = snapshot?.PrimaryWeaponTemplateId ?? cachedKiller?.PrimaryWeapon?.ToString(),
            Appearance = snapshot?.Appearance is null ? null : cloner.Clone(snapshot.Appearance),
            LastSeenMap = location,
            PlayerKills = 1,
            Encounters = 1,
            LastEncounterAtUtc = DateTimeOffset.UtcNow,
            Rank = "Hunter"
        };
        RecalculateThreat(rival);
        profile.Rivals.Add(rival);
        profile.ActiveRivalId = rival.RivalId;
        SendRivalMessage(sessionId, rival, Pick(Config.CreationMessages));
        logger.Success($"[Nemesis Protocol] Created rival {rival.DisplayName} from actual killer bot {request.Results?.KillerId}.");
    }

    private NemesisProfileState GetProfile(MongoId sessionId)
    {
        var key = SessionKey(sessionId);
        return _profiles.GetOrAdd(key, _ => LoadProfile(key));
    }

    private NemesisProfileState LoadProfile(string sessionKey)
    {
        var path = ProfilePath(sessionKey);
        if (!File.Exists(path))
        {
            return new NemesisProfileState();
        }

        try
        {
            var profile = JsonSerializer.Deserialize<NemesisProfileState>(File.ReadAllText(path), _jsonOptions)
                          ?? new NemesisProfileState();
            MigrateProfile(profile);
            return profile;
        }
        catch (Exception exception)
        {
            var backup = path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(path, backup, true);
            logger.Error($"[Nemesis Protocol] Profile data was unreadable and was backed up to {backup}: {exception.Message}");
            return new NemesisProfileState();
        }
    }


    private static void MigrateProfile(NemesisProfileState profile)
    {
        if (profile.SchemaVersion >= 2)
        {
            return;
        }

        foreach (var rival in profile.Rivals)
        {
            var fallbackLevel = Math.Max(1, rival.BaseLevel);
            if (rival.LastRequestedLevel <= 1 && fallbackLevel > 1)
            {
                rival.LastRequestedLevel = fallbackLevel;
            }
            if (rival.LastGeneratedLevel <= 1 && fallbackLevel > 1)
            {
                rival.LastGeneratedLevel = fallbackLevel;
            }
            if (string.IsNullOrWhiteSpace(rival.LastLoadoutAuthority))
            {
                rival.LastLoadoutAuthority = "SPT";
            }
            if (string.IsNullOrWhiteSpace(rival.LastBehaviorAuthority))
            {
                rival.LastBehaviorAuthority = "SPT/installed AI mods";
            }
        }

        profile.SchemaVersion = 2;
    }

    private void SaveProfile(MongoId sessionId, NemesisProfileState profile)
    {
        var key = SessionKey(sessionId);
        var sync = _profileLocks.GetOrAdd(key, _ => new object());
        lock (sync)
        {
            Directory.CreateDirectory(_profileDataPath);
            var path = ProfilePath(key);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(profile, _jsonOptions));
            File.Move(temporaryPath, path, true);
            _profiles[key] = profile;
        }
    }

    private string ProfilePath(string sessionKey)
    {
        var safe = string.Concat(sessionKey.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        return IOPath.Combine(_profileDataPath, safe + ".json");
    }

    private NemesisRival? GetActiveRival(NemesisProfileState profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ActiveRivalId))
        {
            return null;
        }

        return profile.Rivals.FirstOrDefault(rival =>
            !rival.IsDefeated && string.Equals(rival.RivalId, profile.ActiveRivalId, StringComparison.OrdinalIgnoreCase));
    }

    private void RecalculateThreat(NemesisRival rival)
    {
        rival.LevelBonus = Math.Clamp(
            (rival.PlayerKills * Config.LevelGainPerPlayerKill) + (rival.Escapes * Config.LevelGainPerEscape),
            0,
            Config.MaximumBonusLevels);
        var score = rival.PlayerKills + rival.Escapes;
        rival.Rank = score switch
        {
            >= 8 => "Legend",
            >= 5 => "Warlord",
            >= 3 => "Enforcer",
            >= 1 => "Hunter",
            _ => "Initiate"
        };
    }

    private static int RankIndex(string rank) => rank switch
    {
        "Legend" => 4,
        "Warlord" => 3,
        "Enforcer" => 2,
        "Hunter" => 1,
        _ => 0
    };

    private int GetRequestedLevel(NemesisRival rival)
    {
        var progressionTarget = rival.BaseLevel + Math.Clamp(rival.LevelBonus, 0, Config.MaximumBonusLevels);
        return Math.Clamp(Math.Max(progressionTarget, rival.LastGeneratedLevel), 1, 79);
    }

    private NemesisCompatibilityStatus DetectCompatibility()
    {
        var modsRoot = Directory.GetParent(_modPath)?.FullName ?? string.Empty;
        var sptRoot = IOPath.GetFullPath(IOPath.Combine(_modPath, "..", "..", ".."));
        var pluginsRoot = IOPath.Combine(sptRoot, "BepInEx", "plugins");

        var apbsDetected = HasLoadedAssembly("ProgressiveBotSystem")
            || ContainsPathToken(modsRoot, "progressivebotsystem", "acidphantasm-progressivebotsystem");
        var orbitDetected = HasLoadedAssembly("ORBIT")
            || ContainsPathToken(pluginsRoot, "orbit");

        return new NemesisCompatibilityStatus
        {
            ApbsDetected = apbsDetected,
            OrbitDetected = orbitDetected,
            ApbsAuthority = apbsDetected && Config.Compatibility.UseApbsProgression ? "APBS" : "SPT",
            BehaviorAuthority = orbitDetected && Config.Compatibility.PreserveOrbitPmcRole
                ? "ORBIT"
                : "SPT/installed AI mods"
        };
    }

    private static bool HasLoadedAssembly(string token)
    {
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            assembly.GetName().Name?.Contains(token, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool ContainsPathToken(string root, params string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Take(10000)
                .Any(path => tokens.Any(token =>
                    IOPath.GetFileName(path).Contains(token, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    private static string? GetExtensionDataValue(object? target, string key)
    {
        if (target is null)
        {
            return null;
        }

        try
        {
            var property = target.GetType().GetProperty("ExtensionData", BindingFlags.Instance | BindingFlags.Public);
            if (property?.GetValue(target) is not IDictionary dictionary || !dictionary.Contains(key))
            {
                return null;
            }

            return dictionary[key]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private void ScaleHealth(BotBase bot, double multiplier)
    {
        if (multiplier <= 1.0 || bot.Health?.BodyParts is null)
        {
            return;
        }

        foreach (var bodyPart in bot.Health.BodyParts.Values)
        {
            if (bodyPart.Health is null)
            {
                continue;
            }

            if (bodyPart.Health.Maximum is double maximum)
            {
                bodyPart.Health.Maximum = Math.Round(maximum * multiplier, 2);
            }
            bodyPart.Health.Current = bodyPart.Health.Maximum;
        }
    }

    private void SendRivalMessage(MongoId sessionId, NemesisRival rival, string message)
    {
        if (!Config.SendMessages || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            mailSendService.SendUserMessageToPlayer(sessionId, BuildSender(rival), message);
        }
        catch (Exception exception)
        {
            logger.Warning($"[Nemesis Protocol] Failed to send rival message: {exception.Message}");
        }
    }

    private static UserDialogInfo BuildSender(NemesisRival rival) => new()
    {
        Id = "660000000000000000000001",
        Aid = 77701337,
        Info = new UserDialogDetails
        {
            Nickname = rival.DisplayName,
            Side = rival.Faction,
            Level = Math.Max(1, rival.LastGeneratedLevel),
            MemberCategory = MemberCategory.Sherpa,
            SelectedMemberCategory = MemberCategory.Sherpa
        }
    };

    private string BuildDisplayName(string original)
    {
        var suffix = Config.NameSuffix ?? string.Empty;
        return original.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? original : original + suffix;
    }

    private string Pick(IReadOnlyList<string> choices)
    {
        if (choices.Count == 0)
        {
            return string.Empty;
        }

        lock (_random)
        {
            return choices[_random.Next(choices.Count)];
        }
    }

    private bool NextChance(double percent)
    {
        if (percent <= 0) return false;
        if (percent >= 100) return true;
        lock (_random)
        {
            return (_random.NextDouble() * 100.0) < percent;
        }
    }

    private static bool SidesMatch(string? generatedSide, string rivalFaction) =>
        string.Equals(generatedSide, rivalFaction, StringComparison.OrdinalIgnoreCase)
        || (generatedSide?.Contains("usec", StringComparison.OrdinalIgnoreCase) == true
            && rivalFaction.Contains("usec", StringComparison.OrdinalIgnoreCase))
        || (generatedSide?.Contains("bear", StringComparison.OrdinalIgnoreCase) == true
            && rivalFaction.Contains("bear", StringComparison.OrdinalIgnoreCase));

    private void ReleaseReservation(MongoId sessionId)
    {
        if (_raids.TryGetValue(SessionKey(sessionId), out var raid))
        {
            lock (raid.Gate)
            {
                raid.CandidateReserved = false;
            }
        }
    }

    private static bool IdEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private void ClearGeneratedPmcs(MongoId sessionId)
    {
        var prefix = SessionKey(sessionId) + "|";
        foreach (var key in _generatedPmcs.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            _generatedPmcs.TryRemove(key, out _);
        }
    }

    private static string GeneratedBotKey(MongoId sessionId, string botId) => SessionKey(sessionId) + "|" + botId;

    private static string SessionKey(MongoId sessionId) => sessionId.ToString();
}
