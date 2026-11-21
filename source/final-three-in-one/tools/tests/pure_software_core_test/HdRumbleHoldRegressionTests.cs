using System;
using System.Diagnostics;
using Y700Switch2V60Viiper;

/// <summary>
/// Regression tests for the WinJoyCon2-style HD rumble hold ported into Plus.
/// The tuned constants and state machine must stay byte-compatible with the
/// proven Python implementation (WinJoyCon2-Plus/_src/virtual/rumble_hd.py).
/// </summary>
internal static class HdRumbleHoldRegressionTests
{
    private static long Tick(double ms) => (long)Math.Ceiling(ms * Stopwatch.Frequency / 1000.0);

    private static byte[] Frame(bool active)
    {
        byte[] p = new byte[33];
        p[0] = 0x02;
        p[1] = p[17] = 0x50;
        // One active 10-bit amplitude, or a silent frame with neutral carrier.
        ulong bits = 391UL | ((ulong)(active ? 300 : 0) << 10) | (274UL << 20);
        for (int side = 0; side < 2; side++)
        for (int frame = 0; frame < 3; frame++)
        {
            ulong v = bits;
            int off = 2 + side * 16 + frame * 5;
            for (int i = 0; i < 5; i++) p[off + i] = (byte)(v >> (i * 8));
        }
        return p;
    }

    public static void Run(Action<bool, string> check)
    {
        // Parameters are the exact WinJoyCon2 tuned values.
        check(HdRumbleHoldState.RefreshIntervalMs == 36,
            "HD hold refresh is WinJoyCon2's 36 ms (three 12 ms subframes)");
        check(HdRumbleHoldState.HoldAfterSourceMs == 250,
            "HD hold after source is WinJoyCon2's 250 ms HOLD_S");
        check(HdRumbleHoldState.SilenceHoldMs == 100,
            "HD silence brake is WinJoyCon2's 100 ms SILENCE_HOLD_S");
        check(HdRumbleHoldState.SilenceRefreshMax == 3,
            "HD silence brake sends at most three frames, as in WinJoyCon2");

        var hold = new HdRumbleHoldState();
        long t0 = Tick(1000);

        // A live tone holds, then re-sends at 36 ms cadence with only the
        // sequence nibble changed (payload bytes are otherwise byte-exact).
        hold.Observe(Frame(true), "ns2pro-hd", true, t0);
        check(hold.Active, "live HD state arms the hold");
        check(!hold.TryCreate(t0 + Tick(1), false, 1, out _, out _),
            "no refresh before the 36 ms cadence elapses");
        check(hold.TryCreate(t0 + Tick(37), false, 2, out var refresh, out bool expired) &&
              !expired && refresh!.GeneratedKind == "hd_hold" && refresh.Native,
              "36 ms later a native keep-alive is produced");
        byte[] expected = Frame(true);
        expected[1] = expected[17] = 0x52;
        check(refresh!.Data.AsSpan(1, 32).SequenceEqual(expected.AsSpan(1, 32)),
            "keep-alive is byte-exact apart from the sequence nibble");
        hold.Written(refresh, true);

        // A fresh host state supersedes the old revision.
        hold.Observe(Frame(false), "ns2pro-hd", true, t0 + Tick(50));
        check(!hold.IsCurrent(refresh), "new host state invalidates in-flight keep-alive");

        // Silence is braked with a bounded number of re-sends. Keep the
        // cadence at 36 ms so all brakes land inside the 100 ms window.
        int brakes = 0;
        for (int i = 0; i < 10; i++)
        {
            long at = t0 + Tick(50 + i * 36);
            if (hold.TryCreate(at, false, (byte)i, out var brake, out _) && brake!.GeneratedKind == "hd_hold")
            {
                brakes++;
                hold.Written(brake, true);
            }
        }
        // The host's silence frame itself is the first brake; the hold adds
        // SilenceRefreshMax - 1 more inside the window, for 3 total.
        check(brakes == HdRumbleHoldState.SilenceRefreshMax - 1,
            "silence brake totals three frames (host frame + two re-sends), BLE not hogged");

        // A live tone that the host abandons expires into exactly one stop.
        hold.Reset();
        hold.Observe(Frame(true), "switch1-hd-converted", true, t0);
        check(hold.TryCreate(t0 + Tick(300), false, 7, out var stop, out bool toneExpired) &&
              toneExpired && stop!.GeneratedKind == "hd_hold_stop" &&
              !Pro2NativeRumbleEncoder.HasEffect(stop.Data),
              "abandoned tone expires into one neutral stop, not an endless refresh");

        // Ordinary Xbox/DS5 sources must NOT be double-held by this mechanism.
        hold.Reset();
        hold.Observe(Frame(true), "xinput", true, t0);
        check(!hold.Active, "ordinary XInput state stays on the original 12 ms hold only");

        // Invalid packets never arm the hold.
        hold.Observe(new byte[10], "ns2pro-hd", false, t0);
        check(!hold.Active, "invalid/short packet does not arm the HD hold");

        hold.Reset();
        check(!hold.Active && hold.Generated == 0 && hold.SilenceBrakes == 0,
            "reset clears all hold state and counters");
    }
}
