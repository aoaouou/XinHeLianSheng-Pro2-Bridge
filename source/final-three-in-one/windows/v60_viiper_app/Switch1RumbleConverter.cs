using System;

namespace Y700Switch2V60Viiper;

// Pro1 (Switch 1) rumble -> physical Pro2, ported function-for-function from
// WinJoyCon2's proven `virtual/rumble_hd.py` (user-tuned, near-native feel).
//
// The two hard-won rules from that project's testing log (n-mwkdv9vu2o2n):
//   1) NEVER move the Switch 1 frequency onto Pro2. The two frequency domains
//      have different units (JC1 8-bit code vs JC2 10-bit control); copying
//      0x74 into Pro2 made it "lower and coarser". Instead pin the two native
//      JS2 controls 0x187 / 0x112 (what SDL hard-codes) and carry amplitude.
//   2) HF amplitude uses SDL's hfa[101] lookup, LF amplitude is LINEAR over
//      101 steps. Using the HF table for LF (as an earlier Plus build did)
//      made low-frequency vibration far too weak.
public static class Switch1RumbleConverter
{
    // SDL_hidapi_switch.c hfa[101][2] first column: 0..65535 amplitude keys.
    // Index = hf_code / 2 (hf_code is 0x00..0xC8 in steps of 2).
    private static readonly ushort[] HfaKeys =
    [0,514,775,921,1096,1303,1550,1843,2192,2606,3100,3686,4383,5213,6199,7372,
     7698,8039,8395,8767,9155,9560,9984,10426,10887,11369,11873,12398,12947,13520,
     14119,14744,15067,15397,15734,16079,16431,16790,17158,17534,17918,18310,18711,
     19121,19540,19967,20405,20851,21308,21775,22251,22739,23236,23745,24265,24797,
     25340,25894,26462,27041,27633,28238,28856,29488,30134,30794,31468,32157,32861,
     33581,34316,35068,35836,36620,37422,38242,39079,39935,40809,41703,42616,43549,
     44503,45477,46473,47491,48531,49593,50679,51789,52923,54082,55266,56476,57713,
     58977,60268,61588,62936,64315,65535];

    private const int Jc2RumbleMax = 29000;
    private const int Jc1LfAmpZero = 0x40;

    /// <summary>
    /// decode_jc1_block: JC1 4-byte block -> (hf_freq9, hf_amp8, lf_freq7, lf_amp9).
    /// Bit layout per dekuNukem bluetooth_hid_notes.md "Rumble data".
    /// </summary>
    public static (int HfFreq, int HfAmp, int LfFreq, int LfAmp) DecodeBlock(ReadOnlySpan<byte> blk)
    {
        if (blk.Length < 4) return (0, 0, 0, 0);
        int b0 = blk[0], b1 = blk[1], b2 = blk[2], b3 = blk[3];
        int hfFreq = ((b1 & 0x01) << 8) | b0;
        int hfAmp = b1 & 0xFE;
        int lfFreq = b2 & 0x7F;
        int lfAmp = ((b2 & 0x80) << 1) | b3;   // bit8 = mid-step flag
        return (hfFreq, hfAmp, lfFreq, lfAmp);
    }

    /// <summary>jc1_hf_amp16: SDL HFA table lookup, hf_code 0x00..0xC8 step 2.</summary>
    public static double HfAmp16(int hfCode)
    {
        int code = Math.Clamp(hfCode & 0xFE, 0, 0xC8);
        return HfaKeys[code / 2];
    }

    /// <summary>jc1_lf_amp16: LINEAR 101-step mapping (0..65535).</summary>
    public static double LfAmp16(int lfAmp9)
    {
        int v = lfAmp9 & 0x1FF;
        int low = v & 0xFF;
        bool mid = (v & 0x100) != 0;
        if (low <= Jc1LfAmpZero && !mid) return 0;
        int step = Math.Clamp((low - Jc1LfAmpZero) * 2 + (mid ? 1 : 0), 0, 100);
        return 65535.0 * step / 100.0;
    }

    /// <summary>is_neutral: amplitude zero, regardless of carried frequency code.</summary>
    public static bool IsNeutral(ReadOnlySpan<byte> blk)
    {
        var (_, hfAmp, _, lfAmp) = DecodeBlock(blk);
        return hfAmp == 0 && (lfAmp & 0xFF) <= Jc1LfAmpZero;
    }

    /// <summary>
    /// encode_jc2: (hf_amp16, lf_amp16) -> 5-byte Pro2 frame, an exact port of
    /// SDL EncodeHDRumble with RUMBLE_MAX=29000 and native 0x187/0x112 carriers.
    /// </summary>
    public static byte[] EncodeJc2(double hfAmp16, double lfAmp16,
        int hfFreq = 0x187, int lfFreq = 0x112)
    {
        int hf = (int)Math.Round(Math.Clamp(hfAmp16, 0, 65535) * Jc2RumbleMax / 65535.0);
        int lf = (int)Math.Round(Math.Clamp(lfAmp16, 0, 65535) * Jc2RumbleMax / 65535.0);
        hfFreq &= 0x3FF;
        lfFreq &= 0x3FF;
        return
        [
            (byte)(hfFreq & 0xFF),
            (byte)(((hf >> 4) & 0xFC) | ((hfFreq >> 8) & 0x03)),
            (byte)((hf >> 12) | ((lfFreq & 0x0F) << 4)),
            (byte)((lf & 0xC0) | ((lfFreq >> 4) & 0x3F)),
            (byte)((lf >> 8) & 0xFF),
        ];
    }

    // Decode/Frequency/Amplitude are the canonical Pro1 tone reader used by
    // Pro1PhysicalRumble (native->native scaling and amplitude->code oracle).
    // Kept byte-exact to the official v3.2.7 implementation: do not fold the
    // WinJoyCon2 conversion below into these, they serve different callers.
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

    private static ushort Frequency(int code) =>
        (ushort)Math.Clamp(Math.Round(10 * Math.Pow(2, code / 32.0)), 40, 1253);
    private static ushort Amplitude(int code) =>
        (ushort)((HfaKeys[Math.Clamp(code, 0, HfaKeys.Length - 1)] * 29000u + 32767u) / 65535u);

    /// <summary>
    /// jc1_block_to_jc2 -> full 10-byte feedback -> Pro2 raw02. Mirrors
    /// WinJoyCon2: pin Pro2 native carriers, carry faithfully-converted amplitude.
    /// </summary>
    public static bool TryMap(ReadOnlySpan<byte> feedback, out Pro2OutputPacket packet, out string reason)
    {
        packet = default!; reason = "";
        if (feedback.Length != 10) { reason = "Switch Pro feedback must be ten bytes"; return false; }
        byte? led = (feedback[8] & 2) != 0 ? feedback[9] : null;
        if ((feedback[8] & 1) == 0)
        { reason = led.HasValue ? "led-only feedback has no physical Pro2 rumble payload" : "feedback has no rumble flag"; return false; }

        byte[] left5 = BlockToJc2(feedback[..4]) ?? NeutralJc2();
        byte[] right5 = BlockToJc2(feedback.Slice(4, 4)) ?? NeutralJc2();

        byte[] report = new byte[64];
        report[0] = 0x02;
        report[1] = 0x50;
        report[17] = 0x50;
        for (int i = 0; i < 5; i++)
        {
            report[2 + i] = left5[i];
            report[7 + i] = left5[i];
            report[12 + i] = left5[i];
            report[18 + i] = right5[i];
            report[23 + i] = right5[i];
            report[28 + i] = right5[i];
        }
        bool active = Pro2NativeRumbleEncoder.HasEffect(report);
        packet = new Pro2OutputPacket(report, "switch1-hd-converted", active, led) with
        {
            NativePro1Rumble = feedback[..8].ToArray(),
            ConversionTelemetry = "source=winjoycon2-jc1_block_to_jc2 carriers=0x187,0x112 amplitude=hfa+linear"
        };
        return true;
    }

    private static byte[]? BlockToJc2(ReadOnlySpan<byte> blk)
    {
        if (IsNeutral(blk)) return null;
        var (_, hfAmp, _, lfAmp) = DecodeBlock(blk);
        double hf16 = HfAmp16(hfAmp);
        double lf16 = LfAmp16(lfAmp);
        if (hf16 <= 0 && lf16 <= 0) return null;
        return EncodeJc2(hf16, lf16);  // native 0x187/0x112 carriers, like WinJoyCon2
    }

    private static byte[] NeutralJc2() => EncodeJc2(0, 0);
}
