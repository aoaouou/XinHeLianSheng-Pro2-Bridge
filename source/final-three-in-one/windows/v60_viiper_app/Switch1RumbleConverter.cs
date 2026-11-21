using System;

namespace Y700Switch2V60Viiper;

// Decode first-generation Nintendo 4-byte low/high tones, then encode the
// existing physical Pro2 5-byte frame. This is parameter conversion, not PCM
// passthrough.
//
// Unit discipline (the reason Pro1 mode used to feel like ordinary rumble):
// a Switch 1 frame carries a band code, and dekuNukem's rumble_data_table
// defines it as an exponent, Hz = 10 * 2^(code / 32). A Pro2 subframe carries a
// ten-bit frequency control whose physical value is still unmeasured. The two
// are therefore not interchangeable numbers:
//   * passing the code straight through is an exponent used as a control;
//   * passing the derived Hz straight through is an unvalidated Hz-as-control.
// The only physical fact SDL's fixed references discard is accent above roughly
// 400 Hz, which the Switch 1 low band can request and the Pro2 low reference
// (unmeasured, but SDL's tuned "low tone") cannot. Bridging that needs a
// measured Pro2 control curve, which this project does not have, so no
// Hz-to-control curve is invented here. Amplitudes are carried exactly.
public static class Switch1RumbleConverter
{
    // Independent inverse of SDL_hidapi_switch.c hfa[101] (0..65535).
    // Do not use rounded milli-units: the first nonzero code is 514/65535,
    // not 10/1003. Source codes above SDL's safe 1.0 endpoint saturate here.
    private static readonly ushort[] Amplitudes =
    [0,514,775,921,1096,1303,1550,1843,2192,2606,3100,3686,4383,5213,6199,7372,
     7698,8039,8395,8767,9155,9560,9984,10426,10887,11369,11873,12398,12947,13520,
     14119,14744,15067,15397,15734,16079,16431,16790,17158,17534,17918,18310,18711,
     19121,19540,19967,20405,20851,21308,21775,22251,22739,23236,23745,24265,24797,
     25340,25894,26462,27041,27633,28238,28856,29488,30134,30794,31468,32157,32861,
     33581,34316,35068,35836,36620,37422,38242,39079,39935,40809,41703,42616,43549,
     44503,45477,46473,47491,48531,49593,50679,51789,52923,54082,55266,56476,57713,
     58977,60268,61588,62936,64315,65535];

    public readonly record struct Tones(ushort LowHz, ushort LowAmplitude, ushort HighHz, ushort HighAmplitude);

    public static Tones Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < 4) throw new ArgumentException("Switch Pro rumble requires four bytes.", nameof(encoded));
        int highCode = ((encoded[0] | ((encoded[1] & 1) << 8)) >> 2) + 0x60;
        int lowCode = (encoded[2] & 0x7F) + 0x40;
        int highAmplitude = encoded[1] >> 1;
        int lowAmplitude = encoded[3] >= 0x40
            ? ((encoded[3] - 0x40) * 2 + (encoded[2] >> 7)) : 0;
        return new(Frequency(lowCode), Amplitude(lowAmplitude), Frequency(highCode), Amplitude(highAmplitude));
    }

    /// <summary>Exponential band-code to Hz, per dekuNukem's documented formula.</summary>
    public static ushort Frequency(int code) =>
        (ushort)Math.Clamp(Math.Round(10 * Math.Pow(2, code / 32.0)), 40, 1253);
    private static ushort Amplitude(int code) =>
        (ushort)((Amplitudes[Math.Clamp(code, 0, Amplitudes.Length - 1)] * 29000u + 32767u) / 65535u);

    public static bool TryMap(ReadOnlySpan<byte> feedback, out Pro2OutputPacket packet, out string reason)
    {
        packet = default!; reason = "";
        if (feedback.Length != 10) { reason = "Switch Pro feedback must be ten bytes"; return false; }
        byte? led = (feedback[8] & 2) != 0 ? feedback[9] : null;
        if ((feedback[8] & 1) == 0)
        { reason = led.HasValue ? "led-only feedback has no physical Pro2 rumble payload" : "feedback has no rumble flag"; return false; }
        var left = Decode(feedback[..4]); var right = Decode(feedback.Slice(4, 4));
        // Controls stay at the two validated Pro2 references. The Switch 1 band
        // codes are reported as source intent in telemetry, never as controls.
        ushort leftLowControl = Pro2RumbleFrequencyPolicy.ReferenceLowControl;
        ushort leftHighControl = Pro2RumbleFrequencyPolicy.ReferenceHighControl;
        ushort rightLowControl = Pro2RumbleFrequencyPolicy.ReferenceLowControl;
        ushort rightHighControl = Pro2RumbleFrequencyPolicy.ReferenceHighControl;
        packet = Pro2OutputPacketMapper.BuildRaw02Packet(
            leftLowControl, left.LowAmplitude,
            leftHighControl, left.HighAmplitude,
            rightLowControl, right.LowAmplitude,
            rightHighControl, right.HighAmplitude,
            "switch1-hd-converted") with
        {
            PlayerLedMask = led,
            NativePro1Rumble = feedback[..8].ToArray(),
            // Amplitudes are converted exactly; no frequency control is clamped
            // away because none is derived from the source any more.
            FrequencyClamps = 0,
            ConversionTelemetry = $"frequency_mapping={Pro2RumbleFrequencyPolicy.Mapping} band_codes_are_exponents=true" +
                $" frequency_controls={leftLowControl},{leftHighControl},{rightLowControl},{rightHighControl}" +
                $" source_left_hz={left.LowHz},{left.HighHz} source_right_hz={right.LowHz},{right.HighHz}"
        };
        return true;
    }
}
