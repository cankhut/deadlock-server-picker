using ServerPickerX.Services.SystemFirewalls;

namespace ServerPickerX.Tests.Services.SystemFirewalls
{
    // The listings below are what "iptables -S" printed on a real machine (iptables 1.8.10,
    // nf_tables and legacy backends gave identical output), not what it is assumed to print.
    // The app reads its own rules back from this text, so if the parsing is wrong the cards
    // stop matching the firewall
    public class LinuxFirewallServiceTests
    {
        private const string OutputListing =
            "-P OUTPUT ACCEPT\n" +
            "-A OUTPUT -d 155.133.226.68/32 -m comment --comment server_picker_x -j DROP\n" +
            "-A OUTPUT -d 155.133.226.69/32 -m comment --comment server_picker_x -j DROP\n" +
            "-A OUTPUT -d 155.133.226.68/32 -m comment --comment server_picker_x -j DROP\n" +
            // Everything from here on belongs to someone else and must never be counted
            "-A OUTPUT -d 192.0.2.30/32 -m comment --comment \"server picker x lookalike\" -j DROP\n" +
            "-A OUTPUT -d 192.0.2.31/32 -p udp -m comment --comment server_picker_x -j DROP\n" +
            "-A OUTPUT -d 192.0.2.32/32 -m comment --comment server_picker_x -j REJECT --reject-with icmp-port-unreachable\n" +
            "-A OUTPUT -d 192.0.2.0/24 -m comment --comment server_picker_x -j DROP\n" +
            "-A OUTPUT -d 192.0.2.34/32 -m comment --comment \"server_picker_x\\\"quoted\" -j DROP\n" +
            "-A OUTPUT -d 192.0.2.35/32 -j DROP\n" +
            "-A OUTPUT -j ufw-before-output\n";

        private const string InputListing =
            "-P INPUT ACCEPT\n" +
            "-A INPUT -j ufw-before-input\n" +
            "-A INPUT -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT\n" +
            "-A INPUT -s 155.133.226.70/32 -j DROP\n" +
            "-A INPUT -s 155.133.226.71/32 -j DROP\n" +
            "-A INPUT -s 155.133.226.70/32 -j DROP\n" +
            "-A INPUT -s 192.0.2.52/32 -p tcp -j DROP\n";

        [Fact]
        public void ReadsItsOwnRules_CountingEveryCopy()
        {
            Dictionary<string, int> rules = LinuxFirewallService.ParseOutboundRules(OutputListing);

            Assert.Equal(2, rules["155.133.226.68"]);
            Assert.Equal(1, rules["155.133.226.69"]);
        }

        // A rule with anything more or less in it is not one this app wrote, however
        // close it looks, and a rule the app does not count is a rule it never deletes
        [Fact]
        public void NeverCountsSomeoneElsesRules()
        {
            Dictionary<string, int> rules = LinuxFirewallService.ParseOutboundRules(OutputListing);

            Assert.Equal(2, rules.Count);
            Assert.True(rules.ContainsKey("155.133.226.68"));
            Assert.True(rules.ContainsKey("155.133.226.69"));
        }

        // The app only adds rules for single addresses. A range is never its own, even
        // wearing its comment, so "Remove App Rules" must never take it off
        [Fact]
        public void AnAddressRangeIsNeverCounted()
        {
            Dictionary<string, int> rules = LinuxFirewallService.ParseOutboundRules(
                "-A OUTPUT -d 192.0.2.0/24 -m comment --comment server_picker_x -j DROP\n");

            Assert.Empty(rules);
        }

        // What 2.1.2 and earlier added. Still read, so those blocks show up and can be undone
        [Fact]
        public void ReadsTheRulesOlderVersionsAdded()
        {
            Dictionary<string, int> rules = LinuxFirewallService.ParseInboundRules(InputListing);

            Assert.Equal(2, rules["155.133.226.70"]);
            Assert.Equal(1, rules["155.133.226.71"]);
            Assert.Equal(2, rules.Count);
        }

        [Fact]
        public void TheTwoKindsAreNeverMixedUp()
        {
            Assert.Empty(LinuxFirewallService.ParseOutboundRules(InputListing));
            Assert.Empty(LinuxFirewallService.ParseInboundRules(OutputListing));
        }

        [Fact]
        public void LineEndingsAndBlankLinesDoNotMatter()
        {
            Dictionary<string, int> rules = LinuxFirewallService.ParseOutboundRules(
                "\r\n-P OUTPUT ACCEPT\r\n\r\n-A OUTPUT -d 155.133.226.68/32 -m comment --comment server_picker_x -j DROP\r\n");

            Assert.Equal(1, rules["155.133.226.68"]);
        }

        // iptables puts quotes around a comment holding anything but letters, digits,
        // "_" and "-". A quoted comment would no longer match the app's own rules, and every
        // block it made would turn invisible to it
        [Fact]
        public void TheCommentIsOneIptablesNeverQuotes()
        {
            Assert.Matches("^[A-Za-z0-9_-]+$", LinuxFirewallService.RuleComment);
        }
    }
}
