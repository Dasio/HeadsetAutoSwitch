using System;
using System.IO;
using Xunit;

namespace HeadsetAutoSwitch.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"headsetautoswitch-{Guid.NewGuid():N}.ini");

    public void Dispose() => File.Delete(path);

    private Config Load(string contents, out System.Collections.Generic.List<string> warnings)
    {
        File.WriteAllText(path, contents);
        return Config.Load(path, out warnings);
    }

    [Fact]
    public void MissingFileIsCreatedWithDefaults()
    {
        var config = Config.Load(path, out var warnings);

        Assert.True(File.Exists(path));
        Assert.Empty(warnings);
        Assert.Equal(new Config(), config);
        Assert.Equal(Config.DefaultPreferredOutput, config.HeadsetOutputPreferred);
    }

    [Fact]
    public void TheCreatedFileReadsBackAsDefaults()
    {
        Config.Load(path, out _);

        Assert.Equal(new Config(), Config.Load(path, out var warnings));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("off", false)]
    [InlineData("false", false)]
    [InlineData("No", false)]
    [InlineData("0", false)]
    [InlineData("on", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    public void OnOffSettingsAcceptCommonSpellings(string value, bool expected)
    {
        var config = Load($"Alpha2 = {value}\nNotifications = {value}\n", out var warnings);

        Assert.Equal(expected, config.Alpha2);
        Assert.Equal(expected, config.Notifications);
        Assert.Empty(warnings);
    }

    [Fact]
    public void UnknownOnOffValueWarnsAndKeepsDefault()
    {
        var config = Load("Alpha2 = maybe\n", out var warnings);

        Assert.True(config.Alpha2);
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3601")]
    [InlineData("999999999")]
    [InlineData("soon")]
    public void OutOfRangeIntervalWarnsAndKeepsDefault(string value)
    {
        var config = Load($"HeadsetControlInterval = {value}\n", out var warnings);

        Assert.Equal(5, config.HeadsetControlInterval);
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("false")]
    public void HeadsetControlCanBeTurnedOff(string value) =>
        Assert.Null(Load($"HeadsetControl = {value}\n", out _).HeadsetControl);

    [Theory]
    [InlineData("auto")]
    [InlineData("on")]
    [InlineData("yes")]
    [InlineData("")]
    public void HeadsetControlCanBeTurnedOn(string value) =>
        Assert.Equal("auto", Load($"HeadsetControl = {value}\n", out _).HeadsetControl);

    [Fact]
    public void HeadsetControlPathThatDoesNotExistWarnsAndTurnsItOff()
    {
        var config = Load(@"HeadsetControl = C:\nowhere\headsetcontrol.exe" + "\n", out var warnings);

        Assert.Null(config.HeadsetControl);
        Assert.Single(warnings);
    }

    [Fact]
    public void ValuesAreTrimmedAndCommentsIgnored()
    {
        var config = Load("# SpeakersOutput = commented\n  SpeakersOutput =  Speakers (Realtek*  \n", out _);

        Assert.Equal("Speakers (Realtek*", config.SpeakersOutput);
    }
}
