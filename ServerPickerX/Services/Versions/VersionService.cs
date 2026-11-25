using ServerPickerX.Settings;
using ServerPickerX.Helpers;
using ServerPickerX.Views;
using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ServerPickerX.Services.MessageBoxes;
using ServerPickerX.Services.Loggers;
using ServerPickerX.Services.Localizations;

namespace ServerPickerX.Services.Versions
{
    public class VersionService : IVersionService
    {
        // This fork publishes its own builds, so updates are checked against it
        private const string ReleasesApiUrl = "https://api.github.com/repos/cankhut/deadlock-server-picker/releases";

        public const string ReleasesPageUrl = "https://github.com/cankhut/deadlock-server-picker/releases";

        private readonly ILoggerService _logger;
        private readonly IMessageBoxService _messageBoxService;
        private readonly ILocalizationService _localizationService;
        private readonly HttpClient _httpClient;
        private readonly JsonSetting _jsonSettings;

        // The release the download button in the footer points at. Null while this build is up
        // to date, when checking is turned off in Settings, or when GitHub could not be reached
        public AvailableRelease? AvailableUpdate { get; private set; }

        public VersionService(
            ILoggerService logger,
            IMessageBoxService messageBoxService,
            ILocalizationService localizationService,
            HttpClient httpClient,
            JsonSetting jsonSettings
            )
        {
            _logger = logger;
            _messageBoxService = messageBoxService;
            _localizationService = localizationService;
            _httpClient = httpClient;
            _jsonSettings = jsonSettings;
        }

        public async Task CheckVersionAsync()
        {
            AvailableUpdate = null;

            if (MainWindow.IsDebugBuild || !_jsonSettings.version_check_on_startup)
            {
                return;
            }

            // The client is a singleton, adding the header twice throws
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "deadlock-server-picker");
            }

            try
            {
                var res = await _httpClient.GetStreamAsync(ReleasesApiUrl);

                if (res == null)
                {
                    throw new Exception(
                        "Failed to check for newer app version!" + Environment.NewLine + Environment.NewLine +
                        "- Verify your internet connection or firewall are working and enabled" + Environment.NewLine +
                        "- Make sure to run the app as admin or with sudo level execution"
                    );
                }

                var jsonArray = (JsonArray?)await JsonArray.ParseAsync(res);

                Version currentVersion = new(Assembly.GetEntryAssembly()!.GetName()!.Version!.ToString(3));

                // Worked out before anything below can return early, so the button shows even
                // when the prompt has nothing to say
                if (jsonArray != null)
                {
                    AvailableUpdate = PickUpdate(jsonArray, currentVersion);
                }

                if (jsonArray?[0]?["tag_name"] == null)
                {
                    return;
                }

                if (!TryParseReleaseVersion(jsonArray[0]!["tag_name"]!.ToString(), out Version? latestVersion))
                {
                    return;
                }

                // Compared as versions rather than strings, so a tag suffix such as
                // "-cardui.1" cannot make an up to date build look outdated
                if (latestVersion <= currentVersion)
                {
                    return;
                }

                // prompt user to visit gh releases page for newer version
                await _messageBoxService.ShowMessageBoxWithLinkAsync(
                        _localizationService.GetLocaleValue("MessageBoxInfoTitle"),
                        _localizationService.GetLocaleValue("NewVersionDialogue"),
                        ReleasesPageUrl
                    );
            }
            catch (Exception ex)
            {
                await _logger.LogErrorAsync("Failed to check version", ex.Message);

                await _messageBoxService.ShowMessageBoxAsync("Error", ex.Message);
            }
        }

        // The release the download button points at: the newest normal release newer than this
        // build, or failing that the newest pre-release newer than it. A normal release wins over
        // a newer pre-release, because it is the one most people should take. Drafts, entries
        // without a readable tag and anything that is not a release object are passed over
        public static AvailableRelease? PickUpdate(JsonArray releases, Version currentVersion)
        {
            Version? newestRelease = null;
            Version? newestPreRelease = null;

            foreach (JsonNode? node in releases)
            {
                if (node is not JsonObject release
                    || IsTrue(release["draft"])
                    || release["tag_name"] is not JsonValue tag
                    || !tag.TryGetValue(out string? tagName)
                    || !TryParseReleaseVersion(tagName, out Version? version)
                    || version == null
                    || version <= currentVersion)
                {
                    continue;
                }

                if (IsTrue(release["prerelease"]))
                {
                    if (newestPreRelease == null || version > newestPreRelease)
                    {
                        newestPreRelease = version;
                    }
                }
                else if (newestRelease == null || version > newestRelease)
                {
                    newestRelease = version;
                }
            }

            if (newestRelease != null)
            {
                return new AvailableRelease(newestRelease, false);
            }

            return newestPreRelease != null
                ? new AvailableRelease(newestPreRelease, true)
                : null;
        }

        private static bool IsTrue(JsonNode? node)
            => node is JsonValue value && value.TryGetValue(out bool flag) && flag;

        // Accepts tags like "v1.2.0" and "v1.2.0-cardui.1", comparing only the numbers
        private static bool TryParseReleaseVersion(string tagName, out Version? version)
        {
            version = null;

            string trimmedTag = tagName.Trim().TrimStart('v', 'V');

            int suffixIndex = trimmedTag.IndexOfAny(new[] { '-', '+' });

            if (suffixIndex >= 0)
            {
                trimmedTag = trimmedTag[..suffixIndex];
            }

            return Version.TryParse(trimmedTag, out version);
        }
    }
}