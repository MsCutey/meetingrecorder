namespace MeetingRecorder.Core.Recording;

public sealed record WorkerTimeoutOptions(
    TimeSpan Discovery,
    TimeSpan Startup,
    TimeSpan Command,
    TimeSpan Finalization)
{
    public static WorkerTimeoutOptions Default { get; } = new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(2));

    public void Validate()
    {
        if (Discovery <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Discovery));
        if (Startup <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Startup));
        if (Command <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Command));
        if (Finalization <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Finalization));
    }
}
