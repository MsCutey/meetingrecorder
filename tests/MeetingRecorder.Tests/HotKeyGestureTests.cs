using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.Tests;

public sealed class HotKeyGestureTests
{
    [Fact]
    public void DefaultHotKey_IsParsed()
    {
        var gesture = HotKeyGesture.Parse("Ctrl+Shift+F9");
        Assert.True(gesture.Modifiers.HasFlag(HotKeyModifiers.Control));
        Assert.True(gesture.Modifiers.HasFlag(HotKeyModifiers.Shift));
        Assert.Equal(0x78u, gesture.VirtualKey);
    }

    [Fact]
    public void ConflictingSettings_AreRejected()
    {
        var settings = new AppSettings
        {
            StartStopHotKey = "Ctrl+Shift+F9",
            PauseResumeHotKey = "Ctrl+Shift+F9"
        };
        Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("F9")]
    [InlineData("Ctrl+Potato")]
    public void InvalidHotKey_IsRejected(string value) =>
        Assert.Throws<FormatException>(() => HotKeyGesture.Parse(value));
}
