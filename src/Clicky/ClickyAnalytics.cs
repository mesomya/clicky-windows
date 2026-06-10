//
//  ClickyAnalytics.cs
//  Clicky for Windows
//
//  The original app reports usage to Farza's PostHog project. This port
//  deliberately ships analytics as a no-op — your usage stays on your
//  machine — while keeping the original call sites intact so the port
//  stays diff-able against the Swift source. Wire up your own PostHog
//  key here if you ever want telemetry.
//

namespace Clicky;

public static class ClickyAnalytics
{
    public static void Configure() { }

    public static void TrackAppOpened() { }
    public static void TrackOnboardingStarted() { }
    public static void TrackOnboardingReplayed() { }
    public static void TrackOnboardingDemoTriggered() { }
    public static void TrackAllPermissionsGranted() { }
    public static void TrackPermissionGranted(string permission) { }
    public static void TrackPushToTalkStarted() { }
    public static void TrackPushToTalkReleased() { }
    public static void TrackUserMessageSent(string transcript) { }
    public static void TrackAIResponseReceived(string response) { }
    public static void TrackElementPointed(string? elementLabel) { }
    public static void TrackResponseError(string error) { }
    public static void TrackTTSError(string error) { }
}
