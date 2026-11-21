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
    public const double RefreshIntervalMs = 36;
    public const double HoldAfterSourceMs = 250;
    public const double SilenceHoldMs = 100;
    public const int SilenceRefreshMax = 3;

    private byte[]? active;          // state to keep re-sending (silence included)
    private string source = "";
    private long revision, dueTicks, holdUntilTicks;
    private bool activeIsSilence;
    private int silenceRefreshLeft;

    public bool Active => active != null;
    public long Generated { get; private set; }
    public long Cancelled { get; private set; }
    public long SilenceBrakes { get; private set; }
    public long Expiries { get; private set; }

    public string Telemetry =>
        "hd_hold=" + Active +
        " hd_hold_refresh_ms=" + RefreshIntervalMs.ToString("F0", CultureInfo.InvariantCulture) +
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
        // The host frame itself is already being written. The next keep-alive
        // is due one full cadence later, exactly as WinJoyCon2's worker only
        // re-sends _active once now - _last_time >= _refresh_interval().
        dueTicks = now + Ticks(RefreshIntervalMs);
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
            byte[] stop = Pro2NativeRumbleEncoder.Neutralize(active);
            stop[1] = stop[17] = (byte)(0x50 | (sequence & 15));
            item = new(stop, true, now, Source: source, GeneratedKind: "hd_hold_stop");
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
        item = new(copy, true, now, Source: source, GeneratedKind: "hd_hold");
        if (activeIsSilence) { silenceRefreshLeft--; SilenceBrakes++; }
        dueTicks = now + Ticks(RefreshIntervalMs);
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
