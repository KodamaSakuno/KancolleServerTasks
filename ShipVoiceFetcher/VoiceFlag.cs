namespace ShipVoiceFetcher;

[Flags]
public enum VoiceFlag
{
    None,
    Idle = 1,
    HourlyNotification = 1 << 1,
    SpecialIdle = 1 << 2,
}
