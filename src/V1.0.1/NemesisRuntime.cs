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
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Cloners;

namespace ForgeHQ.NemesisProtocol;

[Injectable(InjectionType.Singleton)]
public sealed class NemesisRuntime(
    ISptLogger<NemesisRuntime> logger,
    ModHelper modHelper,
    MailSendService mailSendService,
    SaveServer saveServer,
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
    private static readonly string[] RussianFirstNames =
    [
        "Aleksei", "Aleksandr", "Andrei", "Anton", "Artem", "Boris", "Denis", "Dmitri",
        "Evgeni", "Fyodor", "Gennadi", "Grigori", "Igor", "Ilya", "Ivan", "Kirill",
        "Konstantin", "Maksim", "Mikhail", "Nikolai", "Oleg", "Pavel", "Roman", "Ruslan",
        "Sergei", "Stanislav", "Vadim", "Viktor", "Vladimir", "Yuri"
    ];

    private static readonly string[] RussianLastNames =
    [
        "Antonov", "Baranov", "Belov", "Bogdanov", "Chernov", "Denisov", "Fedorov",
        "Gromov", "Ivanov", "Kalinin", "Karpov", "Kirillov", "Kozlov", "Kravtsov",
        "Kuznetsov", "Lebedev", "Makarov", "Melnikov", "Morozov", "Nikitin", "Novikov",
        "Orlov", "Pavlov", "Petrov", "Popov", "Romanov", "Semyonov", "Smirnov",
        "Sokolov", "Tarasov", "Titov", "Volkov", "Voronin", "Zaitsev", "Zhukov"
    ];


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
            if (Compatibility.OrbitDetected && !string.IsNullOrWhiteSpace(Compatibility.OrbitDetectionPath))
            {
                logger.Success($"[Nemesis Protocol] ORBIT detected at: {Compatibility.OrbitDetectionPath}");
            }
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
            rival.LastBehaviorAuthority = reservation.OrbitActive ? "ORBIT / installed AI framework" : "SPT/installed AI mods";
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

        var currentBehaviorAuthority = GetCurrentBehaviorAuthority();

        return $"ACTIVE NEMESIS\n" +
               $"Name: {active.DisplayName}\n" +
               $"Faction: {active.Faction}\n" +
               $"Rank: {active.Rank}\n" +
               $"Next requested level: {GetRequestedLevel(active)}\n" +
               $"Last actual level: {Math.Max(1, active.LastGeneratedLevel)}\n" +
               $"Loadout authority: {active.LastLoadoutAuthority}\n" +
               $"Last APBS tier: {active.LastApbsTier ?? "N/A"}\n" +
               $"Behavior authority: {currentBehaviorAuthority}\n" +
               $"Encounters: {active.Encounters}\n" +
               $"Kills against you: {active.PlayerKills}\n" +
               $"Escapes: {active.Escapes}\n" +
               $"Last seen: {active.LastSeenMap ?? "Unknown"}";
    }

    public string GetCompatibilityText()
    {
        // Recheck on every command so installs or folder changes made after server startup are reflected.
        Compatibility = DetectCompatibility();

        var apbsState = Compatibility.ApbsDetected && Config.Compatibility.UseApbsProgression
            ? "ACTIVE — APBS owns generated level, tier, inventory and equipment"
            : "NOT ACTIVE — SPT owns generated level and inventory";
        var orbitState = Compatibility.OrbitDetected && Config.Compatibility.PreserveOrbitPmcRole
            ? "ACTIVE — ORBIT receives an unchanged PMC role/side/brain"
            : "NOT DETECTED — normal installed AI behavior remains in control";
        var orbitLocation = Compatibility.OrbitDetected
            ? $"\nORBIT location: {Compatibility.OrbitDetectionPath ?? "loaded server assembly"}"
            : $"\nORBIT search paths:\n{string.Join("\n", Compatibility.OrbitSearchPaths.Select(path => $"- {path}"))}";

        return $"NEMESIS COMPATIBILITY\nAPBS: {apbsState}\nORBIT: {orbitState}{orbitLocation}";
    }

    public string GetHistoryText(MongoId sessionId)
    {
        var profile = GetProfile(sessionId);
        if (profile.Rivals.Count == 0)
        {
            return "Nemesis archive is empty.";
        }

        var ordered = profile.Rivals
            .OrderByDescending(rival => !rival.IsDefeated)
            .ThenByDescending(rival => rival.CreatedAtUtc)
            .Take(10)
            .ToList();

        var lines = ordered.Select((rival, index) =>
            $"{index + 1}. {(rival.IsDefeated ? "DEFEATED" : "ACTIVE")} — {rival.DisplayName}, " +
            $"{rival.Rank}, level {Math.Max(1, rival.LastGeneratedLevel)}, {rival.PlayerKills} kills");

        return "NEMESIS ARCHIVE\n" +
               string.Join("\n", lines) +
               "\n\nUse: rival <number or name>";
    }

    public string GetRivalDetailText(MongoId sessionId, string query)
    {
        var profile = GetProfile(sessionId);
        if (profile.Rivals.Count == 0)
        {
            return "Nemesis archive is empty.";
        }

        var ordered = profile.Rivals
            .OrderByDescending(rival => !rival.IsDefeated)
            .ThenByDescending(rival => rival.CreatedAtUtc)
            .ToList();

        NemesisRival? rival = null;
        if (int.TryParse(query, out var index) && index >= 1 && index <= ordered.Count)
        {
            rival = ordered[index - 1];
        }

        rival ??= ordered.FirstOrDefault(candidate =>
            string.Equals(candidate.OriginalName, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.DisplayName, query, StringComparison.OrdinalIgnoreCase));

        rival ??= ordered.FirstOrDefault(candidate =>
            candidate.OriginalName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || candidate.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (rival is null)
        {
            return $"No rival matched \"{query}\". Use 'rivals' to view the archive.";
        }

        var currentBehaviorAuthority = GetCurrentBehaviorAuthority();

        return $"NEMESIS DOSSIER\n" +
               $"Name: {rival.DisplayName}\n" +
               $"Status: {(rival.IsDefeated ? "Defeated" : "Active")}\n" +
               $"Faction: {rival.Faction}\n" +
               $"Rank: {rival.Rank}\n" +
               $"Base level: {Math.Max(1, rival.BaseLevel)}\n" +
               $"Last actual level: {Math.Max(1, rival.LastGeneratedLevel)}\n" +
               $"Next requested level: {GetRequestedLevel(rival)}\n" +
               $"Encounters: {rival.Encounters}\n" +
               $"Kills against you: {rival.PlayerKills}\n" +
               $"Escapes: {rival.Escapes}\n" +
               $"Times spawned: {rival.TimesSpawned}\n" +
               $"Last seen: {rival.LastSeenMap ?? "Unknown"}\n" +
               $"Loadout authority: {rival.LastLoadoutAuthority}\n" +
               $"Behavior authority: {currentBehaviorAuthority}";
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
        var sourceKillerName = !string.IsNullOrWhiteSpace(aggressor?.Name) ? aggressor.Name.Trim() : fallbackName.Trim();
        var rivalName = GenerateRussianRivalName(profile);
        var fallbackFaction = snapshot?.Faction ?? cachedKiller?.Side.ToString() ?? "Usec";
        var faction = !string.IsNullOrWhiteSpace(aggressor?.Side) ? aggressor.Side.Trim() : fallbackFaction;
        var rival = new NemesisRival
        {
            DialogueId = CreateDialogueId(),
            OriginalName = rivalName,
            SourceBotName = sourceKillerName,
            DisplayName = BuildDisplayName(rivalName),
            Faction = faction,
            BaseLevel = Math.Max(1, snapshot?.Level ?? cachedKiller?.Level ?? 1),
            LastRequestedLevel = Math.Max(1, snapshot?.Level ?? cachedKiller?.Level ?? 1),
            LastGeneratedLevel = Math.Max(1, snapshot?.Level ?? cachedKiller?.Level ?? 1),
            LastLoadoutAuthority = !string.IsNullOrWhiteSpace(snapshot?.ApbsTier) ? "APBS" : "SPT",
            LastApbsTier = snapshot?.ApbsTier,
            LastBehaviorAuthority = Compatibility.OrbitDetected && Config.Compatibility.PreserveOrbitPmcRole ? "ORBIT / installed AI framework" : "SPT/installed AI mods",
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
        logger.Success($"[Nemesis Protocol] Created rival {rival.DisplayName} from actual killer bot {request.Results?.KillerId}. Source PMC name: {sourceKillerName}.");
    }

    private NemesisProfileState GetProfile(MongoId sessionId)
    {
        var key = SessionKey(sessionId);
        var profile = _profiles.GetOrAdd(key, _ => LoadProfile(key));
        SyncExistingRivalDialogueIdentities(sessionId, profile);
        return profile;
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
            if (MigrateProfile(profile))
            {
                var migrationTemporaryPath = path + ".migration.tmp";
                File.WriteAllText(migrationTemporaryPath, JsonSerializer.Serialize(profile, _jsonOptions));
                File.Move(migrationTemporaryPath, path, true);
                logger.Success($"[Nemesis Protocol] Migrated rival profile data to schema {profile.SchemaVersion}.");
            }

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


    private bool MigrateProfile(NemesisProfileState profile)
    {
        var changed = false;

        if (profile.SchemaVersion < 2)
        {
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
            changed = true;
        }

        // Validate rival Messenger identities on every load, not only during the first schema migration.
        foreach (var rival in profile.Rivals)
        {
            if (!IsValidDialogueId(rival.DialogueId)
                || string.Equals(rival.DialogueId, NemesisChatBot.NetworkId, StringComparison.OrdinalIgnoreCase))
            {
                rival.DialogueId = CreateDialogueId();
                changed = true;
            }
        }

        if (profile.SchemaVersion < 3)
        {
            profile.SchemaVersion = 3;
            changed = true;
        }

        if (profile.SchemaVersion < 4)
        {
            var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rival in profile.Rivals)
            {
                var previousName = RemoveNameSuffix(rival.OriginalName);
                if (string.IsNullOrWhiteSpace(rival.SourceBotName))
                {
                    rival.SourceBotName = previousName;
                    changed = true;
                }

                if (IsBuiltInRussianFullName(previousName))
                {
                    reservedNames.Add(previousName);
                    rival.OriginalName = previousName;
                    rival.DisplayName = BuildDisplayName(previousName);
                    continue;
                }

                var generatedName = GenerateRussianRivalName(profile, reservedNames);
                reservedNames.Add(generatedName);
                rival.OriginalName = generatedName;
                rival.DisplayName = BuildDisplayName(generatedName);
                changed = true;
            }

            profile.SchemaVersion = 4;
            changed = true;
        }

        return changed;
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

    private string GetCurrentBehaviorAuthority()
    {
        // Keep status and dossier commands synchronized with the live compatibility detector.
        // This avoids stale values saved during an earlier encounter or before ORBIT was detected.
        Compatibility = DetectCompatibility();
        return Compatibility.BehaviorAuthority;
    }

    private NemesisCompatibilityStatus DetectCompatibility()
    {
        var modsRoot = Directory.GetParent(_modPath)?.FullName ?? string.Empty;
        var sptRoot = IOPath.GetFullPath(IOPath.Combine(_modPath, "..", "..", ".."));
        var orbitSearchPaths = BuildOrbitSearchPaths(sptRoot);

        var apbsDetected = HasLoadedAssembly("ProgressiveBotSystem")
            || ContainsPathToken(modsRoot, "progressivebotsystem", "acidphantasm-progressivebotsystem");

        var orbitDetectionPath = FindLoadedAssemblyPath("ORBIT");
        if (string.IsNullOrWhiteSpace(orbitDetectionPath))
        {
            foreach (var searchPath in orbitSearchPaths)
            {
                if (TryFindPathToken(searchPath, out var matchingPath, "orbit"))
                {
                    orbitDetectionPath = matchingPath;
                    break;
                }
            }
        }

        var orbitDetected = !string.IsNullOrWhiteSpace(orbitDetectionPath);

        return new NemesisCompatibilityStatus
        {
            ApbsDetected = apbsDetected,
            OrbitDetected = orbitDetected,
            ApbsAuthority = apbsDetected && Config.Compatibility.UseApbsProgression ? "APBS" : "SPT",
            BehaviorAuthority = orbitDetected && Config.Compatibility.PreserveOrbitPmcRole
                ? "ORBIT / installed AI framework"
                : "SPT/installed AI mods",
            OrbitDetectionPath = orbitDetectionPath,
            OrbitSearchPaths = orbitSearchPaths
        };
    }

    private static List<string> BuildOrbitSearchPaths(string sptRoot)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string? ParentOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Directory.GetParent(IOPath.GetFullPath(path))?.FullName;
            }
            catch
            {
                return null;
            }
        }

        void AddInstallRoot(string? installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                return;
            }

            try
            {
                paths.Add(IOPath.GetFullPath(IOPath.Combine(installRoot, "BepInEx", "plugins")));
            }
            catch
            {
                // Ignore malformed paths and continue checking the remaining install roots.
            }
        }

        AddInstallRoot(sptRoot);
        AddInstallRoot(ParentOf(sptRoot));
        AddInstallRoot(ParentOf(ParentOf(sptRoot)));

        var baseDirectory = AppContext.BaseDirectory;
        AddInstallRoot(baseDirectory);
        AddInstallRoot(ParentOf(baseDirectory));

        var currentDirectory = Environment.CurrentDirectory;
        AddInstallRoot(currentDirectory);
        AddInstallRoot(ParentOf(currentDirectory));

        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FindLoadedAssemblyPath(string token)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            candidate.GetName().Name?.Contains(token, StringComparison.OrdinalIgnoreCase) == true);
        if (assembly is null)
        {
            return null;
        }

        try
        {
            return string.IsNullOrWhiteSpace(assembly.Location)
                ? $"Loaded assembly: {assembly.GetName().Name}"
                : assembly.Location;
        }
        catch
        {
            return $"Loaded assembly: {assembly.GetName().Name}";
        }
    }

    private static bool HasLoadedAssembly(string token) =>
        AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            assembly.GetName().Name?.Contains(token, StringComparison.OrdinalIgnoreCase) == true);

    private static bool ContainsPathToken(string root, params string[] tokens) =>
        TryFindPathToken(root, out _, tokens);

    private static bool TryFindPathToken(string root, out string? matchingPath, params string[] tokens)
    {
        matchingPath = null;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return false;
        }

        try
        {
            matchingPath = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Take(10000)
                .FirstOrDefault(path => tokens.Any(token =>
                    IOPath.GetFileName(path).Contains(token, StringComparison.OrdinalIgnoreCase)));

            return !string.IsNullOrWhiteSpace(matchingPath);
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

    private string GenerateRussianRivalName(NemesisProfileState profile, ISet<string>? reservedNames = null)
    {
        var unavailableNames = profile.Rivals
            .Select(rival => RemoveNameSuffix(rival.OriginalName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (reservedNames is not null)
        {
            unavailableNames.UnionWith(reservedNames);
        }

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var candidate = $"{Pick(RussianFirstNames)} {Pick(RussianLastNames)}";
            if (!unavailableNames.Contains(candidate))
            {
                return candidate;
            }
        }

        // The built-in pool has more than one thousand combinations, so this should be practically unreachable.
        return $"{Pick(RussianFirstNames)} {Pick(RussianLastNames)}";
    }

    private bool IsBuiltInRussianFullName(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && RussianFirstNames.Contains(parts[0], StringComparer.OrdinalIgnoreCase)
            && RussianLastNames.Contains(parts[1], StringComparer.OrdinalIgnoreCase);
    }

    private string RemoveNameSuffix(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
        var suffix = Config.NameSuffix ?? string.Empty;
        if (!string.IsNullOrEmpty(suffix) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return name[..^suffix.Length].Trim();
        }

        return name;
    }

    private void SyncExistingRivalDialogueIdentities(MongoId sessionId, NemesisProfileState profile)
    {
        try
        {
            var sptProfile = saveServer.GetProfile(sessionId);
            if (sptProfile.DialogueRecords is null)
            {
                return;
            }

            foreach (var rival in profile.Rivals)
            {
                if (!IsValidDialogueId(rival.DialogueId))
                {
                    continue;
                }

                var dialogueId = new MongoId(rival.DialogueId);
                if (sptProfile.DialogueRecords.TryGetValue(dialogueId, out var dialogue))
                {
                    dialogue.Users = [BuildRivalSender(rival)];
                }
            }
        }
        catch (Exception exception)
        {
            logger.Warning($"[Nemesis Protocol] Could not refresh rival Messenger names: {exception.Message}");
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
            if (!IsValidDialogueId(rival.DialogueId)
                || string.Equals(rival.DialogueId, NemesisChatBot.NetworkId, StringComparison.OrdinalIgnoreCase))
            {
                rival.DialogueId = CreateDialogueId();
            }

            var sender = BuildRivalSender(rival);
            PrepareRivalDialogue(sessionId, sender);
            mailSendService.SendUserMessageToPlayer(sessionId, sender, message);
        }
        catch (Exception exception)
        {
            logger.Warning($"[Nemesis Protocol] Failed to send rival message: {exception.Message}");
        }
    }

    private void PrepareRivalDialogue(MongoId sessionId, UserDialogInfo sender)
    {
        var profile = saveServer.GetProfile(sessionId);
        profile.DialogueRecords ??= [];

        if (!profile.DialogueRecords.TryGetValue(sender.Id, out var dialogue))
        {
            dialogue = new Dialogue
            {
                Id = sender.Id,
                Type = MessageType.UserMessage,
                Messages = [],
                Pinned = false,
                New = 0,
                AttachmentsNew = 0,
                Users = [sender]
            };
            profile.DialogueRecords[sender.Id] = dialogue;
            return;
        }

        dialogue.Type = MessageType.UserMessage;
        dialogue.Users = [sender];
        dialogue.Messages ??= [];
    }

    private static UserDialogInfo BuildRivalSender(NemesisRival rival) => new()
    {
        Id = rival.DialogueId,
        Aid = BuildRivalAid(rival.DialogueId),
        Info = new UserDialogDetails
        {
            Nickname = rival.DisplayName,
            Side = rival.Faction,
            Level = Math.Max(1, rival.LastGeneratedLevel),
            MemberCategory = MemberCategory.Default,
            SelectedMemberCategory = MemberCategory.Default
        }
    };

    private static int BuildRivalAid(string dialogueId)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in dialogueId)
            {
                hash ^= character;
                hash *= 16777619;
            }

            return 10000000 + (int)(hash % 80000000);
        }
    }

    private static string CreateDialogueId() => new MongoId().ToString();

    private static bool IsValidDialogueId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length == 24
        && value.All(character =>
            character is >= '0' and <= '9'
            || character is >= 'a' and <= 'f'
            || character is >= 'A' and <= 'F');

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
