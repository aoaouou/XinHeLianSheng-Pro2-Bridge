using System;
using System.Diagnostics;
using System.Globalization;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Keeps a freshly written Pro2 rumble state alive on the physical actuator,
/// ported from WinJoyCon2's proven `rumble_hd.py` hold mechanism.
///
/// Why this exists: an LRA does not stop the instant the driver drops. A host
/// (game or emulator) may also write rumble reports sparsely or stop writing
/// without sending an explicit zero block. Forwarding each report exactly once
/// therefore lets the actuator decay on its own, which is heard as a thin
/// "tap" instead of a sustained HD tone. Re-writing the last state on a fixed
/// cadence holds the motor where the host asked it to be.
///
/// Parameters are taken verbatim from WinJoyCon2 (multi-round tuned against
/// real hardware):
///   RefreshInterval = 36 ms  (one packet spans three 12 ms subframes, so the
///                            effective resolution stays 12 ms while BLE
///                            writes drop to 1/3)
///   HoldAfterSource = 250 ms (absorbs sparse or jittery host reports)
///   SilenceHold     = 100 ms (brake the LRA, otherwise residual motion blurs
///                            the pause between pulses)
///   SilenceRefreshMax = 3    (a braked LRA needs no continuous refreshing)
///
/// Unlike the ordinary Xbox/DS5 hold, this covers native and converted HD
/// packets; those are byte-exact states, so only the sequence nibble changes.
/// </summary>
public sealed class HdRumbleHoldState
{
    // Per-subframe timing from switch2_controller_research / SDL_hidapi_switch2.c:
    // one 5-byte subframe drives the LRA for ~12 ms, and SDL resends every 12 ms.
    // A packet that fills all three subframes therefore spans 36 ms; a native
    // Pro2 packet (real hardware capture: only 1 subframe non-zero) spans 12 ms.
    // Refresh cadence must match the active subframe count or the motor coasts
    // between refreshes and the rumble feels weak/hollow.
    public const double SubframeMs = 12;
    public const double RefreshIntervalMs = 36;   // 3 subframes (legacy default / Pro1 converted)
    public const double NativeRefreshIntervalMs = 12; // 1 active subframe (native Pro2)
    public const double HoldAfterSourceMs = 250;
    public const double SilenceHoldMs = 100;
    public const int SilenceRefreshMax = 3;

    private byte[]? active;          // state to keep re-sending (silence included)
    private string source = "";
    private long revision, dueTicks, holdUntilTicks;
    private bool activeIsSilence;
    private int silenceRefreshLeft;
    private double currentRefreshMs = RefreshIntervalMs;

    public bool Active => active != null;
    public long Generated { get; private set; }
    public long Cancelled { get; private set; }
    public long SilenceBrakes { get; private set; }
    public long Expiries { get; private set; }

    public string Telemetry =>
        "hd_hold=" + Active +
        " hd_hold_refresh_ms=" + RefreshIntervalMs.ToString("F0", CultureInfo.InvariantCulture) +
        " hd_hold_active_refresh_ms=" + currentRefreshMs.ToString("F0", CultureInfo.InvariantCulture) +
        " hd_hold_after_source_ms=" + HoldAfterSourceMs.ToString("F0", CultureInfo.InvariantCulture) +
        " hd_hold_silence_ms=" + SilenceHoldMs.ToString("F0", CultureInfo.InvariantCulture) +
        " hd_hold_silence_max=" + SilenceRefreshMax +
        " hd_hold_generated=" + Generated.ToString(CultureInfo.InvariantCulture) +
        " hd_hold_cancelled=" + Cancelled.ToString(CultureInfo.InvariantCulture) +
        " hd_hold_brakes=" + SilenceBrakes.ToString(CultureInfo.InvariantCulture) +
        " hd_hold_expiries=" + Expiries.ToString(CultureInfo.InvariantCulture) +
        " hd_hold_scope=native_and_converted_hd segment_aware=true";

    private static long Ticks(double ms) => (long)Math.Ceiling(ms * Stopwatch.Frequency / 1000.0);

    /// <summary>
    /// Records the state the host just asked for. A silent packet starts the
    /// short brake window; anything else becomes the sustained state.
    /// Called only under the owning writer's lock.
    /// </summary>
    public void Observe(ReadOnlySpan<byte> packet, string packetSource, bool valid, long now)
    {
        revision++;
        active = null; dueTicks = 0; holdUntilTicks = 0;
        activeIsSilence = false; silenceRefreshLeft = 0; source = "";
        // Xbox/DS5 ordinary motor state is a start/change/stop signal handled by
        // OrdinaryRumbleHoldState. Never double-hold it here.
        if (OrdinaryRumbleHoldState.IsOrdinarySource(packetSource, native: false)) return;
        if (!valid || packet.Length < 33) return;

        bool silent = !Pro2NativeRumbleEncoder.HasEffect(packet);
        active = packet[..33].ToArray();
        source = packetSource;
        activeIsSilence = silent;
        // Match refresh cadence to how many subframes actually carry amplitude.
        // Native Pro2 report 0x02 fills one subframe per 16-byte block (verified
        // on real hardware: 795/795 packets), so it spans only 12 ms and must be
        // refreshed every 12 ms to hold the LRA continuously (SDL's native pace).
        // Converted Pro1 packets fill three subframes, spanning 36 ms.
        currentRefreshMs = silent || ActiveSubframes(packet) >= 2
            ? RefreshIntervalMs
            : NativeRefreshIntervalMs;
        // The host frame itself is already being written. The next keep-alive
        // is due one full cadence later, exactly as WinJoyCon2's worker only
        // re-sends _active once now - _last_time >= _refresh_interval().
        dueTicks = now + Ticks(currentRefreshMs);
        if (silent)
        {
            holdUntilTicks = now + Ticks(SilenceHoldMs);
            silenceRefreshLeft = SilenceRefreshMax;
        }
        else
        {
            holdUntilTicks = now + Ticks(HoldAfterSourceMs);
            silenceRefreshLeft = 0;
        }
    }

    /// <summary>
    /// Count 5-byte subframes that carry non-zero amplitude across both motor
    /// blocks. Native Pro2 report 0x02 fills one subframe per block (real-hardware
    /// capture: 795/795), spanning 12 ms; converted Pro1 fills all three, spanning
    /// 36 ms. The hold cadence is scaled by this so every subframe is refreshed at
    /// SDL's native 12 ms rate instead of coasting between widely spaced writes.
    /// </summary>
    private static int ActiveSubframes(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 33) return 3;
        const ulong amplitudeMask = (0x3ffUL << 10) | (0x3ffUL << 30);
        // Count subframes WITHIN ONE motor block, then take the max of left and
        // right. Both blocks always mirror each other on Pro2, so counting both
        // would double-report a 1-subframe native packet as 2 and pick the wrong
        // (36 ms) cadence.
        int max = 0;
        for (int side = 1; side <= 17; side += 16)
        {
            if ((packet[side] & 0xf0) != 0x50) return 3; // opaque: worst case
            int count = 0;
            for (int frame = 0; frame < 3; frame++)
            {
                ulong bits = 0;
                int off = side + 1 + frame * 5;
                for (int i = 0; i < 5; i++) bits |= (ulong)packet[off + i] << (8 * i);
                if ((bits & amplitudeMask) != 0) count++;
            }
            max = Math.Max(max, count);
        }
        return max;
    }

    /// <summary>
    /// Milliseconds until the next keep-alive is due. Infinite when nothing is
    /// held, so the writer waits on its source signal instead of spinning.
    /// </summary>
    public double MillisecondsUntilDue(long now)
    {
        if (active == null) return double.PositiveInfinity;
        if (dueTicks == 0) return 0;
        return Math.Max(0, (dueTicks - now) * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>
    /// Whether the hold should keep re-sending right now, and how long until
    /// the next attempt. Returns false (infinite) when nothing is scheduled so
    /// the writer can sleep on its source signal instead of spinning.
    /// </summary>
    public bool TrySchedule(long now, bool sourcePending, out double delayMs)
    {
        delayMs = double.PositiveInfinity;
        if (active == null || sourcePending) return false;
        if (now >= holdUntilTicks)
        {
            // Silence window simply ends. A live tone expires into one explicit
            // stop so the actuator is never left driven by a stale command.
            return false;
        }
        delayMs = Math.Max(0, (dueTicks - now) * 1000.0 / Stopwatch.Frequency);
        return true;
    }

    /// <summary>
    /// Produces the next keep-alive packet when it is due, or null when there is
    /// nothing to send. Expiry is reported through <paramref name="expired"/>.
    /// </summary>
    public bool TryCreate(long now, bool sourcePending, byte sequence,
        out Pro2QueuedRumble? item, out bool expired)
    {
        item = null;
        expired = false;
        if (active == null || sourcePending) return false;

        if (now >= holdUntilTicks)
        {
            if (activeIsSilence)
            {
                // Brake window over: stop without another packet.
                CancelExpired();
                return false;
            }
            // Tone expired while the host said nothing: send one neutral stop.
            // Capture the revision BEFORE CancelExpired() bumps it: the writer
            // gates this packet on IsCurrent(), which compares StateRevision.
            long stopRevision = revision;
            byte[] stop = Pro2NativeRumbleEncoder.Neutralize(active);
            stop[1] = stop[17] = (byte)(0x50 | (sequence & 15));
            item = new(stop, true, now, Source: source, GeneratedKind: "hd_hold_stop",
                StateRevision: stopRevision);
            CancelExpired();
            expired = true;
            Expiries++;
            return true;
        }

        if (dueTicks != 0 && now < dueTicks) return false;
        if (activeIsSilence && silenceRefreshLeft <= 0)
        {
            active = null; dueTicks = 0;
            return false;
        }

        byte[] copy = active.ToArray();
        copy[1] = copy[17] = (byte)(0x50 | (sequence & 15));
        // The writer drops any hd_hold packet whose StateRevision != revision.
        // Without this the whole HD hold cadence is generated then discarded,
        // so the actuator only ever sees the host's single short source packet
        // (Pro2 native = one 12 ms subframe -> no perceptible rumble at all).
        item = new(copy, true, now, Source: source, GeneratedKind: "hd_hold",
            StateRevision: revision);
        if (activeIsSilence) { silenceRefreshLeft--; SilenceBrakes++; }
        dueTicks = now + Ticks(currentRefreshMs);
        Generated++;
        return true;
    }

    /// <summary>Re-arm only after the GATT call really completed.</summary>
    public void Written(Pro2QueuedRumble item, bool success)
    {
        if (item.GeneratedKind is not "hd_hold" and not "hd_hold_stop") { return; }
        if (!success)
        {
            // A failed keep-alive must not strand the hold armed forever.
            Cancelled++;
            dueTicks = 0;
            return;
        }
    }

    public bool IsCurrent(Pro2QueuedRumble item) => active != null &&
        item.StateRevision == revision && item.Source == source;

    public void RefreshSuperseded() => Cancelled++;

    public void Invalidate()
    {
        revision++;
        active = null; source = ""; dueTicks = 0; holdUntilTicks = 0;
        activeIsSilence = false; silenceRefreshLeft = 0;
    }

    public void Reset()
    {
        Invalidate();
        Generated = Cancelled = SilenceBrakes = Expiries = 0;
    }

    /// <summary>
    /// The hold became stale (silence window elapsed / tone expired). Bump the
    /// revision so an in-flight keep-alive from the previous state cannot be
    /// mistaken for current.
    /// </summary>
    private void CancelExpired()
    {
        revision++;
        active = null; source = ""; dueTicks = 0; holdUntilTicks = 0;
        activeIsSilence = false; silenceRefreshLeft = 0;
    }
}
