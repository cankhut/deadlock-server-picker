using System;

namespace ServerPickerX.Services.Versions
{
    // A release newer than the running build, and whether GitHub marks it as a pre-release
    public sealed record AvailableRelease(Version Version, bool IsPreRelease);
}
