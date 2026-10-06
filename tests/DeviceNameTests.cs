using Xunit;

namespace HeadsetAutoSwitch.Tests;

public class DeviceNameTests
{
    [Theory]
    [InlineData("Speakers (Realtek(R) Audio)", "Speakers (Realtek*", true)]
    [InlineData("Speakers (Realtek(R) Audio)", "speakers (realtek(r) audio)", true)]
    [InlineData("Headphones (HyperX Cloud Alpha 2 Wireless Game)", "*Cloud Alpha 2 Wireless Game*", true)]
    [InlineData("NGENUITY - 8 Channel Spatial (HyperX Virtual Audio Device)", "NGENUITY - 8 Channel Spatial*", true)]
    [InlineData("NGENUITY - 12 Channel Spatial (HyperX Virtual Audio Device)", "NGENUITY - 8 Channel Spatial*", false)]
    [InlineData("Headset Earphone (HyperX Cloud Alpha 2 Wireless Chat)", "*Cloud Alpha 2 Wireless Game*", false)]
    [InlineData("Speakers (Realtek(R) Audio)", "Speakers", false)]
    public void MatchesWildcardPatterns(string name, string pattern, bool expected) =>
        Assert.Equal(expected, DeviceName.Matches(name, pattern));

    [Theory]
    [InlineData("Speakers (Realtek(R) Audio)", "")]
    [InlineData("Speakers (Realtek(R) Audio)", null)]
    [InlineData(null, "*")]
    public void EmptyPatternOrNameMatchesNothing(string? name, string? pattern) =>
        Assert.False(DeviceName.Matches(name, pattern));

    [Fact]
    public void RegexCharactersAreLiteral() =>
        Assert.False(DeviceName.Matches("Speakers (Realtek(R) Audio)", "Speakers .Realtek.R. Audio."));
}
