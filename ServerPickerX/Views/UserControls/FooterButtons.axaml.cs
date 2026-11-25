using Avalonia;
using Avalonia.Controls;
using ServerPickerX.Services.DependencyInjection;
using ServerPickerX.Services.Localizations;
using ServerPickerX.Services.Processes;
using ServerPickerX.Services.Themes;
using ServerPickerX.Services.Versions;
using ServerPickerX.Settings;
using ServerPickerX.Views;

namespace ServerPickerX;

public partial class FooterButtons : UserControl
{
    // Bound by the theme button so its glyph tracks the current setting
    public static readonly StyledProperty<string> ThemeIconProperty =
        AvaloniaProperty.Register<FooterButtons, string>(nameof(ThemeIcon), "fa-desktop");

    public string ThemeIcon
    {
        get => GetValue(ThemeIconProperty);
        set => SetValue(ThemeIconProperty, value);
    }

    // Lets the main window refresh the glyph after settings.json is read
    public static FooterButtons? Instance { get; private set; }

    private readonly JsonSetting _jsonSetting;

    // The release the download button points at, kept for its tip
    private AvailableRelease? _availableUpdate;

    public FooterButtons()
    {
        InitializeComponent();

        Instance = this;

        _jsonSetting = ServiceLocator.GetRequiredService<JsonSetting>();

        RefreshThemeButton();

        // Attach tooltips to the footer buttons
        ToolTip.SetTip(GithubBtn, "Go to GitHub repository");
        ToolTip.SetTip(SettingsBtn, "Open settings");
    }

    private async void ThemeBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string nextTheme = ThemeService.Next(_jsonSetting.theme);

        ThemeService.Apply(nextTheme);

        RefreshThemeButton(nextTheme);

        await _jsonSetting.SetThemeAsync(nextTheme);
    }

    public void RefreshThemeButton(string? theme = null)
    {
        string currentTheme = theme ?? _jsonSetting.theme;

        ThemeIcon = ThemeService.GetIcon(currentTheme);

        ToolTip.SetTip(ThemeBtn, $"Theme: {currentTheme}");
    }

    // Shows the download button while a newer release is out, and hides it otherwise. It stays
    // in place after the update prompt is closed, for anyone who clicked Ok without reading it
    public void ShowUpdate(AvailableRelease? update)
    {
        _availableUpdate = update;

        UpdateBtn.IsVisible = update != null;

        if (update == null)
        {
            return;
        }

        UpdateDot.Classes.Set("pre-release", update.IsPreRelease);

        RefreshUpdateTip();
    }

    // Also called when the language changes, so the tip follows it
    public void RefreshUpdateTip()
    {
        if (_availableUpdate == null)
        {
            return;
        }

        string tip = ServiceLocator
            .GetRequiredService<ILocalizationService>()
            .GetLocaleValue(_availableUpdate.IsPreRelease ? "PreReleaseAvailableToolTip" : "UpdateAvailableToolTip");

        ToolTip.SetTip(UpdateBtn, string.Format(tip, _availableUpdate.Version));
    }

    private async void UpdateBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await ServiceLocator
            .GetRequiredService<IProcessService>()
            .OpenUrl(VersionService.ReleasesPageUrl);
    }

    private async void GithubBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await ServiceLocator
            .GetRequiredService<IProcessService>()
            .OpenUrl("https://github.com/cankhut/deadlock-server-picker");
    }

    private void SettingsBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = new()
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        settingsWindow.ShowDialog(MainWindow.Instance!);
        settingsWindow.Activate();
    }
}