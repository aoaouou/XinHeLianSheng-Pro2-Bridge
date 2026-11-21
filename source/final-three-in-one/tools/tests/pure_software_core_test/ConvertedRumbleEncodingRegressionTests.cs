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

        // Independent source oracle: Joy-Con frequency code and SDL amplitude
        // lookup; protocol controls remain an explicitly unvalidated proxy.
        var quiet = Switch1RumbleConverter.Decode(new byte[] { 0, 3, 0xc0, 0x40 });
        ushort firstStep = (ushort)((514u * 29000u + 32767u) / 65535u);
        check(quiet.LowHz == 160 && quiet.HighHz == 320 &&
            quiet.LowAmplitude == firstStep && quiet.HighAmplitude == firstStep,
            "Pro1 first nonzero amplitude uses precise SDL inverse rather than rounded milli-units");
        byte[] source = [0xfc, 0xc9, 0xff, 0x71, 0, 1, 0x40, 0x40, 1, 0];
        var sourceLeft = Switch1RumbleConverter.Decode(source);
        check(sourceLeft.HighHz == 1253 && sourceLeft.LowHz == 626,
            "Pro1 source decoder retains frequency above final proxy limit");
        check(Switch1RumbleConverter.TryMap(source, out var mapped, out _) && mapped.FrequencyClamps == 0 &&
            mapped.ConversionTelemetry.Contains("legacy_hz_proxy_unvalidated") &&
            mapped.ConversionTelemetry.Contains("band_codes_are_exponents=true"),
            "Pro1 conversion labels the mapping and never reports a clip it did not perform");
        // A Switch 1 band code is an exponent (Hz = 10 * 2^(code/32)) and a Pro2
        // control is unmeasured, so the only defensible conversion keeps both
        // validated Pro2 references and carries amplitude alone. Feeding the
        // source Hz in as a control is the unit error that made Pro1 feel ordinary.
        check((Read(mapped.Report, 2) & 1023) == Pro2RumbleFrequencyPolicy.ReferenceHighControl &&
            ((Read(mapped.Report, 2) >> 20) & 1023) == Pro2RumbleFrequencyPolicy.ReferenceLowControl,
            "Pro1 conversion keeps both Pro2 band references instead of a Hz-as-control proxy");
        check((Read(mapped.Report, 18) & 1023) == Pro2RumbleFrequencyPolicy.ReferenceHighControl &&
            ((Read(mapped.Report, 18) >> 20) & 1023) == Pro2RumbleFrequencyPolicy.ReferenceLowControl,
            "Pro1 conversion applies the same references to the right actuator");
        check(Pro2RumbleFrequencyPolicy.FromEstimatedHz(double.NaN) == 40 &&
            Pro2RumbleFrequencyPolicy.FromEstimatedHz(900) == 511 &&
            Pro2RumbleFrequencyPolicy.FromEstimatedHz(160.5) == 161,
            "shared proxy handles nonfinite, out-of-range and half-step inputs deterministically");

        // The Switch 1 band code is an exponent, not a linear control:
        // dekuNukem's rumble_data_table gives Hz = 10 * 2^(code / 32), so adding
        // 0x20 to the code doubles the frequency. Any conversion that feeds the
        // decoded Hz into a ten-bit Pro2 control is therefore off by a whole
        // dimension, which is exactly why Pro1 mode used to feel ordinary.
        check(Switch1RumbleConverter.Frequency(96) == 80 &&
            Switch1RumbleConverter.Frequency(128) == 160 &&
            Switch1RumbleConverter.Frequency(160) == 320 &&
            Switch1RumbleConverter.Frequency(192) == 640,
            "band code is a doubling exponent at the documented 0x20 step");
        check(Pro2RumbleFrequencyPolicy.FromEstimatedHz(
                Switch1RumbleConverter.Frequency(192)) == Pro2RumbleFrequencyPolicy.MaximumLegacyControl,
            "the discarded Hz-as-control path would have clipped a 640 Hz band to the ten-bit ceiling");
        foreach (byte code in new byte[] { 0x04, 0x40, 0x60, 0xFC })
        {
            byte[] frame = [code, 0xC8, code, 0x72, 0, 1, 0x40, 0x40, 1, 0];
            check(Switch1RumbleConverter.TryMap(frame, out var probe, out _) &&
                (Read(probe.Report, 2) & 1023) == Pro2RumbleFrequencyPolicy.ReferenceHighControl &&
                ((Read(probe.Report, 2) >> 20) & 1023) == Pro2RumbleFrequencyPolicy.ReferenceLowControl,
                "source band code never becomes a ten-bit Pro2 control " + code);
        }
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
