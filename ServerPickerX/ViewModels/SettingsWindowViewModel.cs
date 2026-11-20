using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServerPickerX.Models;
using ServerPickerX.Services.DependencyInjection;
using ServerPickerX.Services.Loggers;
using ServerPickerX.Services.MessageBoxes;
using ServerPickerX.Services.Servers;
using ServerPickerX.Services.SystemFirewalls;
using ServerPickerX.Settings;
using ServerPickerX.Views;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace ServerPickerX.ViewModels
{
    public partial class SettingsWindowViewModel : ViewModelBase
    {
        public bool VersionCheckOnStartup { get; set; }

        // Whether anything has been hidden with "Don't show again", which is when the
        // button that brings it all back has something to do
        [ObservableProperty]
        private bool hasHiddenNotices;

        private readonly ILoggerService _loggerService;
        private readonly IMessageBoxService _messageBoxService;
        private readonly ISystemFirewallService _systemFirewallService;
        private readonly JsonSetting _jsonSetting;

        // Parameterless constructor, allows design previewer to instantiate this class since it doesn't support DI
        public SettingsWindowViewModel()
        {
            _loggerService = ServiceLocator.GetRequiredService<ILoggerService>();
            _messageBoxService = ServiceLocator.GetRequiredService<IMessageBoxService>();
            _systemFirewallService = ServiceLocator.GetRequiredService<ISystemFirewallService>();
            _jsonSetting = ServiceLocator.GetRequiredService<JsonSetting>();

            VersionCheckOnStartup = _jsonSetting.version_check_on_startup;

            HasHiddenNotices = _jsonSetting.HasHiddenNotices();
        }

        // DI constructor, allows inversion of control and unit tests mocking
        public SettingsWindowViewModel(
            ILoggerService loggerService,
            IMessageBoxService messageBoxService,
            ISystemFirewallService systemFirewallService,
            JsonSetting jsonSetting
            )
        {
            _loggerService = loggerService;
            _messageBoxService = messageBoxService;
            _systemFirewallService = systemFirewallService;
            _jsonSetting = jsonSetting;

            VersionCheckOnStartup = _jsonSetting.version_check_on_startup;

            HasHiddenNotices = _jsonSetting.HasHiddenNotices();
        }

        [RelayCommand]
        public async Task VersionCheckerToggleCommand()
        {
            _jsonSetting.version_check_on_startup = VersionCheckOnStartup;

            await _jsonSetting.SaveSettingsAsync();
        }

        public async Task ShowHiddenNoticesCommand()
        {
            await _jsonSetting.ShowHiddenNoticesAgainAsync();

            HasHiddenNotices = false;
        }

        public async Task ResetFirewallCommand()
        {
            try
            {
                // Windows finds its own rules by name, Linux needs the servers to
                // delete precisely rather than flushing the table
                ObservableCollection<ServerModel> serverModels =
                    MainWindow.Instance?.DataContext is MainWindowViewModel mainViewModel
                        ? new ObservableCollection<ServerModel>(mainViewModel.ServerModels)
                        : [];

                bool rulesRemoved = await _systemFirewallService.ResetFirewallAsync(serverModels);

                // Cancelled, the rules are all still there, so the cards must keep showing them
                if (!rulesRemoved)
                {
                    return;
                }

                if (MainWindow.Instance?.DataContext is MainWindowViewModel viewModelToRefresh)
                {
                    await viewModelToRefresh.ClearBlockedStateAsync();
                }
            }
            catch (FirewallOperationException ex)
            {
                await _loggerService.LogErrorAsync("Some firewall rules could not be removed.", ex.Message);

                // Linux takes the rules off server by server, so some can be gone
                // while others are still there. The cards show exactly that, and
                // the message names the ones that are still blocked
                if (MainWindow.Instance?.DataContext is MainWindowViewModel partlyResetViewModel)
                {
                    await partlyResetViewModel.ClearBlockedStateAsync(ex.FailedServers);

                    await partlyResetViewModel.ShowFirewallFailureAsync(false, ex.FailedServers);
                }
            }
            catch (Exception ex)
            {
                await _loggerService.LogErrorAsync("An error has occurred while resetting firewall.", ex.Message);

                await _messageBoxService.ShowMessageBoxAsync(
                    "Error",
                    "Oops! Something went wrong. Please upload the log file to GitHub."
                    );

            }
        }
    }
}
