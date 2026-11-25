using Moq;
using ServerPickerX.Services.Loggers;
using ServerPickerX.Services.MessageBoxes;
using ServerPickerX.Settings;

namespace ServerPickerX.Tests.Services.Settings
{
    public class JsonSettingServerListTests
    {
        private static JsonSetting NewSetting(string gameMode) =>
            new(new Mock<IMessageBoxService>().Object, new Mock<ILoggerService>().Object)
            {
                game_mode = gameMode,
            };

        // The list is checked on every launch, and is the same almost every time. Only a real
        // change may cost a write of the settings file
        [Fact]
        public void RemembersAList_AndSaysWhenItActuallyChanged()
        {
            JsonSetting setting = NewSetting("Deadlock");

            Dictionary<string, string> servers = new() { ["fra"] = "Frankfurt (Germany)" };

            Assert.True(setting.RememberServerListByGameMode(servers));
            Assert.False(setting.RememberServerListByGameMode(new Dictionary<string, string>(servers)));

            servers["hel"] = "Helsinki (Finland)";

            Assert.True(setting.RememberServerListByGameMode(servers));
            Assert.Equal(2, setting.GetLastServerListByGameMode().Count);
        }

        [Fact]
        public void ARenameCountsAsAChange_SoTheSavedNameStaysCurrent()
        {
            JsonSetting setting = NewSetting("Deadlock");

            setting.RememberServerListByGameMode(new() { ["fra"] = "Frankfurt (Germany)" });

            Assert.True(setting.RememberServerListByGameMode(new() { ["fra"] = "Frankfurt am Main (Germany)" }));
            Assert.Equal("Frankfurt am Main (Germany)", setting.GetLastServerListByGameMode()["fra"]);
        }

        [Fact]
        public void EachGameKeepsItsOwnList()
        {
            JsonSetting setting = NewSetting("Deadlock");

            setting.RememberServerListByGameMode(new() { ["fra"] = "Frankfurt (Germany)" });

            setting.game_mode = "Counter Strike 2";

            Assert.Empty(setting.GetLastServerListByGameMode());
        }

        // What the caller gets is a copy. Changing it must not change what was saved
        [Fact]
        public void TheListHandedOutIsACopy()
        {
            JsonSetting setting = NewSetting("Deadlock");

            setting.RememberServerListByGameMode(new() { ["fra"] = "Frankfurt (Germany)" });

            setting.GetLastServerListByGameMode().Clear();

            Assert.Single(setting.GetLastServerListByGameMode());
        }
    }
}
