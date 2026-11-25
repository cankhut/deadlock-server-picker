using ServerPickerX.Helpers;

namespace ServerPickerX.Tests.Helpers
{
    public class ServerListChangesTests
    {
        private static readonly Dictionary<string, string> Before = new()
        {
            ["fra"] = "Frankfurt (Germany)",
            ["gum"] = "Guam",
            ["sto"] = "Stockholm - Kista (Sweden)",
        };

        private static string? Describe(Dictionary<string, string>? previous, Dictionary<string, string> current) =>
            ServerListChanges.Describe(previous, current, "Added:", "Removed:", "Nothing changed.", "\u2026and {0} more");

        // The first time there is nothing saved to compare with. The message must then read
        // exactly as it always has, not claim that nothing changed
        [Fact]
        public void SaysNothing_WhenThereIsNoEarlierList()
        {
            Assert.Null(Describe(null, Before));
            Assert.Null(Describe(new Dictionary<string, string>(), Before));
        }

        [Fact]
        public void ListsWhatCameAndWhatWent()
        {
            Dictionary<string, string> after = new(Before)
            {
                ["hel"] = "Helsinki (Finland)",
            };
            after.Remove("gum");

            Assert.Equal(
                "Added:\n\u2022 Helsinki (Finland)\n\nRemoved:\n\u2022 Guam",
                Describe(Before, after));
        }

        // Servers are told apart by Valve's code, so a new name on an old server is not news
        [Fact]
        public void ARenamedServer_IsNeitherAddedNorRemoved()
        {
            Dictionary<string, string> after = new(Before)
            {
                ["fra"] = "Frankfurt am Main (Germany)",
            };

            Assert.Equal("Nothing changed.", Describe(Before, after));
        }

        [Fact]
        public void OnlyTheSectionsWithSomethingInThemAreShown()
        {
            Dictionary<string, string> after = new(Before);
            after.Remove("sto");

            Assert.Equal("Removed:\n\u2022 Stockholm - Kista (Sweden)", Describe(Before, after));
        }

        // A big update must not stretch the message past the bottom of the screen
        [Fact]
        public void ALongListIsCutShort()
        {
            Dictionary<string, string> after = new(Before);

            for (int i = 1; i <= ServerListChanges.MaxNamesPerList + 2; i++)
            {
                after[$"new{i:00}"] = $"New server {i:00}";
            }

            string[] lines = Describe(Before, after)!.Split('\n');

            Assert.Equal("Added:", lines[0]);
            Assert.Equal(ServerListChanges.MaxNamesPerList + 2, lines.Length);
            Assert.Equal("\u2022 New server 01", lines[1]);
            Assert.Equal("\u2026and 2 more", lines[^1]);
        }
    }
}
