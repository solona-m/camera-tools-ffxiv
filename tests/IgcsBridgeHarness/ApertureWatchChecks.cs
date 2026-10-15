// Checks the aperture warning's decisions and wording without the game.
//
// ApertureWatch and ParallaxDofGuidance are linked in from the plugin's source rather
// than copied, so these run against the code that ships. They are kept free of Dalamud
// for exactly this reason: the session that owns the watch cannot be built off the game,
// so its wiring (Start -> Reset, MoveMultishot -> Observe, End/Abort -> SessionEnded) is
// left to code review and the in-game checks. What is here is the logic those calls feed.

using System.Globalization;
using CameraToolsXIV.Igcs;

internal static class ApertureWatchChecks
{
    private const long T0 = 1_000_000;

    public static void Run(Action<string, bool, string?> check)
    {
        // --- Flagging ----------------------------------------------------------------

        var watch = new ApertureWatch();
        watch.Observe(1f, 0f, T0);
        var status = watch.Status(T0);
        check("[AC-blur-radius-warning-1] default radius step is flagged",
            status.Warn && status.Step == 1f, $"got {status}");

        // A preview that sends its step once must not lose its warning just because
        // nothing has arrived since.
        status = watch.Status(T0 + 60_000);
        check("[AC-blur-radius-warning-1] a still-too-large latest step stays flagged with no new steps",
            status.Warn, $"got {status}");

        watch = new ApertureWatch();
        watch.Observe(1f, 0f, T0);
        watch.Observe(0.5f, 0f, T0 + 10);
        status = watch.Status(T0 + 10);
        check("[AC-blur-radius-warning-1] the step reported is the latest too-large one",
            status.Warn && status.Step == 0.5f, $"got {status}");

        check("[AC-blur-radius-warning-1] running heading names Blur Radius",
            ParallaxDofGuidance.WarningHeading(false) == "Blur Radius is far too large for FFXIV",
            ParallaxDofGuidance.WarningHeading(false));

        var body = ParallaxDofGuidance.WarningBody(1f);
        check("[AC-blur-radius-warning-1] warning body names the step and 0.003",
            ContainsAll(body, "1.00", "world units", "Blur Radius", "0.003", "Recommended Parallax DoF settings"),
            body);

        // --- Announcing --------------------------------------------------------------

        watch = new ApertureWatch();
        watch.Observe(1f, 0f, T0);
        var first = watch.TakeAnnouncement();
        watch.Observe(0.5f, 0f, T0 + 10);
        var second = watch.TakeAnnouncement();
        check("[AC-blur-radius-warning-2] announcement is taken once per session",
            first == 1f && second is null, $"first {first}, second {second}");

        var chat = ParallaxDofGuidance.ChatMessage(1f);
        check("[AC-blur-radius-warning-2] chat message names Blur Radius, 0.003, /camtools and the step",
            ContainsAll(chat, "Blur Radius", "0.003", "/camtools", "1.00"), chat);

        // --- Not flagging ------------------------------------------------------------

        watch = new ApertureWatch();
        watch.Observe(0.003f, 0f, T0);
        watch.Observe(0.05f, 0f, T0 + 10);
        watch.Observe(0.1f, 0f, T0 + 20);
        status = watch.Status(T0 + 20);
        check("[AC-blur-radius-warning-3] recommended and sub-cut-off steps are never flagged",
            !status.Warn && watch.TakeAnnouncement() is null, $"got {status}");

        // --- Clearing mid-session ----------------------------------------------------

        watch = new ApertureWatch();
        watch.Observe(1f, 0f, T0);
        watch.Observe(0.003f, 0f, T0 + 500);
        var before = watch.Status(T0 + 1999).Warn;
        var after = watch.Status(T0 + 2000).Warn;
        check("[AC-blur-radius-warning-4] [EDGE-blur-radius-warning-10] warning clears 2 s after the last large step mid-session",
            before && !after, $"1999 ms {before}, 2000 ms {after}");

        // --- After the session -------------------------------------------------------

        watch = new ApertureWatch();
        watch.Observe(1f, 0f, T0);
        watch.SessionEnded(T0 + 100);
        status = watch.Status(T0 + 600_000);
        check("[AC-blur-radius-warning-5] warning persists after the session ends",
            status.Warn && status.FromEndedSession, $"got {status}");
        check("[AC-blur-radius-warning-5] ended heading says last stack",
            ParallaxDofGuidance.WarningHeading(true).Contains("Last stack"),
            ParallaxDofGuidance.WarningHeading(true));

        // The add-on sends its own End after "Release camera" has already ended the
        // session, and by then the 2 s window has long passed.
        watch.SessionEnded(T0 + 5100);
        status = watch.Status(T0 + 5100);
        check("[EDGE-blur-radius-warning-11] a second end does not clear the last stack's warning",
            status.Warn && status.FromEndedSession, $"got {status}");

        watch = new ApertureWatch();
        watch.Observe(1f, 0f, T0);
        watch.Observe(0.003f, 0f, T0 + 10);
        watch.SessionEnded(T0 + 5000);
        status = watch.Status(T0 + 5000);
        check("[AC-blur-radius-warning-5] a session ended after the fix shows no warning",
            !status.Warn, $"got {status}");

        watch = new ApertureWatch();
        watch.Observe(1f, 0f, T0);
        watch.SessionEnded(T0 + 100);
        watch.Reset();
        status = watch.Status(T0 + 200);
        check("[AC-blur-radius-warning-5] next session clears it",
            !status.Warn && !status.FromEndedSession && status.Step == 0f && watch.TakeAnnouncement() is null,
            $"got {status}");

        // --- Recommended settings ----------------------------------------------------

        (string, string)[] expected =
        [
            ("Accumulation Init / Delay", "4 frames each"),
            ("Rangefinder Focus", "~15.1 (per shot)"),
            ("Blur Radius", "0.003"),
            ("Bokeh Intensity / Gamma / Colour", "0.550 / 0.580 / 0.370"),
            ("Aperture", "Circular, aspect 1.000"),
            ("Sample Count", "1024 (about four minutes)"),
        ];
        check("[AC-blur-radius-warning-6] recommended settings are the six documented rows",
            ParallaxDofGuidance.Recommended.SequenceEqual(expected)
                && ParallaxDofGuidance.RecommendedBlurRadius == "0.003",
            string.Join("; ", ParallaxDofGuidance.Recommended));

        // --- Edge cases --------------------------------------------------------------

        watch = new ApertureWatch();
        watch.Observe(0.1f, 0f, T0);
        var atCutOff = watch.Status(T0).Warn;
        watch.Observe(0.1001f, 0f, T0 + 10);
        var overCutOff = watch.Status(T0 + 10).Warn;
        check("[EDGE-blur-radius-warning-1] exactly 0.1 is not flagged, 0.1001 is",
            !atCutOff && overCutOff, $"0.1 {atCutOff}, 0.1001 {overCutOff}");

        watch = new ApertureWatch();
        watch.Observe(0.08f, 0.08f, T0);
        check("[EDGE-blur-radius-warning-2] diagonal (0.08, 0.08) is flagged", watch.Status(T0).Warn, null);

        watch = new ApertureWatch();
        watch.Observe(-1f, 0f, T0);
        check("[EDGE-blur-radius-warning-3] negative (-1, 0) is flagged", watch.Status(T0).Warn, null);

        watch = new ApertureWatch();
        watch.Observe(float.NaN, 0f, T0);
        watch.Observe(float.PositiveInfinity, 0f, T0 + 10);
        watch.Observe(0f, float.NegativeInfinity, T0 + 20);
        status = watch.Status(T0 + 20);
        check("[EDGE-blur-radius-warning-4] NaN and infinite steps are ignored",
            !status.Warn && status.Step == 0f && watch.TakeAnnouncement() is null, $"got {status}");

        watch = new ApertureWatch();
        watch.Observe(0f, 0f, T0);
        check("[EDGE-blur-radius-warning-5] zero step is not flagged", !watch.Status(T0).Warn, null);

        watch = new ApertureWatch();
        watch.Observe(1f, 0f, T0);
        watch.SessionEnded(T0 + 1);
        var survived = watch.TakeAnnouncement();
        check("[EDGE-blur-radius-warning-8] announcement survives session end", survived == 1f, $"got {survived}");

        watch = new ApertureWatch();
        watch.Observe(1f, 0f, T0);
        watch.Reset();
        var dropped = watch.TakeAnnouncement();
        watch.Observe(1f, 0f, T0 + 10);
        var again = watch.TakeAnnouncement();
        check("[EDGE-blur-radius-warning-9] Reset drops an untaken announcement; next too-large session announces again",
            dropped is null && again == 1f, $"dropped {dropped}, again {again}");

        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var germanChat = ParallaxDofGuidance.ChatMessage(1f);
            var germanBody = ParallaxDofGuidance.WarningBody(1f);
            check("[EDGE-blur-radius-warning-13] messages use invariant formatting",
                ContainsAll(germanChat, "1.00", "0.003") && ContainsAll(germanBody, "1.00", "0.003")
                    && !germanChat.Contains("1,00") && !germanBody.Contains("1,00"),
                germanChat);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        check("[EDGE-blur-radius-warning-14] body and chat carry the IgcsConnector caveat",
            body.Contains("IgcsConnector") && chat.Contains("IgcsConnector"), null);

        watch = new ApertureWatch();
        watch.Observe(1f, 0f, T0);
        watch.TakeAnnouncement();
        var reissued = false;
        for (var i = 0; i < 5; i++)
        {
            watch.Observe(1f, 0f, T0 + 10 + i);
            reissued |= watch.TakeAnnouncement() is not null;
        }

        check("[EDGE-blur-radius-warning-15] a taken announcement is not re-issued", !reissued, null);

        watch = new ApertureWatch();
        for (var i = 0; i < 20; i++)
        {
            watch.Observe(0.09f, 0f, T0 + i);
        }

        check("[EDGE-blur-radius-warning-16] steps are judged individually", !watch.Status(T0 + 20).Warn, null);
    }

    private static bool ContainsAll(string text, params string[] parts)
        => parts.All(text.Contains);
}
