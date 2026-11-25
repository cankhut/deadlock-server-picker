using System.Text.Json.Nodes;
using ServerPickerX.Services.Versions;

namespace ServerPickerX.Tests.Services.Versions
{
    // Which release the download button in the footer points at
    public class VersionServicePickUpdateTests
    {
        private static readonly Version Current = new(2, 1, 3);

        private static JsonObject Release(string tag, bool preRelease = false, bool draft = false) =>
            new()
            {
                ["tag_name"] = tag,
                ["prerelease"] = preRelease,
                ["draft"] = draft,
            };

        private static AvailableRelease? Pick(params JsonNode?[] releases) =>
            VersionService.PickUpdate(new JsonArray(releases), Current);

        [Fact]
        public void NothingNewer_NoButton()
        {
            Assert.Null(Pick(Release("v2.1.3"), Release("v2.1.2"), Release("v2.1.1")));
        }

        [Fact]
        public void ANewerRelease_IsShownAsARelease()
        {
            AvailableRelease? update = Pick(Release("v2.1.4"), Release("v2.1.3"));

            Assert.NotNull(update);
            Assert.Equal(new Version(2, 1, 4), update.Version);
            Assert.False(update.IsPreRelease);
        }

        // Most people should take the normal release, even when a newer pre-release is out
        [Fact]
        public void AReleaseWins_OverANewerPreRelease()
        {
            AvailableRelease? update = Pick(Release("v2.2.0", preRelease: true), Release("v2.1.4"));

            Assert.NotNull(update);
            Assert.Equal(new Version(2, 1, 4), update.Version);
            Assert.False(update.IsPreRelease);
        }

        [Fact]
        public void APreRelease_IsShown_WhenItIsTheOnlyNewerOne()
        {
            AvailableRelease? update = Pick(Release("v2.1.4", preRelease: true), Release("v2.1.3"));

            Assert.NotNull(update);
            Assert.Equal(new Version(2, 1, 4), update.Version);
            Assert.True(update.IsPreRelease);
        }

        // GitHub lists the newest first, but nothing here relies on that
        [Fact]
        public void TheNewestOfThemIsPicked_WhateverTheOrder()
        {
            AvailableRelease? update = Pick(Release("v2.1.5"), Release("v2.1.7"), Release("v2.1.6"));

            Assert.NotNull(update);
            Assert.Equal(new Version(2, 1, 7), update.Version);
        }

        [Fact]
        public void DraftsAndBrokenEntries_ArePassedOver()
        {
            AvailableRelease? update = Pick(
                Release("v9.0.0", draft: true),
                Release("not a version"),
                new JsonObject { ["name"] = "no tag at all" },
                new JsonObject { ["tag_name"] = 42 },
                JsonValue.Create(7),
                null,
                Release("v2.1.4"));

            Assert.NotNull(update);
            Assert.Equal(new Version(2, 1, 4), update.Version);
        }

        // Tags like "v2.1.4-beta.1" are read by their numbers, as the startup check reads them
        [Fact]
        public void ATagSuffix_IsReadByItsNumbers()
        {
            AvailableRelease? update = Pick(Release("v2.1.4-beta.1", preRelease: true));

            Assert.NotNull(update);
            Assert.Equal(new Version(2, 1, 4), update.Version);
            Assert.True(update.IsPreRelease);
        }
    }
}
