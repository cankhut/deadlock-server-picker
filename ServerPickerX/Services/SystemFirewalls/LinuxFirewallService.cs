using ServerPickerX.Models;
using ServerPickerX.Services.Localizations;
using ServerPickerX.Services.Loggers;
using ServerPickerX.Services.MessageBoxes;
using ServerPickerX.Services.Processes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ServerPickerX.Services.SystemFirewalls
{
    public class LinuxFirewallService(
        ILoggerService _loggerService,
        ILocalizationService _localizationService,
        IMessageBoxService _messageBoxService,
        IProcessService _processService
        ) : ISystemFirewallService
    {
        // Waits a few seconds for the iptables lock instead of failing outright.
        // Docker and fail2ban both hold it briefly, and a rule that was not added
        // because something else held the lock for a moment is a block the user
        // asked for and did not get
        private const string LockWait = "-w 5";

        // Every rule this app adds carries this comment. It is how the app tells its own
        // rules apart from everyone else's, the way the Windows rules carry the
        // server_picker_x_ name prefix, and it is plain enough that iptables never quotes it
        public const string RuleComment = "server_picker_x";

        // Outbound, like the Windows rules, and inserted at the top of the chain instead of
        // appended to it. A stateful firewall such as ufw accepts replies to traffic this PC
        // started before an appended rule is ever reached, so the inbound DROP that 2.1.2
        // and earlier appended did nothing at all on those systems. An outbound rule at the
        // top stops the packet before it leaves, whatever the rest of the chain says
        private const string OutboundRule = "OUTPUT -d {0} -m comment --comment " + RuleComment + " -j DROP";

        // The rule 2.1.2 and earlier added. Nothing adds it any more, but rules outlive the
        // app, so it is still read, and still taken off when its server is unblocked
        private const string InboundRule = "INPUT -s {0} -j DROP";

        // How "iptables -S" prints the two rules above, token for token. The null is where
        // the address goes. A rule with anything more or less in it is someone else's
        private static readonly string?[] OutboundShape =
            ["-A", "OUTPUT", "-d", null, "-m", "comment", "--comment", RuleComment, "-j", "DROP"];

        private static readonly string?[] InboundShape =
            ["-A", "INPUT", "-s", null, "-j", "DROP"];

        // Both passes read the rules once first, then only add what is missing and
        // only remove what is there. Every server is tried, and the ones that fail
        // are reported together at the end so the app can show each server as it
        // really is
        public async Task BlockServersAsync(ObservableCollection<ServerModel> serverModels)
        {
            AppRules rules = await ReadAppRulesAsync();

            List<ServerModel> failedServers = [];

            foreach (var serverModel in serverModels)
            {
                try
                {
                    // An empty address list would turn into "-d -m comment ...", which
                    // iptables rejects, so it is reported as the failure it is
                    if (serverModel.RelayModels.Count == 0)
                    {
                        throw new InvalidOperationException("the server has no addresses to block");
                    }

                    // An address that already has a rule does not get a second one.
                    // A rule left by an older version does not count: it may be the
                    // appended inbound kind that a stateful firewall walks past, so the
                    // outbound rule is added next to it
                    List<string> missingAddresses = GetAddresses(serverModel)
                        .Where(address => rules.Outbound.GetValueOrDefault(address) == 0)
                        .ToList();

                    if (missingAddresses.Count == 0)
                    {
                        continue;
                    }

                    await RunIptablesAsync($"{LockWait} -I {string.Format(OutboundRule, string.Join(",", missingAddresses))}");

                    foreach (string address in missingAddresses)
                    {
                        rules.Outbound[address] = 1;
                    }
                }
                catch (Exception ex)
                {
                    failedServers.Add(serverModel);

                    await _loggerService.LogErrorAsync($"Could not block {serverModel.Description}", ex.Message);
                }
            }

            if (failedServers.Count > 0)
            {
                throw new FirewallOperationException(failedServers);
            }
        }

        public async Task UnblockServersAsync(ObservableCollection<ServerModel> serverModels)
        {
            AppRules rules = await ReadAppRulesAsync();

            List<ServerModel> failedServers = [];

            foreach (var serverModel in serverModels)
            {
                try
                {
                    await RemoveServerRulesAsync(serverModel, rules);
                }
                catch (Exception ex)
                {
                    failedServers.Add(serverModel);

                    await _loggerService.LogErrorAsync($"Could not unblock {serverModel.Description}", ex.Message);
                }
            }

            if (failedServers.Count > 0)
            {
                throw new FirewallOperationException(failedServers);
            }
        }

        // Takes off every copy of every rule for this server's addresses, the ones this
        // version adds and the ones older versions added. Hands back how many went
        private async Task<int> RemoveServerRulesAsync(ServerModel serverModel, AppRules rules)
        {
            List<string> addresses = GetAddresses(serverModel).ToList();

            int removed = await RemoveEveryCopyAsync(addresses, rules.Outbound, OutboundRule);

            removed += await RemoveEveryCopyAsync(addresses, rules.Inbound, InboundRule);

            return removed;
        }

        // A server with nothing in the firewall needs no command at all: iptables treats
        // deleting a rule that is not there as an error, and "Unblock all" goes through
        // every server, blocked or not. Each round lowers the counts, so this always
        // comes to an end
        private async Task<int> RemoveEveryCopyAsync(IReadOnlyList<string> addresses, Dictionary<string, int> copies, string rule)
        {
            int removed = 0;

            while (true)
            {
                List<string> presentAddresses = addresses
                    .Where(address => copies.GetValueOrDefault(address) > 0)
                    .ToList();

                if (presentAddresses.Count == 0)
                {
                    return removed;
                }

                await RunIptablesAsync($"{LockWait} -D {string.Format(rule, string.Join(",", presentAddresses))}");

                foreach (string address in presentAddresses)
                {
                    copies[address]--;
                }

                removed += presentAddresses.Count;
            }
        }

        private static IEnumerable<string> GetAddresses(ServerModel serverModel)
            => serverModel.RelayModels.Select(relayModel => relayModel.IPv4).Distinct();

        // When the rules cannot be read at all nothing has been changed yet, so that
        // failure is all or nothing
        private async Task<AppRules> ReadAppRulesAsync(bool mayPrompt = true)
        {
            string outbound = await RunIptablesAsync($"{LockWait} -S OUTPUT", mayPrompt);
            string inbound = await RunIptablesAsync($"{LockWait} -S INPUT", mayPrompt);

            return new AppRules(ParseOutboundRules(outbound), ParseInboundRules(inbound));
        }

        // "-A OUTPUT -d 155.133.226.68/32 -m comment --comment server_picker_x -j DROP",
        // the rules this version adds, as address and number of copies
        public static Dictionary<string, int> ParseOutboundRules(string listing)
            => ParseRules(listing, OutboundShape);

        // "-A INPUT -s 155.133.226.68/32 -j DROP", the rules 2.1.2 and earlier added. They
        // carry no comment, so the exact shape is all there is to go on
        public static Dictionary<string, int> ParseInboundRules(string listing)
            => ParseRules(listing, InboundShape);

        private static Dictionary<string, int> ParseRules(string listing, string?[] shape)
        {
            Dictionary<string, int> rules = new(StringComparer.Ordinal);

            foreach (string line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length != shape.Length)
                {
                    continue;
                }

                string? address = null;
                bool sameShape = true;

                for (int i = 0; i < shape.Length; i++)
                {
                    if (shape[i] == null)
                    {
                        address = parts[i];
                    }
                    else if (!string.Equals(parts[i], shape[i], StringComparison.Ordinal))
                    {
                        sameShape = false;

                        break;
                    }
                }

                if (!sameShape || address == null)
                {
                    continue;
                }

                // iptables prints one address as 155.133.226.68/32. This app only ever adds
                // rules for single addresses, so a range is someone else's, comment or not
                if (address.EndsWith("/32", StringComparison.Ordinal))
                {
                    address = address[..^3];
                }
                else if (address.Contains('/'))
                {
                    continue;
                }

                rules[address] = rules.GetValueOrDefault(address) + 1;
            }

            return rules;
        }

        // Runs one iptables command and hands back what it printed. Anything but a
        // clean exit is thrown, with what iptables said, rather than logged and
        // passed over
        private async Task<string> RunIptablesAsync(string arguments, bool mayPrompt = true)
        {
            using var process = _processService.CreateProcess("sudo");

            // -n stops sudo from asking for a password. Asked, it waits in the terminal the
            // app was started from, and whatever called it waits with it
            process.StartInfo.Arguments = (mayPrompt ? "" : "-n ") + "iptables " + arguments;

            process.Start();

            // Both outputs are read while the command runs rather than after it
            // exits. A command that fills either pipe stops and waits for it to be
            // emptied, so waiting for the exit first would leave both sides waiting
            // on each other for good
            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stdErrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            string stdOut = await stdOutTask;
            string stdErr = (await stdErrTask).Trim();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"iptables {arguments} exited with {process.ExitCode}: {stdErr}"
                    );
            }

            return stdOut;
        }

        // Deletes only the rules this app added. The previous implementation ran
        // "iptables -F", which flushes every rule in the table and would take unrelated
        // rules, including ones protecting remote access, with it
        public async Task<bool> ResetFirewallAsync(ObservableCollection<ServerModel> serverModels)
        {
            bool confirmed = await _messageBoxService.ShowMessageBoxConfirmationAsync(
                _localizationService.GetLocaleValue("MessageBoxInfoTitle"),
                _localizationService.GetLocaleValue("FirewallResetConfirmDialogue"),
                MsBox.Avalonia.Enums.Icon.Warning
                );

            if (!confirmed)
            {
                return false;
            }

            try
            {
                AppRules rules = await ReadAppRulesAsync();

                List<ServerModel> failedServers = [];

                int removed = 0;

                foreach (var serverModel in serverModels)
                {
                    try
                    {
                        removed += await RemoveServerRulesAsync(serverModel, rules);
                    }
                    catch (Exception ex)
                    {
                        failedServers.Add(serverModel);

                        await _loggerService.LogErrorAsync($"Could not unblock {serverModel.Description}", ex.Message);
                    }
                }

                // What is left with the app's comment on it belongs to no server on the list,
                // an address Valve has since taken off a server. The comment says whose it is,
                // so it goes too. The older inbound rules carry no comment, so only the ones
                // for listed servers can be told apart, and those went above
                try
                {
                    List<string> leftoverAddresses = rules.Outbound
                        .Where(rule => rule.Value > 0)
                        .Select(rule => rule.Key)
                        .ToList();

                    removed += await RemoveEveryCopyAsync(leftoverAddresses, rules.Outbound, OutboundRule);
                }
                catch (Exception ex)
                {
                    // None of these belong to a card, so nothing on screen is wrong because of it
                    await _loggerService.LogErrorAsync("Could not remove firewall rules left from servers that are gone", ex.Message);
                }

                if (failedServers.Count > 0)
                {
                    throw new FirewallOperationException(failedServers);
                }

                await _loggerService.LogInfoAsync($"Removed {removed} firewall rules created by this app");

                await _messageBoxService.ShowMessageBoxAsync(
                    _localizationService.GetLocaleValue("MessageBoxInfoTitle"),
                    string.Format(
                        _localizationService.GetLocaleValue("FirewallResetSuccessDialogue"),
                        removed
                        ),
                    MsBox.Avalonia.Enums.Icon.Success
                    );

                return true;
            }
            catch (Exception ex)
            {
                await _loggerService.LogErrorAsync(ex.Message);
                throw;
            }
        }

        // The rules are read back, so the cards show what the firewall really does. Most
        // distributions drop iptables rules on a reboot, and without this the cards went on
        // showing servers as blocked that nothing blocked any more. Rules from older
        // versions count too, they are still in the firewall doing what they did.
        // This runs while the server list loads, so it never asks for a password: a sudo
        // prompt would wait in a terminal and hold the load up. When the rules cannot be
        // read, the saved state is kept, which is how it worked before
        public async Task<List<ServerModel>?> GetBlockedServersAsync(ObservableCollection<ServerModel> serverModels)
        {
            try
            {
                AppRules rules = await ReadAppRulesAsync(mayPrompt: false);

                return serverModels
                    .Where(serverModel => GetAddresses(serverModel).Any(rules.Blocks))
                    .ToList();
            }
            catch (Exception ex)
            {
                await _loggerService.LogWarningAsync("Could not read firewall rules: " + ex.Message);

                return null;
            }
        }

        // The rules this app has in the firewall, as address and number of copies
        private sealed class AppRules(Dictionary<string, int> outbound, Dictionary<string, int> inbound)
        {
            public Dictionary<string, int> Outbound { get; } = outbound;

            public Dictionary<string, int> Inbound { get; } = inbound;

            public bool Blocks(string address)
                => Outbound.GetValueOrDefault(address) > 0 || Inbound.GetValueOrDefault(address) > 0;
        }
    }
}
