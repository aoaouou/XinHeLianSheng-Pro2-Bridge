using System;
using System.Linq;
using Y700Switch2V60Viiper;

internal static class ConvertedRumbleEncodingRegressionTests
{
    private const ulong AmplitudeMask = (1023UL << 10) | (1023UL << 30);
    public static void Run(Action<bool, string> check)
    {
        var random = new Random(261023);
        foreach (int sample in Enumerable.Range(0, 128))
        {
            var report = new byte[64]; random.NextBytes(report);
            report[0] = 2; report[1] = 0x53; report[17] = 0x59;
            var original = report.ToArray();
            foreach (double gain in new[] { 0.0, 0.1, 0.5, 1.0, 2.0, 3.0 })
            {
                var ble = new byte[33];
                check(Pro2BleRumblePacketEncoder.TryEncodeRaw02(report, 0x1c, ble, out bool active, out _, gain),
                    "converted known-frame transport accepted");
                check(ble[0] == 0 && ble[1] == 0x5c && ble[17] == 0x5c,
                    "converted transport owns sequence only");
                bool expectedActive = false;
                for (int side = 1; side <= 17; side += 16)
                for (int frame = 0; frame < 3; frame++)
                {
                    int offset = side + 1 + frame * 5;
                    ulong before = Read(report, offset), after = Read(ble, offset);
                    check((before & ~AmplitudeMask) == (after & ~AmplitudeMask),
                        "all converted frequency and control bits remain in place");
                    foreach (int shift in new[] { 10, 30 })
                    {
                        int expected = (int)Math.Clamp(Math.Round(((before >> shift) & 1023) * gain,
                            MidpointRounding.AwayFromZero), 0, 1023);
                        check((int)((after >> shift) & 1023) == expected,
                            "converted gain scales packed amplitude exactly once");
                        expectedActive |= expected != 0;
                    }
                    if (gain == 1) check(before == after, "unity retains all six converted subframes");
                }
                check(active == expectedActive && original.SequenceEqual(report),
                    "converted active flag follows encoded data without mutating source");
            }
        }

        var payload = Pro2OutputPacketMapper.BuildRaw02Packet(160, 29000, 320, 29000,
            180, 9800, 400, 9800, "test");
        var final = Encode(payload, 1, check);
        check(FirstAmplitude(final, 2) == 453 && SecondAmplitude(final, 2) == 453,
            "unity keeps safe reference 453 instead of old second normalization to 1023");
        check(FirstAmplitude(final, 18) == 153 && SecondAmplitude(final, 18) == 153,
            "lower right amplitude remains independent at 153 not 345");
        final = Encode(payload, .1, check);
        check(FirstAmplitude(final, 2) == 45 && SecondAmplitude(final, 2) == 45,
            "ten percent uses 45 not old 102");
        check((Read(final, 2) & 1023) == 320 && ((Read(final, 2) >> 20) & 1023) == 160,
            "SDL high-first low-second ordering is unchanged by transport");
        var tiny = Pro2OutputPacketMapper.BuildRaw02Packet(160, 1, 320, 1, 160, 1, 320, 1, "test");
        check(!tiny.Active && !Pro2NativeRumbleEncoder.HasEffect(Encode(tiny, 1, check)),
            "sub-step amplitudes are classified from packed output");
        var saturated = Pro2OutputPacketMapper.BuildRaw02Packet(160, ushort.MaxValue, 320, ushort.MaxValue,
            160, 0, 320, 0, "test");
        check(FirstAmplitude(Encode(saturated, 1, check), 2) == 453,
            "over-range caller clamps before quantization instead of wrapping");

        for (int value = 0; value <= 255; value++)
        {
            var strong = Pro2OutputPacketMapper.BuildOrdinaryPacket(0, (byte)value, "ordinary");
            var weak = Pro2OutputPacketMapper.BuildOrdinaryPacket((byte)value, 0, "ordinary");
            var a = Encode(strong, 1, check); var b = Encode(weak, 1, check);
            int expected = (int)(((value * 29000u + 127u) / 255u) >> 6);
            check(FirstAmplitude(a, 2) == 0 && SecondAmplitude(a, 2) == expected &&
                FirstAmplitude(a, 18) == 0 && SecondAmplitude(a, 18) == 0,
                "ordinary strong motor stays on left low-frequency actuator " + value);
            check(FirstAmplitude(b, 18) == expected && SecondAmplitude(b, 18) == 0 &&
                FirstAmplitude(b, 2) == 0 && SecondAmplitude(b, 2) == 0,
                "ordinary weak motor stays on right high-frequency actuator " + value);
            check((Read(a, 2) & 1023) == Pro2RumbleFrequencyPolicy.ReferenceHighControl &&
                ((Read(a, 2) >> 20) & 1023) == Pro2RumbleFrequencyPolicy.ReferenceLowControl,
                "ordinary intensity does not invent a frequency sweep " + value);
            check(!strong.PreserveNativePayload && !weak.PreserveNativePayload,
                "payload-safe conversion does not mislabel source as native");
        }
        check(Pro2OutputPacketMapper.TryMapFeedback(ViiperDeviceProfile.Xbox, new byte[] { 255, 0 },
            out var xbox, out _) && SecondAmplitude(Encode(xbox, 1, check), 2) == 453,
            "Xbox feedback strong/weak source ordering preserved");
        check(Pro2OutputPacketMapper.TryMapFeedback(ViiperDeviceProfile.DualSenseLike, new byte[] { 0, 255 },
            out var ps5, out _) && SecondAmplitude(Encode(ps5, 1, check), 2) == 453,
            "DualSense feedback weak/strong source ordering preserved");

        // WinJoyCon2's proven mapping: pin Pro2 native carriers 0x187/0x112,
        // carry amplitude via HFA lookup (HF) and linear 101-step (LF).
        byte[] source = [0xfc, 0xc9, 0xff, 0x71, 0, 1, 0x40, 0x40, 1, 0];
        check(Switch1RumbleConverter.TryMap(source, out var mapped, out _) &&
            mapped.ConversionTelemetry.Contains("switch1-amplitude-to-native-pro2"),
            "Pro1 amplitude conversion emits native Pro2 report");
        // Physical Pro2: ONE subframe per motor block (real capture 795/795).
        // Slots 2 and 3 must stay zero so this matches Switch2's real output.
        foreach (int off in new[] { 2, 18 })
        {
            check((Read(mapped.Report, off) & 1023) == 0x187 &&
                 ((Read(mapped.Report, off) >> 20) & 1023) == 0x112,
                "Pro2 native carriers 0x187/0x112 pinned at subframe " + off);
        }
        foreach (int off in new[] { 7, 12, 23, 28 })
            check(Read(mapped.Report, off) == 0,
                "native Pro2 keeps subframe 2/3 silent at " + off);
        // A Pro1 max-amplitude block must produce a strong Pro2 amplitude, not a
        // weak one: full HFA key 0xC8 -> 65535 -> 29000 and LF 0x72 -> 29000.
        byte[] loud = [0x00, 0xC8, 0x40, 0x72, 0, 1, 0x40, 0x40, 1, 0];
        check(Switch1RumbleConverter.IsNeutral(new byte[] { 0, 1, 0x40, 0x40 }),
            "JC1 neutral 00 01 40 40 detected regardless of frequency code");
        check(Switch1RumbleConverter.HfAmp16(0xC8) == 65535 &&
            Switch1RumbleConverter.LfAmp16(0x72) == 65535,
            "max JC1 HF/LF amplitude maps to full 65535 scale");
        check(Switch1RumbleConverter.LfAmp16(0x40) == 0 && Switch1RumbleConverter.LfAmp16(0x59) == 65535.0 * 50 / 100,
            "LF amplitude is linear across its 101 steps");
        // Reference value from WinJoyCon2's jc1_block_to_jc2 for 00 C8 40 72:
        // HFA[0xC8]=65535 and LF linear=65535 -> encode_jc2 -> 87 15 27 51 71,
        // packed HF/LF amplitude 28992. Match the proven implementation byte for byte.
        byte[] refFrame = Switch1RumbleConverter.EncodeJc2(65535, 65535);
        check(refFrame.SequenceEqual(new byte[] { 0x87, 0x15, 0x27, 0x51, 0x71 }),
            "encode_jc2 full-scale matches WinJoyCon2 reference bytes 87 15 27 51 71");
        check(Switch1RumbleConverter.TryMap(loud, out var loudPacket, out _) && loudPacket.Active &&
            loudPacket.Report.AsSpan(2, 5).SequenceEqual(refFrame),
            "full Pro1 amplitude maps to Pro2 exactly as WinJoyCon2 does");
        byte[] neutralSource = [0, 1, 0x40, 0x40, 0, 1, 0x40, 0x40, 1, 0];
        check(Switch1RumbleConverter.TryMap(neutralSource, out var sourceStopped, out _) && !sourceStopped.Active,
            "Pro1 neutral frame still stops the physical actuator");

        var invalid = payload.Report.ToArray(); invalid[17] = 0x70;
        check(!Pro2BleRumblePacketEncoder.TryEncodeRaw02(invalid, 0, new byte[33], out _, out _),
            "converted unknown second motor format is rejected instead of guessed");
        check(!Pro2BleRumblePacketEncoder.TryEncodeRaw02(payload.Report.AsSpan(0, 63), 0, new byte[33], out _, out _) &&
            !Pro2BleRumblePacketEncoder.TryEncodeRaw02(payload.Report, 0, new byte[32], out _, out _),
            "converted short input and destination fail safely");
    }

    private static byte[] Encode(Pro2OutputPacket packet, double gain, Action<bool, string> check)
    {
        var output = new byte[33];
        check(Pro2BleRumblePacketEncoder.TryEncodeRaw02(packet.Report, 7, output, out _, out _, gain),
            "mapped packet encodes");
        return output;
    }
    private static int FirstAmplitude(byte[] data, int offset) => (int)((Read(data, offset) >> 10) & 1023);
    private static int SecondAmplitude(byte[] data, int offset) => (int)((Read(data, offset) >> 30) & 1023);
    private static ulong Read(byte[] data, int offset)
    {
        ulong value = 0;
        for (int i = 0; i < 5; i++) value |= (ulong)data[offset + i] << (8 * i);
        return value;
    }
}
