using Moq;
using ServerPickerX.Services.Loggers;
using ServerPickerX.Services.MessageBoxes;
using ServerPickerX.Settings;

namespace ServerPickerX.Tests.Services.Settings
{
    public class JsonSettingNoticeTests
    {
        private static JsonSetting NewSetting() =>
            new(new Mock<IMessageBoxService>().Object, new Mock<ILoggerService>().Object);

        [Fact]
        public void NothingIsHidden_OnAFreshInstall()
        {
            Assert.False(NewSetting().HasHiddenNotices());
        }

        [Fact]
        public void AnyOneHiddenNotice_CountsAsHidden()
        {
            JsonSetting lateNight = NewSetting();
            lateNight.late_night_notice_hidden = true;

            JsonSetting gameSwitch = NewSetting();
            gameSwitch.game_switch_notice_hidden = true;

            JsonSetting clusterSwitch = NewSetting();
            clusterSwitch.cluster_switch_notice_hidden = true;

            Assert.True(lateNight.HasHiddenNotices());
            Assert.True(gameSwitch.HasHiddenNotices());
            Assert.True(clusterSwitch.HasHiddenNotices());
        }

        // The late night notice keeps its once a day limit, so the day it was last shown
        // stays as it was and the notice does not come straight back the same night
        [Fact]
        public async Task ShowingThemAgain_BringsBackEveryOne_AndKeepsTheDailyLimit()
        {
            JsonSetting setting = NewSetting();
            setting.late_night_notice_hidden = true;
            setting.late_night_notice_last_shown = "2026-11-20";
            setting.game_switch_notice_hidden = true;
            setting.cluster_switch_notice_hidden = true;

            await setting.ShowHiddenNoticesAgainAsync();

            Assert.False(setting.late_night_notice_hidden);
            Assert.False(setting.game_switch_notice_hidden);
            Assert.False(setting.cluster_switch_notice_hidden);
            Assert.False(setting.HasHiddenNotices());
            Assert.Equal("2026-11-20", setting.late_night_notice_last_shown);
        }
    }
}
