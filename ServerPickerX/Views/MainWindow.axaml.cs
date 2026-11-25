using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using ServerPickerX.Helpers;
using ServerPickerX.Models;
using ServerPickerX.Services.DependencyInjection;
using ServerPickerX.Services.Games;
using ServerPickerX.Services.Localizations;
using ServerPickerX.Services.Loggers;
using ServerPickerX.Services.MessageBoxes;
using ServerPickerX.Services.Servers;
using ServerPickerX.Services.Themes;
using ServerPickerX.Services.Versions;
using ServerPickerX.Settings;
using ServerPickerX.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServerPickerX.Views
{
    public partial class MainWindow : Window
    {
        // Singleton instance for accessing the main window on execution lifetime
        public static MainWindow? Instance { get; private set; }

        public static bool IsDebugBuild
        {
            get
            {
                #if DEBUG
                    return true;
                #else
                    return false;
                #endif
            }
        }

        private bool _suppressPresetSelectionChanged;
        private PresetModel? _previousPreset;
        private DispatcherTimer? _autoRefreshTimer;
        private DispatcherTimer? _gameWatcherTimer;
        private TrayIcon? _trayIcon;
        private NativeMenu? _trayMenu;
        private bool _trayActionRunning;
        private bool _gameSwitching;
        private DateTime _playerCountCheckedAt = DateTime.MinValue;
        private bool _isExiting;
        private bool _blockedExitPromptAnswered;

        // Game watcher state. The game mode is captured when the watcher starts so a
        // switch mid session restarts it instead of matching one game's process
        // against another game's preset
        private string _gameWatcherGameMode = string.Empty;
        private bool _gameWasRunning;
        private bool _gameWatcherApplied;
        private bool _gameWatcherBusy;

        // What was blocked before auto apply put the preset on, and what was
        // blocked once it had. The game closing puts back the first, but only
        // while the blocks still match the second: anything changed by hand
        // during the game is the user's and is left alone
        private MainWindowViewModel.BlockedState? _blocksBeforeAutoApply;
        private MainWindowViewModel.BlockedState? _blocksAfterAutoApply;

        // While the game runs the watcher waits on its process instead of polling. The
        // generation lets a wait that ends late tell the watcher was restarted meanwhile
        private CancellationTokenSource? _gameExitWait;
        private int _gameWatcherGeneration;
        private bool _gameExitWaitSupported = true;

        private readonly ILoggerService _loggerService;
        private readonly JsonSetting _jsonSetting;
        private readonly IMessageBoxService _messageBoxService;
        private readonly IVersionService _versionService;
        private readonly ILocalizationService _localizationService;
        private readonly ServerDefinitionProvider _serverDefinitionProvider;
        private readonly IGameProcessService _gameProcessService;

        // Parameterless constructor, allows design previewer to create its own instance since it doesn't support DI
        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            _loggerService = ServiceLocator.GetRequiredService<ILoggerService>();
            _jsonSetting = ServiceLocator.GetRequiredService<JsonSetting>();
            _messageBoxService = ServiceLocator.GetRequiredService<IMessageBoxService>();
            _versionService = ServiceLocator.GetRequiredService<IVersionService>();
            _localizationService = ServiceLocator.GetRequiredService<ILocalizationService>();
            _serverDefinitionProvider = ServiceLocator.GetRequiredService<ServerDefinitionProvider>();
            _gameProcessService = ServiceLocator.GetRequiredService<IGameProcessService>();
        }

        // DI constructor, allows inversion of control and unit tests mocking
        public MainWindow(
            ILoggerService loggerService,
            JsonSetting jsonSetting,
            IMessageBoxService messageBoxService,
            IVersionService versionService,
            ILocalizationService localizationService
            )
        {
            InitializeComponent();
            Instance = this;

            _loggerService = loggerService;
            _messageBoxService = messageBoxService;
            _versionService = versionService;
            _jsonSetting = jsonSetting;
            _localizationService = localizationService;
            _serverDefinitionProvider = ServiceLocator.GetRequiredService<ServerDefinitionProvider>();
            _gameProcessService = ServiceLocator.GetRequiredService<IGameProcessService>();
        }

        private async void Window_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            await InitializeApp();
        }

        private async void GameModeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            await HandleGameModeChangeAsync();
        }

        private async void PresetComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            PresetModel? previousPreset = e.RemovedItems.Count > 0
                ? e.RemovedItems[0] as PresetModel
                : _previousPreset;

            if (_suppressPresetSelectionChanged)
            {
                return;
            }

            if (PresetComboBox?.SelectedItem is not PresetModel selectedPreset)
            {
                _previousPreset = null;
                return;
            }

            if (!PresetComboBox.IsDropDownOpen)
            {
                _previousPreset = selectedPreset;
                return;
            }

            await HandlePresetChangeAsync(selectedPreset, previousPreset);
        }

        // A click anywhere on a server card toggles its firewall rule
        private async void ServerCard_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            if (sender is not Border { DataContext: ServerModel serverModel })
            {
                return;
            }

            e.Handled = true;

            int blockedBefore = vm.BlockedServerCount;

            await vm.ToggleServerBlockAsync(serverModel);

            await ShowLateNightNoticeIfNeededAsync(vm, blockedBefore);
        }

        private void PingServerMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: ServerModel serverModel })
            {
                serverModel.PingServer();
            }
        }

        private async void BlockSlowerMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            if (sender is not MenuItem { DataContext: ServerModel serverModel })
            {
                return;
            }

            int blockedBefore = vm.BlockedServerCount;

            await vm.BlockSlowerThanAsync(serverModel);

            await ShowLateNightNoticeIfNeededAsync(vm, blockedBefore);
        }

        private async void BlockAllBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            int blockedBefore = vm.BlockedServerCount;

            await vm.BlockAllAsync();

            await ShowLateNightNoticeIfNeededAsync(vm, blockedBefore);
        }

        // Re-pings the servers and asks Steam for a fresh player count
        private void RefreshBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            vm.PingServers(vm.ServerModels);

            _ = RefreshPlayerCountAsync(vm);
        }

        private void SortBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            if (sender is not Button { Tag: string sortTag })
            {
                return;
            }

            ServerSortField sortField = sortTag switch
            {
                "ping" => ServerSortField.Ping,
                "loss" => ServerSortField.PacketLoss,
                _ => ServerSortField.Location,
            };

            // Clicking the active chip flips direction and re-applies the sort
            if (vm.SortField == sortField)
            {
                vm.SortDescending = !vm.SortDescending;
            }
            else
            {
                vm.SortField = sortField;
                vm.SortDescending = false;
            }

            RefreshSortChips();
        }

        private void RefreshSortChips()
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            SetSortChipState(SortLocationBtn, SortLocationArrow, vm, ServerSortField.Location);
            SetSortChipState(SortPingBtn, SortPingArrow, vm, ServerSortField.Ping);
            SetSortChipState(SortLossBtn, SortLossArrow, vm, ServerSortField.PacketLoss);
        }

        private static void SetSortChipState(
            Button chip,
            TextBlock arrow,
            MainWindowViewModel vm,
            ServerSortField sortField
            )
        {
            bool isActive = vm.SortField == sortField;

            chip.Classes.Set("active", isActive);

            arrow.IsVisible = isActive;
            arrow.Text = vm.SortDescending ? "\u2193" : "\u2191";
        }

        private void TitleBar_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            e.Handled = true;
            var parentWindow = TopLevel.GetTopLevel(this) as Window;
            parentWindow?.BeginMoveDrag(e);
        }

        private async void ClusterUnclusterBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm || !vm.ServerModelsInitialized)
            {
                return;
            }

            if (!await ConfirmClusterSwitchAsync(vm))
            {
                return;
            }

            await vm.ClusterUnclusterServersAsync();
            SyncPresetSelection(vm.SelectedPreset);
            RefreshClusterButtonContent();
        }

        /// Switching between the grouped and ungrouped views clears every
        /// block, because the two views name servers differently and a rule
        /// made in one cannot be carried into the other.
        ///
        /// Switching games does exactly the same damage and has always asked
        /// first. This one did not, so a view change quietly threw away work.
        /// Nothing is blocked means nothing to lose, and then it does not ask.
        private async Task<bool> ConfirmClusterSwitchAsync(MainWindowViewModel vm)
        {
            if (_jsonSetting.cluster_switch_notice_hidden || vm.BlockedServerCount == 0)
            {
                return true;
            }

            string okDontAskAgain = _localizationService.GetLocaleValue("OkDontAskAgain");

            string choice = await _messageBoxService.ShowMessageBoxChoiceAsync(
                _localizationService.GetLocaleValue("MessageBoxInfoTitle"),
                _localizationService.GetLocaleValue("SwapClusterUnblockAllConflict"),
                ["Ok", okDontAskAgain, "Cancel"],
                MsBox.Avalonia.Enums.Icon.Setting
                );

            // Checked first, so a translation that happens to read "Ok" cannot
            // turn the plain Ok button into the one that stops asking
            if (choice == "Ok")
            {
                return true;
            }

            if (choice == okDontAskAgain)
            {
                await _jsonSetting.SetClusterSwitchNoticeHiddenAsync(true);

                return true;
            }

            // Closing the dialog without choosing counts as not switching
            return false;
        }

        private async void PresetsBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            PresetManagerWindow presetManagementWindow = new(vm)
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            int blockedBefore = vm.BlockedServerCount;

            await presetManagementWindow.ShowDialog(this);

            // Reload presets after closing preset manager window
            vm.LoadPresetPickerItems();
            SyncPresetSelection(vm.SelectedPreset);
            RefreshClusterButtonContent();
            RefreshTrayMenu();

            await ShowLateNightNoticeIfNeededAsync(vm, blockedBefore);
        }

        public async Task InitializeApp()
        {
            await _jsonSetting.LoadSettingsAsync();

            ThemeService.Apply(_jsonSetting.theme);

            FooterButtons.Instance?.RefreshThemeButton(_jsonSetting.theme);

            await SetLanguage();

            await ConfigureControls();

            var vm = ServiceLocator.GetRequiredService<MainWindowViewModel>();

            await vm.LoadServersAsync();

            DataContext = vm;

            _ = RefreshPlayerCountAsync(vm);

            if (vm.ServersLoaded)
            {
                await SyncServersAsync(vm);
                await vm.InitializePresetServerListsAsync();
                vm.LoadPresetPickerItems();
                await vm.RestoreLastSelectedPresetAsync();
            }

            ConfigurePresetControls(vm);
            RefreshClusterButtonContent();
            RefreshSortChips();

            ConfigureAutoRefresh();
            ConfigureTrayIcon();
            ConfigureGameWatcher();

            await _versionService.CheckVersionAsync();
        }

        // Re-probes on a timer when the user has opted in. Blocked servers are skipped
        // by the sweep itself, so an idle tick costs nothing when everything is blocked
        public void ConfigureAutoRefresh()
        {
            if (_autoRefreshTimer != null)
            {
                _autoRefreshTimer.Stop();
                _autoRefreshTimer.Tick -= AutoRefreshTimer_Tick;
                _autoRefreshTimer = null;
            }

            int minutes = _jsonSetting.auto_refresh_minutes;

            if (minutes <= 0)
            {
                return;
            }

            _autoRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMinutes(minutes)
            };

            _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
            _autoRefreshTimer.Start();
        }

        private void AutoRefreshTimer_Tick(object? sender, EventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            // Skip the tick outright while hidden or mid firewall operation rather
            // than queueing work the user will never see
            if (!IsVisible || WindowState == WindowState.Minimized || !vm.IsOperationAllowed)
            {
                return;
            }

            _ = vm.PingServersAsync(vm.ServerModels);
        }

        // Watches for the current game starting and stopping so the preset the user
        // last picked is applied on launch and taken back off on exit. The process list
        // is only polled while waiting for a launch. Once the game is running its exit
        // is awaited instead, so the watcher does no work at all while you play
        public void ConfigureGameWatcher()
        {
            StopGameWatcher();

            _gameWatcherApplied = false;
            _blocksBeforeAutoApply = null;
            _blocksAfterAutoApply = null;
            _gameWatcherBusy = false;
            _gameExitWaitSupported = true;

            if (!_jsonSetting.auto_apply_on_game_launch)
            {
                return;
            }

            _gameWatcherGameMode = _jsonSetting.game_mode;

            // Seed the state from the current reality, so a game that is already running
            // when the watcher starts is not mistaken for a launch that just happened
            Process? runningGame = _gameProcessService.FindRunningGameProcess(_gameWatcherGameMode);

            _gameWasRunning = runningGame != null;

            if (runningGame != null)
            {
                _ = WaitForGameExitAsync(runningGame);

                return;
            }

            StartGameWatcherPolling();
        }

        private void StopGameWatcher()
        {
            _gameWatcherGeneration++;

            if (_gameWatcherTimer != null)
            {
                _gameWatcherTimer.Stop();
                _gameWatcherTimer.Tick -= GameWatcherTimer_Tick;
                _gameWatcherTimer = null;
            }

            _gameExitWait?.Cancel();
            _gameExitWait = null;
        }

        private void StartGameWatcherPolling()
        {
            if (_gameWatcherTimer == null)
            {
                _gameWatcherTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromSeconds(5)
                };

                _gameWatcherTimer.Tick += GameWatcherTimer_Tick;
            }

            _gameWatcherTimer.Start();
        }

        private void GameWatcherTimer_Tick(object? sender, EventArgs e)
        {
            _ = HandleGameWatcherTickAsync();
        }

        private async Task HandleGameWatcherTickAsync()
        {
            // One tick at a time, a firewall pass easily outlasts the interval
            if (_gameWatcherBusy || DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            if (!_gameWatcherGameMode.Equals(_jsonSetting.game_mode, StringComparison.OrdinalIgnoreCase))
            {
                ConfigureGameWatcher();

                return;
            }

            int generation = _gameWatcherGeneration;

            Process? runningGame = _gameProcessService.FindRunningGameProcess(_gameWatcherGameMode);

            try
            {
                bool isRunning = runningGame != null;

                if (isRunning != _gameWasRunning)
                {
                    // Wait for the app to go idle rather than queueing firewall work behind a
                    // running operation. The transition is still there on the next tick
                    if (!vm.ServersLoaded || !vm.IsOperationAllowed || _trayActionRunning)
                    {
                        return;
                    }

                    _gameWatcherBusy = true;

                    try
                    {
                        if (isRunning)
                        {
                            await ApplyPresetForGameLaunchAsync(vm);
                        }
                        else if (_gameWatcherApplied)
                        {
                            _gameWatcherApplied = false;

                            MainWindowViewModel.BlockedState? before = _blocksBeforeAutoApply;
                            MainWindowViewModel.BlockedState? after = _blocksAfterAutoApply;

                            _blocksBeforeAutoApply = null;
                            _blocksAfterAutoApply = null;

                            // Blocks changed by hand while the game ran are the user's now,
                            // and are left exactly as they are
                            if (before == null || after == null || !vm.CaptureBlockedState().SameAs(after))
                            {
                                await _loggerService.LogInfoAsync(
                                    $"Auto apply left the blocks alone after {_gameWatcherGameMode} closed, they were changed during the game"
                                    );
                            }
                            else
                            {
                                // Puts back what was blocked before the game started,
                                // including blocks the user made by hand. This used to
                                // unblock everything, and took those with it
                                bool restored = await vm.RestoreBlockedStateAsync(before);

                                SyncPresetSelection(vm.SelectedPreset);
                                RefreshClusterButtonContent();

                                await _loggerService.LogInfoAsync(restored
                                    ? $"Auto apply put back the blocks from before {_gameWatcherGameMode} started"
                                    : $"Auto apply could not fully put back the blocks from before {_gameWatcherGameMode} started"
                                    );
                            }
                        }

                        _gameWasRunning = isRunning;
                    }
                    finally
                    {
                        _gameWatcherBusy = false;
                    }
                }

                // Nothing needs polling while the game runs, its exit is awaited instead.
                // Skipped when the watcher was restarted during the firewall work above
                if (_gameWasRunning
                    && runningGame != null
                    && _gameExitWaitSupported
                    && generation == _gameWatcherGeneration)
                {
                    _ = WaitForGameExitAsync(runningGame);

                    runningGame = null;
                }
            }
            finally
            {
                runningGame?.Dispose();
            }
        }

        // Takes ownership of the process and disposes it once the wait is over
        private async Task WaitForGameExitAsync(Process gameProcess)
        {
            int generation = _gameWatcherGeneration;

            _gameWatcherTimer?.Stop();

            using CancellationTokenSource exitWait = new();

            _gameExitWait = exitWait;

            try
            {
                await gameProcess.WaitForExitAsync(exitWait.Token);
            }
            catch (OperationCanceledException)
            {
                // The watcher was restarted or switched off, or the window is closing
                return;
            }
            catch (Exception ex)
            {
                // A process that cannot be waited on, for example when access to it is
                // denied, falls back to polling for the exit the way the watcher used to
                _gameExitWaitSupported = false;

                await _loggerService.LogWarningAsync("Auto apply could not wait for the game to exit, polling instead: " + ex.Message);
            }
            finally
            {
                gameProcess.Dispose();

                if (ReferenceEquals(_gameExitWait, exitWait))
                {
                    _gameExitWait = null;
                }
            }

            if (generation != _gameWatcherGeneration)
            {
                return;
            }

            // The next tick finds the game gone and takes the preset back off, or finds it
            // running again, a relaunch or a second process, and goes back to waiting
            StartGameWatcherPolling();
        }

        private async Task ApplyPresetForGameLaunchAsync(MainWindowViewModel vm)
        {
            string presetName = _jsonSetting.GetLastSelectedPresetNameByGameMode();

            // Nothing to apply is a normal state, not an error. The user simply has not
            // picked a preset for this game yet
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return;
            }

            PresetModel? preset = vm.GetCurrentGamePreset(presetName);

            if (preset == null)
            {
                return;
            }

            MainWindowViewModel.BlockedState before = vm.CaptureBlockedState();

            bool presetApplied = await vm.ApplyPresetAsync(preset);

            MainWindowViewModel.BlockedState after = vm.CaptureBlockedState();

            // Armed whenever anything changed, not only on success. A preset that
            // only partly went on has still taken the user's own blocks off on the
            // way, and those should come back when the game closes
            if (!after.SameAs(before))
            {
                _blocksBeforeAutoApply = before;
                _blocksAfterAutoApply = after;
                _gameWatcherApplied = true;
            }

            if (!presetApplied)
            {
                await _loggerService.LogWarningAsync($"Auto apply could not apply preset '{presetName}' on game launch");

                // A preset that only partly went on is left deselected, and auto
                // apply picks the next game's preset from that selection. It is
                // put back so the picker shows the truth and the next launch
                // still has something to apply
                await vm.RestoreSelectedPresetAsync(presetName);

                SyncPresetSelection(vm.SelectedPreset);
                RefreshClusterButtonContent();

                return;
            }

            SyncPresetSelection(vm.SelectedPreset);
            RefreshClusterButtonContent();

            await _loggerService.LogInfoAsync($"Auto apply applied preset '{presetName}' after {_gameWatcherGameMode} started");
        }

        public void ConfigureTrayIcon()
        {
            if (!_jsonSetting.minimize_to_tray)
            {
                DisposeTrayIcon();

                return;
            }

            if (_trayIcon != null)
            {
                // Already there, only the menu has to catch up with the game and its presets
                RefreshTrayMenu();

                return;
            }

            try
            {
                _trayMenu = new NativeMenu();

                RefreshTrayMenu();

                _trayIcon = new TrayIcon
                {
                    Icon = new WindowIcon(AssetLoader.Open(ResourceHelper.CreateResourceUriFromPath("/Assets/favicon.ico"))),
                    ToolTipText = "Deadlock Server Picker",
                    IsVisible = true,
                    Menu = _trayMenu
                };

                _trayIcon.Clicked += (_, _) => RestoreFromTray();
            }
            catch (Exception)
            {
                // A desktop without a system tray just keeps the window on close
                _trayIcon = null;
                _trayMenu = null;
            }
        }

        // Show, the current game's presets, Unblock All and Quit. Rebuilt when the game, the
        // presets or the language change, so it never offers a preset that is gone
        public void RefreshTrayMenu()
        {
            if (_trayMenu == null)
            {
                return;
            }

            _trayMenu.Items.Clear();

            NativeMenuItem showItem = new(_localizationService.GetLocaleValue("TrayShow"));
            showItem.Click += (_, _) => RestoreFromTray();
            _trayMenu.Add(showItem);

            _trayMenu.Add(new NativeMenuItemSeparator());

            List<PresetModel> presets = _jsonSetting.GetPresetsByGameMode(_jsonSetting.game_mode);

            if (presets.Count > 0)
            {
                NativeMenu presetMenu = new();

                foreach (PresetModel preset in presets)
                {
                    string presetName = preset.Name;

                    // A single underscore would be read as a keyboard shortcut marker and vanish
                    NativeMenuItem presetItem = new(presetName.Replace("_", "__"));
                    presetItem.Click += async (_, _) => await ApplyPresetFromTrayAsync(presetName);
                    presetMenu.Add(presetItem);
                }

                NativeMenuItem presetsItem = new(_localizationService.GetLocaleValue("Presets"))
                {
                    Menu = presetMenu
                };

                _trayMenu.Add(presetsItem);
            }

            NativeMenuItem unblockAllItem = new(_localizationService.GetLocaleValue("UnblockAll"));
            unblockAllItem.Click += async (_, _) => await UnblockAllFromTrayAsync();
            _trayMenu.Add(unblockAllItem);

            _trayMenu.Add(new NativeMenuItemSeparator());

            NativeMenuItem quitItem = new(_localizationService.GetLocaleValue("TrayQuit"));
            quitItem.Click += (_, _) => ExitApplication();
            _trayMenu.Add(quitItem);
        }

        // A tray click while something is already running, or while the game is being switched,
        // is ignored rather than queued, so it can never act on the wrong game's servers
        private bool CanRunTrayAction(out MainWindowViewModel? vm)
        {
            vm = DataContext as MainWindowViewModel;

            return !_trayActionRunning
                && !_gameSwitching
                && !_gameWatcherBusy
                && vm != null
                && vm.ServersLoaded
                && vm.IsOperationAllowed;
        }

        private async Task ApplyPresetFromTrayAsync(string presetName)
        {
            if (!CanRunTrayAction(out MainWindowViewModel? vm) || vm == null)
            {
                return;
            }

            _trayActionRunning = true;

            try
            {
                PresetModel? preset = vm.GetCurrentGamePreset(presetName);

                if (preset == null)
                {
                    RefreshTrayMenu();

                    return;
                }

                bool presetApplied = await vm.ApplyPresetAsync(preset);

                if (!presetApplied)
                {
                    return;
                }

                SyncPresetSelection(vm.SelectedPreset);
                RefreshClusterButtonContent();
            }
            catch (Exception)
            {
                // A click handler must never take the app down. Firewall errors are
                // already reported by the view model itself
            }
            finally
            {
                _trayActionRunning = false;
            }
        }

        private async Task UnblockAllFromTrayAsync()
        {
            if (!CanRunTrayAction(out MainWindowViewModel? vm) || vm == null)
            {
                return;
            }

            _trayActionRunning = true;

            try
            {
                bool unblocked = await vm.UnblockAllAsync();

                if (unblocked)
                {
                    SyncPresetSelection(vm.SelectedPreset);
                }
            }
            catch (Exception)
            {
                // Same as above, reported by the view model when it is a firewall error
            }
            finally
            {
                _trayActionRunning = false;
            }
        }

        private void DisposeTrayIcon()
        {
            if (_trayIcon == null)
            {
                return;
            }

            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
            _trayMenu = null;
        }

        private void RestoreFromTray()
        {
            Show();

            WindowState = WindowState.Normal;

            Activate();

            // The count can be hours old after sitting in the tray. Not when the window only
            // came back to ask about quitting
            if (!_isExiting
                && DataContext is MainWindowViewModel vm
                && DateTime.UtcNow - _playerCountCheckedAt > TimeSpan.FromMinutes(1))
            {
                _ = RefreshPlayerCountAsync(vm);
            }
        }

        private async Task RefreshPlayerCountAsync(MainWindowViewModel vm)
        {
            _playerCountCheckedAt = DateTime.UtcNow;

            try
            {
                string appId = _serverDefinitionProvider.GetAppIdByGameMode(_jsonSetting.game_mode);

                await vm.RefreshPlayerCountAsync(appId);
            }
            catch (InvalidOperationException)
            {
                // A game without a server definition has no Steam app to ask about
            }
        }

        // Explains that late night queues are slow because fewer people play, not because of
        // the app. Only follows a block done in this window, never auto apply or the tray,
        // and shows at most once a day until the user picks "Don't show again"
        private async Task ShowLateNightNoticeIfNeededAsync(MainWindowViewModel vm, int blockedBefore)
        {
            // Closed to the tray or minimized while the block was running, so not now
            if (!IsVisible || WindowState == WindowState.Minimized)
            {
                return;
            }

            if (vm.BlockedServerCount <= blockedBefore)
            {
                return;
            }

            DateTime now = DateTime.Now;

            bool shouldShow = LateNightNotice.ShouldShow(
                now,
                vm.BlockedServerCount,
                vm.ServerModels.Count,
                _jsonSetting.late_night_notice_hidden,
                _jsonSetting.late_night_notice_last_shown
                );

            if (!shouldShow)
            {
                return;
            }

            string dateKey = LateNightNotice.ToDateKey(now);

            // Saved before it shows, so blocking again while it is open cannot queue a second one
            await _jsonSetting.SetLateNightNoticeShownAsync(dateKey, false);

            string dontShowAgain = _localizationService.GetLocaleValue("DontShowAgain");

            string choice = await _messageBoxService.ShowMessageBoxChoiceAsync(
                _localizationService.GetLocaleValue("MessageBoxInfoTitle"),
                _localizationService.GetLocaleValue("LateNightNoticeDialogue"),
                ["Ok", dontShowAgain]
                );

            if (choice == dontShowAgain)
            {
                await _jsonSetting.SetLateNightNoticeShownAsync(dateKey, true);
            }
        }

        private void ExitApplication()
        {
            _isExiting = true;

            // The tray icon is disposed on the real close path, not here, so a
            // cancelled exit does not leave the app hidden with no way back to it
            Close();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!_isExiting && _jsonSetting.minimize_to_tray && _trayIcon != null)
            {
                e.Cancel = true;

                Hide();

                return;
            }

            // Firewall rules outlive the app, so leaving servers blocked is a decision
            // the user should make knowingly rather than discover weeks later
            if (!_blockedExitPromptAnswered
                && DataContext is MainWindowViewModel viewModel
                && viewModel.BlockedServerCount > 0)
            {
                e.Cancel = true;

                // Quitting from the tray leaves the window hidden, and a dialog owned
                // by a hidden window never reaches the user
                RestoreFromTray();

                _ = ConfirmExitWithBlockedServersAsync(viewModel);

                return;
            }

            _autoRefreshTimer?.Stop();
            StopGameWatcher();

            DisposeTrayIcon();

            base.OnClosing(e);
        }

        private async Task ConfirmExitWithBlockedServersAsync(MainWindowViewModel viewModel)
        {
            string keepBlocked = _localizationService.GetLocaleValue("BlockedOnExitKeep");
            string unblockAll = _localizationService.GetLocaleValue("BlockedOnExitUnblock");
            string cancel = _localizationService.GetLocaleValue("BlockedOnExitCancel");

            string choice = await _messageBoxService.ShowMessageBoxChoiceAsync(
                _localizationService.GetLocaleValue("MessageBoxInfoTitle"),
                string.Format(
                    _localizationService.GetLocaleValue("BlockedOnExitDialogue"),
                    viewModel.BlockedServerCount
                    ),
                [unblockAll, keepBlocked, cancel],
                MsBox.Avalonia.Enums.Icon.Warning
                );

            // Dismissing the dialog leaves the app open rather than guessing, and the
            // pending exit is cancelled so closing the window still minimises to tray
            if (choice != keepBlocked && choice != unblockAll)
            {
                _isExiting = false;

                return;
            }

            // The unblock not going through means the reason has just been shown.
            // Quitting anyway would leave rules in the firewall with nothing
            // running to take them off, the opposite of what was asked for
            if (choice == unblockAll && !await viewModel.UnblockAllAsync())
            {
                _isExiting = false;

                return;
            }

            _blockedExitPromptAnswered = true;
            _isExiting = true;

            Close();
        }

        private async Task SetLanguage()
        {
            // Extract language code from enum text
            var language = _jsonSetting.language.Replace(" ", "").Split("|")[1];

            await _localizationService.SetLanguage(language);
        }

        private async Task ConfigureControls()
        {
            try
            {
                IReadOnlyList<string> gameModes = _serverDefinitionProvider.GetGameModes();

                if (gameModes.Count == 0)
                {
                    throw new InvalidOperationException("No server definitions were found.");
                }

                // A saved game that is no longer listed falls back to the first one, Deadlock
                if (!gameModes.Contains(_jsonSetting.game_mode, StringComparer.OrdinalIgnoreCase))
                {
                    await _jsonSetting.SetGameModeAsync(gameModes[0]);
                }

                GameModeComboBox.SelectionChanged -= GameModeComboBox_SelectionChanged;
                GameModeComboBox.ItemsSource = gameModes;
                GameModeComboBox.SelectedItem = _jsonSetting.game_mode;
                GameModeComboBox.SelectionChanged += GameModeComboBox_SelectionChanged;
            }
            catch (InvalidOperationException ex)
            {
                await _loggerService.LogErrorAsync("An error has occured while setting game mode combo box", ex.Message);

                throw;
            }

            RefreshClusterButtonContent();
        }

        private void ConfigurePresetControls(MainWindowViewModel vm)
        {
            SyncPresetSelection(vm.SelectedPreset);
        }

        private async Task SyncServersAsync(MainWindowViewModel vm)
        {
            var localRevision = await _jsonSetting.GetRevisionByGameModeAsync();

            var fetchedRevision = vm.GetServerDataService().GetFetchedRevision();

            // Every server Valve lists for this game right now, by code and name, kept so the
            // next update from Valve can be compared with it. Taken from the full list, so it is
            // the same whether the servers are grouped or not
            Dictionary<string, string> currentServers = new(StringComparer.Ordinal);

            foreach (ServerModel serverModel in vm.GetServerDataService().GetServerData().UnclusteredServers)
            {
                currentServers.TryAdd(serverModel.Name, serverModel.Description);
            }

            string appId = _serverDefinitionProvider.GetAppIdByGameMode(_jsonSetting.game_mode);
            
            IReadOnlyList<string> affectedGameModes = _serverDefinitionProvider.GetGameModesByAppId(appId);
            
            bool hasAffectedPresets = affectedGameModes.Any(
                    gameMode => _jsonSetting.GetPresetsByGameMode(gameMode).Count > 0
                );

            // Store the initial revision without a reset when this game has no saved presets yet.
            // The list goes out in the same write as the revision
            if (localRevision == "-1" && !hasAffectedPresets)
            {
                _jsonSetting.RememberServerListByGameMode(currentServers);

                await _jsonSetting.SetRevisionByGameModeAsync(fetchedRevision);
                return;
            }

            // Skip server unblocking and revision sync if local revision is equal to fetched revision.
            // The list is still kept up to date, at the cost of a write only when it changed: the
            // first launch of this version, or a game that shares Valve's data with another one
            if (localRevision == fetchedRevision)
            {
                if (_jsonSetting.RememberServerListByGameMode(currentServers))
                {
                    await _jsonSetting.SaveSettingsAsync();
                }

                return;
            }

            // Says which servers came and went since the list was last up to date. There is
            // nothing to compare with the first time, and the message then reads as it always has
            string message = _localizationService.GetLocaleValue("SyncServersUnblockAllDialogue");

            string? changes = ServerListChanges.Describe(
                _jsonSetting.GetLastServerListByGameMode(),
                currentServers,
                _localizationService.GetLocaleValue("SyncServersAdded"),
                _localizationService.GetLocaleValue("SyncServersRemoved"),
                _localizationService.GetLocaleValue("SyncServersUnchanged"),
                _localizationService.GetLocaleValue("SyncServersMore")
                );

            if (changes != null)
            {
                message += "\n\n" + changes;
            }

            // This only happens on successful load and sync on startup or game switch
            await _messageBoxService.ShowMessageBoxAsync(
                    _localizationService.GetLocaleValue("MessageBoxInfoTitle"),
                    message,
                    MsBox.Avalonia.Enums.Icon.Setting
                    );

            // Unblock current game rules while preserving last selected preset
            bool unblocked = await vm.UnblockAllAsync(shouldClearLastSelectedPreset: false);

            if (!unblocked)
            {
                return;
            }

            // Presets are left as they are. A server Valve takes away for a while comes back the
            // way the user left it, and one that is new starts out blocked (PresetModel.BlocksServer).
            // The list is only kept once the update went through, so one that could not finish is
            // described the same way again next time, and it goes out in the same write as the revision
            _jsonSetting.RememberServerListByGameMode(currentServers);

            await _jsonSetting.SetRevisionByGameModeAsync(fetchedRevision);
        }

        private async Task HandleGameModeChangeAsync()
        {
            if (DataContext is not MainWindowViewModel vm || GameModeComboBox?.SelectedItem == null)
            {
                return;
            }

            if (!await ConfirmGameSwitchAsync())
            {
                // Revert back selection without triggering event handler
                GameModeComboBox.SelectionChanged -= GameModeComboBox_SelectionChanged;
                GameModeComboBox.SelectedItem = _jsonSetting.game_mode;
                GameModeComboBox.SelectionChanged += GameModeComboBox_SelectionChanged;

                return;
            }

            _gameSwitching = true;

            try
            {
                // Clear the currently loaded game rules before changing game mode while preserving last selected preset
                await vm.UnblockAllAsync(shouldClearLastSelectedPreset: false);

                // Update json setting game mode and serialize it
                await _jsonSetting.SetGameModeAsync((string)GameModeComboBox.SelectedItem);

                await InitializeApp();
            }
            finally
            {
                _gameSwitching = false;
            }
        }

        // Switching games clears this game's rules first, so the user is warned before it
        // happens. Someone who switches often can turn the warning off, and the switch then
        // goes ahead straight away. It still unblocks either way, the app just stops saying so
        private async Task<bool> ConfirmGameSwitchAsync()
        {
            if (_jsonSetting.game_switch_notice_hidden)
            {
                return true;
            }

            string okDontAskAgain = _localizationService.GetLocaleValue("OkDontAskAgain");

            string choice = await _messageBoxService.ShowMessageBoxChoiceAsync(
                _localizationService.GetLocaleValue("MessageBoxInfoTitle"),
                _localizationService.GetLocaleValue("SwapGameModeUnblockAllConflict"),
                ["Ok", okDontAskAgain, "Cancel"],
                MsBox.Avalonia.Enums.Icon.Setting
                );

            // Checked first, so a translation that happens to read "Ok" cannot turn the
            // plain Ok button into the one that stops asking
            if (choice == "Ok")
            {
                return true;
            }

            if (choice == okDontAskAgain)
            {
                await _jsonSetting.SetGameSwitchNoticeHiddenAsync(true);

                return true;
            }

            // Closing the dialog without choosing counts as not switching
            return false;
        }

        private async Task HandlePresetChangeAsync(
            PresetModel selectedPreset,
            PresetModel? previousPreset
            )
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            if (AreSamePresetSelection(selectedPreset, previousPreset))
            {
                return;
            }

            int blockedBefore = vm.BlockedServerCount;

            bool presetApplied = await vm.ApplyPresetAsync(selectedPreset);

            if (!presetApplied)
            {
                SyncPresetSelection(previousPreset);
                return;
            }

            SyncPresetSelection(vm.SelectedPreset);
            RefreshClusterButtonContent();

            await ShowLateNightNoticeIfNeededAsync(vm, blockedBefore);
        }

        private void SyncPresetSelection(PresetModel? preset)
        {
            _suppressPresetSelectionChanged = true;
            PresetComboBox.SelectedItem = preset;
            _suppressPresetSelectionChanged = false;
            _previousPreset = preset;
        }

        private static bool AreSamePresetSelection(PresetModel? left, PresetModel? right)
        {
            if (left == null || right == null)
            {
                return left == null && right == null;
            }

            return left.Equals(right);
        }

        private void RefreshClusterButtonContent()
        {
            ClusterUnclusterBtn.Content = _localizationService.GetLocaleValue(
                _jsonSetting.is_clustered ? "UnclusterServers" : "ClusterServers"
                );
        }

    }
}
