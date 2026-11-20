using ServerPickerX.Models;
using ServerPickerX.Services.DependencyInjection;
using ServerPickerX.Services.Loggers;
using ServerPickerX.Services.MessageBoxes;
using ServerPickerX.Services.Servers;
using ServerPickerX.Services.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ServerPickerX.Settings
{
    // Publishing an app with trimmed assemblies or using AOT compilation for reduced build size
    // can break serialization due to limitations when using Reflection which analyzes dynamic types on runtime.
    // JsonSerializerContext preserves the types and provides serialization metadata on compile-time.
    [JsonSerializable(typeof(JsonSetting))]
    internal partial class SourceGenerationContext : JsonSerializerContext { }

    public class JsonSetting : ISetting
    {
        // Properties are virtual for unit test mocking 
        public virtual string warning { get; private set; } = "Do not modify settings here! only do it from the app!";

        // New installs open on Deadlock, after that the last game picked is remembered
        public virtual string game_mode { set; get; } = "Deadlock";

        public virtual string render_mode { set; get; } = "Software (CPU)";

        public virtual string language { set; get; } = "English | en-us";

        // "System" follows the OS setting, "Light" and "Dark" pin it
        public virtual string theme { set; get; } = "System";

        public virtual Dictionary<string, string> server_revisions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public virtual bool is_clustered { get; set; } = false;

        public virtual bool version_check_on_startup { get; set; } = true;

        public virtual List<PresetModel> server_presets { get; set; } = [];

        public virtual Dictionary<string, string> last_selected_preset_names { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Blocked servers per game mode, firewall rules outlive the process
        public virtual Dictionary<string, List<string>> blocked_server_keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Last successful reading per server as "ping|loss", kept so a blocked server
        // still has something to sort by, since no probe of it can succeed
        public virtual Dictionary<string, Dictionary<string, string>> last_server_readings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Minutes between automatic re-pings, 0 disables the timer
        public virtual int auto_refresh_minutes { get; set; } = 0;

        public virtual bool minimize_to_tray { get; set; } = false;

        // Applies this game's last used preset when the game starts and unblocks
        // again when it exits, opt in and off by default
        public virtual bool auto_apply_on_game_launch { get; set; } = false;

        // The late night queue notice shows at most once a day, and never again once the
        // user picks "Don't show again"
        public virtual bool late_night_notice_hidden { get; set; } = false;

        public virtual string late_night_notice_last_shown { get; set; } = string.Empty;

        // The warning that switching games unblocks everything first, until the user picks
        // "Ok, don't ask again". Switching still unblocks, the app just stops asking
        public virtual bool game_switch_notice_hidden { get; set; } = false;

        // The same warning for grouping servers into clusters and back, which
        // clears the blocks for the same reason: the two views name servers
        // differently, so a rule made in one cannot be carried into the other
        public virtual bool cluster_switch_notice_hidden { get; set; } = false;

        [JsonIgnore]
        public readonly string jsonFilePath = ResolveSettingsPath();

        [JsonIgnore]
        public readonly JsonSerializerOptions serializerOptions = new()
        {
            TypeInfoResolver = SourceGenerationContext.Default,
            WriteIndented = true,
            IncludeFields = true,
        };

        // A ping sweep and a firewall pass can both save at the same moment, and two
        // overlapping writes to the same file fail, so saves take turns
        private static readonly SemaphoreSlim SaveLock = new(1, 1);

        [JsonIgnore]
        private IMessageBoxService _messageBoxService { get; set; }
        [JsonIgnore]
        private ILoggerService _loggerService { get; set; }

        // The settings file belongs next to the executable, not in whatever directory
        // the process happened to start in. A shortcut without a working directory or
        // an elevated launch can hand the app a cwd such as C:\Windows\System32, and the
        // settings would silently be written and read there instead
        private static string ResolveSettingsPath()
        {
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

            try
            {
                string legacyPath = Path.GetFullPath("./settings.json");

                bool hasLegacySettingsToMove = !File.Exists(settingsPath)
                    && File.Exists(legacyPath)
                    && !string.Equals(legacyPath, settingsPath, StringComparison.OrdinalIgnoreCase);

                if (hasLegacySettingsToMove)
                {
                    File.Move(legacyPath, settingsPath);
                }
            }
            catch (Exception)
            {
                // A migration that cannot happen is not worth failing startup over.
                // The app carries on with fresh settings in the correct place
            }

            return settingsPath;
        }

        public JsonSetting() {}

        public JsonSetting(
            IMessageBoxService messageBoxService,
            ILoggerService logger
            )
        {
            _messageBoxService = messageBoxService;
            _loggerService = logger;
        }

        #pragma warning disable IL2026
        // Reflection is partially used here and might not be trim-compatible
        // unless JsonSerializerIsReflectionEnabledByDefault is set to true in .csproj
        public async Task LoadSettingsAsync()
        {
            try
            {
                // create local json settings if not exists with serialized object properties
                if (!File.Exists(jsonFilePath))
                {
                    using FileStream newSettingsFile = File.Create(jsonFilePath);

                    await JsonSerializer.SerializeAsync(newSettingsFile, this);

                    return;
                }

                using FileStream settingsFile = File.OpenRead(jsonFilePath);

                JsonSetting localSettings = await JsonSerializer.DeserializeAsync<JsonSetting>(settingsFile, serializerOptions) ?? this;

                game_mode = localSettings.game_mode;
                render_mode = localSettings.render_mode;
                language = localSettings.language;
                theme = string.IsNullOrWhiteSpace(localSettings.theme) ? "System" : localSettings.theme;
                server_revisions = localSettings.server_revisions != null
                    ? new Dictionary<string, string>(localSettings.server_revisions, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                is_clustered = localSettings.is_clustered;
                version_check_on_startup = localSettings.version_check_on_startup;
                server_presets = localSettings.server_presets ?? [];
                last_selected_preset_names = localSettings.last_selected_preset_names != null
                    ? new Dictionary<string, string>(localSettings.last_selected_preset_names, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                auto_refresh_minutes = localSettings.auto_refresh_minutes;
                minimize_to_tray = localSettings.minimize_to_tray;
                auto_apply_on_game_launch = localSettings.auto_apply_on_game_launch;
                late_night_notice_hidden = localSettings.late_night_notice_hidden;
                late_night_notice_last_shown = localSettings.late_night_notice_last_shown ?? string.Empty;
                game_switch_notice_hidden = localSettings.game_switch_notice_hidden;
                cluster_switch_notice_hidden = localSettings.cluster_switch_notice_hidden;
                last_server_readings = localSettings.last_server_readings != null
                    ? new Dictionary<string, Dictionary<string, string>>(localSettings.last_server_readings, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                blocked_server_keys = localSettings.blocked_server_keys != null
                    ? new Dictionary<string, List<string>>(localSettings.blocked_server_keys, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                await _loggerService.LogErrorAsync("An error has occured while loading json settings", ex.Message);

                await _messageBoxService.ShowMessageBoxAsync("Error", "An error has occured while loading json settings");
            }
        }

        // Reflection is partially used here and might not be trim-compatible
        // unless JsonSerializerIsReflectionEnabledByDefault is set to true in .csproj
        public async Task<bool> SaveSettingsAsync()
        {
            await SaveLock.WaitAsync();

            // Written beside the real file and then moved over it in one go.
            // Writing into the real file meant emptying it first and filling
            // it again afterwards, and anything going wrong in between -- a
            // crash, the power cutting out, the serializer throwing on one
            // bad value -- left the file empty or half written. Either way
            // every setting, preset and blocked server list was gone on the
            // next launch. Replacing the file in one move cannot do that:
            // what is on disk is always either all of the old settings or
            // all of the new ones
            string temporaryPath = jsonFilePath + ".tmp";

            try
            {
                await using (FileStream file = File.Create(temporaryPath))
                {
                    await JsonSerializer.SerializeAsync(file, this, serializerOptions);

                    // Settles the bytes on the drive before anything is
                    // replaced, so the move cannot swap in a file the
                    // drive has not actually finished writing
                    file.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, jsonFilePath, overwrite: true);

                return true;
            }
            catch (Exception ex)
            {
                // The real settings file was never touched, so it still
                // holds the last good copy. Only the half written spare
                // needs clearing away
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch (Exception)
                {
                    // Nothing more to do about it, and the save error below
                    // is the one worth telling anybody about
                }

                await _loggerService.LogErrorAsync("An error has occured while saving json settings", ex.Message);

                await _messageBoxService.ShowMessageBoxAsync("Error", "An error has occured while saving json settings");

                return false;
            }
            finally
            {
                SaveLock.Release();
            }
        }

        public async Task<string> GetRevisionByGameModeAsync()
        {
            try
            {
                string appId = GetCurrentAppId();

                return server_revisions.TryGetValue(appId, out string? revision)
                    ? revision
                    : "-1";
            } catch (InvalidOperationException ex) {
                await _loggerService.LogErrorAsync("An error has occured while getting server revision by current game mode", ex.Message);

                throw;
            }
        }

        public async Task SetRevisionByGameModeAsync(string revision)
        {
            try
            {
                string appId = GetCurrentAppId();
                server_revisions[appId] = revision;

                await this.SaveSettingsAsync();
            }
            catch (InvalidOperationException ex)
            {
                await _loggerService.LogErrorAsync("An error has occured while setting server revision by current game mode", ex.Message);

                throw;
            }
        }

        private string GetCurrentAppId()
        {
            ServerDefinitionProvider serverDefinitionProvider =
                ServiceLocator.GetRequiredService<ServerDefinitionProvider>();

            return serverDefinitionProvider.GetAppIdByGameMode(this.game_mode);
        }

        public async Task SetGameModeAsync(string gameMode)
        {
            this.game_mode = gameMode;

            await this.SaveSettingsAsync();
        }

        public async Task SetLanguageAsync(string language)
        {
            this.language = language;

            await this.SaveSettingsAsync();
        }

        public async Task SetRenderModeAsync(string renderMode)
        {
            this.render_mode = renderMode;

            await this.SaveSettingsAsync();
        }

        public async Task SetThemeAsync(string theme)
        {
            this.theme = theme;

            await this.SaveSettingsAsync();
        }

        public string GetLastSelectedPresetNameByGameMode()
        {
            if (string.IsNullOrWhiteSpace(game_mode))
            {
                return string.Empty;
            }

            return last_selected_preset_names.TryGetValue(game_mode, out string? presetName)
                ? presetName
                : string.Empty;
        }

        public async Task SetLastSelectedPresetNameByGameModeAsync(string presetName)
        {
            if (string.IsNullOrWhiteSpace(game_mode))
            {
                return;
            }

            last_selected_preset_names[game_mode] = presetName;

            await SaveSettingsAsync();
        }

        public async Task ClearLastSelectedPresetNameByGameModeAsync()
        {
            if (string.IsNullOrWhiteSpace(game_mode))
            {
                return;
            }

            last_selected_preset_names.Remove(game_mode);

            await SaveSettingsAsync();
        }

        public Dictionary<string, string> GetServerReadingsByGameMode()
        {
            if (string.IsNullOrWhiteSpace(game_mode))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return last_server_readings.TryGetValue(game_mode, out Dictionary<string, string>? readings) && readings != null
                ? new Dictionary<string, string>(readings, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// Keeps the readings in memory without writing anything.
        ///
        /// A ping and a packet loss figure are a convenience, not something
        /// worth a write of their own: they are measured again a second
        /// after every launch, and losing them costs nothing but a moment
        /// of empty cards. Right after a block or an unblock the settings
        /// file has just been written, so these ride along with whatever is
        /// saved next instead of making one click cost two writes.
        public void SetServerReadingsByGameMode(Dictionary<string, string> readings)
        {
            if (string.IsNullOrWhiteSpace(game_mode))
            {
                return;
            }

            last_server_readings ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            if (readings.Count == 0)
            {
                last_server_readings.Remove(game_mode);
            }
            else
            {
                last_server_readings[game_mode] = new Dictionary<string, string>(readings, StringComparer.OrdinalIgnoreCase);
            }
        }

        public async Task SetServerReadingsByGameModeAsync(Dictionary<string, string> readings)
        {
            if (string.IsNullOrWhiteSpace(game_mode))
            {
                return;
            }

            SetServerReadingsByGameMode(readings);

            await SaveSettingsAsync();
        }

        public async Task SetAutoRefreshMinutesAsync(int minutes)
        {
            auto_refresh_minutes = minutes;

            await SaveSettingsAsync();
        }

        public async Task SetMinimizeToTrayAsync(bool enabled)
        {
            minimize_to_tray = enabled;

            await SaveSettingsAsync();
        }

        public async Task SetAutoApplyOnGameLaunchAsync(bool enabled)
        {
            auto_apply_on_game_launch = enabled;

            await SaveSettingsAsync();
        }

        public async Task SetLateNightNoticeShownAsync(string dateKey, bool hideForGood)
        {
            late_night_notice_last_shown = dateKey;

            if (hideForGood)
            {
                late_night_notice_hidden = true;
            }

            await SaveSettingsAsync();
        }

        public async Task SetGameSwitchNoticeHiddenAsync(bool hidden)
        {
            game_switch_notice_hidden = hidden;

            await SaveSettingsAsync();
        }

        public async Task SetClusterSwitchNoticeHiddenAsync(bool hidden)
        {
            cluster_switch_notice_hidden = hidden;

            await SaveSettingsAsync();
        }

        // A method rather than a property, so it never ends up in settings.json
        public bool HasHiddenNotices()
        {
            return late_night_notice_hidden || game_switch_notice_hidden || cluster_switch_notice_hidden;
        }

        // Undoes every "Don't show again" and "Ok, don't ask again" at once, from Settings.
        // The late night notice keeps its once a day limit, so one already shown today
        // waits until tomorrow
        public async Task ShowHiddenNoticesAgainAsync()
        {
            late_night_notice_hidden = false;
            game_switch_notice_hidden = false;
            cluster_switch_notice_hidden = false;

            await SaveSettingsAsync();
        }

        public List<string> GetBlockedServerKeysByGameMode()
        {
            if (string.IsNullOrWhiteSpace(game_mode))
            {
                return [];
            }

            return blocked_server_keys.TryGetValue(game_mode, out List<string>? serverKeys)
                ? serverKeys ?? []
                : [];
        }

        public async Task SetBlockedServerKeysByGameModeAsync(IEnumerable<string> serverKeys)
        {
            if (string.IsNullOrWhiteSpace(game_mode))
            {
                return;
            }

            blocked_server_keys ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            List<string> normalizedServerKeys = serverKeys
                .Where(serverKey => !string.IsNullOrWhiteSpace(serverKey))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(serverKey => serverKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedServerKeys.Count == 0)
            {
                blocked_server_keys.Remove(game_mode);
            }
            else
            {
                blocked_server_keys[game_mode] = normalizedServerKeys;
            }

            await SaveSettingsAsync();
        }

        public List<PresetModel> GetPresetsByGameMode(string gameMode)
        {
            string normalizedGameMode = gameMode ?? string.Empty;

            return (server_presets ?? [])
                .Where(preset => (preset.GameMode ?? string.Empty).Equals(normalizedGameMode, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public PresetModel? GetPresetByGameMode(string gameMode, string presetName)
        {
            string normalizedGameMode = gameMode ?? string.Empty;
            string normalizedPresetName = presetName ?? string.Empty;

            return (server_presets ?? []).FirstOrDefault(preset =>
                (preset.GameMode ?? string.Empty).Equals(normalizedGameMode, StringComparison.OrdinalIgnoreCase) &&
                (preset.Name ?? string.Empty).Equals(normalizedPresetName, StringComparison.OrdinalIgnoreCase));
        }

        public bool HasDuplicatePresetNameByCurrentGameMode(string presetName)
        {
            string normalizedPresetName = presetName ?? string.Empty;

            return (server_presets ?? [])
                .Count(preset =>
                    (preset.GameMode ?? string.Empty).Equals(this.game_mode, StringComparison.OrdinalIgnoreCase) &&
                    (preset.Name ?? string.Empty).Equals(normalizedPresetName, StringComparison.OrdinalIgnoreCase)
                ) > 1;
        }

        public async Task AddOrUpdatePresetAsync(PresetModel preset)
        {
            server_presets ??= [];

            PresetModel? existingPreset = GetPresetByGameMode(preset.GameMode, preset.Name);

            List<string> blockedServerKeys = NormalizeServerKeys(preset.BlockedServerKeys);

            List<string>? knownServerKeys = preset.KnownServerKeys == null
                ? null
                : NormalizeServerKeys(preset.KnownServerKeys);

            if (existingPreset == null)
            {
                server_presets.Add(new PresetModel
                {
                    Name = preset.Name,
                    GameMode = preset.GameMode,
                    IsClustered = preset.IsClustered,
                    BlockedServerKeys = blockedServerKeys,
                    KnownServerKeys = knownServerKeys,
                });
            }
            else
            {
                existingPreset.IsClustered = preset.IsClustered;
                existingPreset.BlockedServerKeys = blockedServerKeys;

                // Null only when no server list was loaded, the next load fills it in again
                existingPreset.KnownServerKeys = knownServerKeys;
            }

            await SaveSettingsAsync();
        }

        public async Task RemovePresetAsync(string gameMode, string presetName)
        {
            server_presets ??= [];
            server_presets.RemoveAll(preset =>
                preset.GameMode.Equals(gameMode, StringComparison.OrdinalIgnoreCase) &&
                preset.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));

            await SaveSettingsAsync();
        }

        // Presets saved by versions before 2.1.0 do not know which servers existed when they were
        // made. They learn the current list once, so any server Valve adds from then on is new to
        // them and stays blocked. Other games' presets wait until that game's servers are loaded
        public async Task<bool> InitializeKnownServerKeysByGameModeAsync(
            string gameMode,
            IReadOnlyCollection<string> clusteredServerKeys,
            IReadOnlyCollection<string> unclusteredServerKeys
            )
        {
            if (string.IsNullOrWhiteSpace(gameMode))
            {
                return false;
            }

            server_presets ??= [];

            bool presetsChanged = false;

            foreach (PresetModel preset in server_presets.Where(preset =>
                         (preset.KnownServerKeys?.Count ?? 0) == 0 &&
                         (preset.GameMode ?? string.Empty).Equals(gameMode, StringComparison.OrdinalIgnoreCase)))
            {
                List<string> currentServerKeys = NormalizeServerKeys(preset.IsClustered
                    ? clusteredServerKeys
                    : unclusteredServerKeys);

                // An empty list means the servers did not load, so there is nothing to learn yet
                if (currentServerKeys.Count == 0)
                {
                    continue;
                }

                preset.KnownServerKeys = currentServerKeys;
                presetsChanged = true;
            }

            if (!presetsChanged)
            {
                return false;
            }

            await SaveSettingsAsync();

            return true;
        }

        private static List<string> NormalizeServerKeys(IEnumerable<string>? serverKeys)
        {
            return (serverKeys ?? [])
                .Where(serverKey => !string.IsNullOrWhiteSpace(serverKey))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(serverKey => serverKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
