using System.Buffers.Binary;

namespace Solvelt.Marketplace.Api.Voice.TestSet;

/// <summary>Reads a recording (a WAV file) into what the recognisers take: G.711 mu-law, 8 kHz, mono (decision V-46). In memory only; nothing is written.</summary>
public static class WavAudio
{
    public const int MaxBytes = 20 * 1024 * 1024;

    /// <summary>Mu-law (format 7) at 8 kHz passes through unchanged; 16-bit PCM at any rate is mixed to mono, resampled to 8 kHz and encoded. Anything else is refused.</summary>
    public static byte[] ToMulaw8k(byte[] wav)
    {
        if (wav.Length < 44 || wav.Length > MaxBytes || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8)) throw new FormatException("Not a WAV file.");
        int format = 0, channels = 0, rate = 0, bits = 0, dataStart = -1, dataLength = 0;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.AsSpan(pos, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4, 4)), int.MaxValue);
            var body = pos + 8;
            if (id.SequenceEqual("fmt "u8) && body + 16 <= wav.Length)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8)) { dataStart = body; dataLength = Math.Min(size, wav.Length - body); break; }
            pos = body + size + (size & 1);
        }
        if (dataStart < 0 || channels < 1 || rate < 4000) throw new FormatException("The WAV file has no usable audio.");
        var data = wav.AsSpan(dataStart, dataLength);
        if (format == 7 && bits == 8 && channels == 1 && rate == 8000) return data.ToArray();
        if (format != 1 || bits != 16) throw new FormatException("Only 16-bit PCM or 8 kHz mu-law WAV files are supported.");
        var frames = dataLength / (2 * channels);
        var mono = new short[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0;
            for (var c = 0; c < channels; c++) sum += BinaryPrimitives.ReadInt16LittleEndian(data.Slice((i * channels + c) * 2, 2));
            mono[i] = (short)(sum / channels);
        }
        var at8k = Resample(mono, rate, 8000);
        var o = new byte[at8k.Length];
        for (var i = 0; i < o.Length; i++) o[i] = PhoneAudio.PcmToMulaw(at8k[i]);
        return o;
    }

    /// <summary>A whole-number step down averages the samples of each group (a crude low-pass, enough for speech); any other rate is linear interpolation.</summary>
    public static short[] Resample(short[] pcm, int from, int to)
    {
        if (from == to || pcm.Length == 0) return pcm;
        if (from % to == 0)
        {
            var k = from / to; var o = new short[pcm.Length / k];
            for (var i = 0; i < o.Length; i++) { var s = 0; for (var j = 0; j < k; j++) s += pcm[i * k + j]; o[i] = (short)(s / k); }
            return o;
        }
        var n = (int)((long)pcm.Length * to / from); var r = new short[n];
        for (var i = 0; i < n; i++)
        {
            var x = (double)i * from / to; var a = (int)x; var b = Math.Min(a + 1, pcm.Length - 1);
            r[i] = (short)(pcm[Math.Min(a, pcm.Length - 1)] + (pcm[b] - pcm[Math.Min(a, pcm.Length - 1)]) * (x - a));
        }
        return r;
    }

    /// <summary>A mu-law 8 kHz mono WAV file (format 7) around the samples, as SignalWire-style telephone audio.</summary>
    public static byte[] FromMulaw8k(byte[] mulaw)
    {
        using var ms = new MemoryStream(58 + mulaw.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(50 + mulaw.Length + (mulaw.Length & 1)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(18); w.Write((short)7); w.Write((short)1); w.Write(8000); w.Write(8000); w.Write((short)1); w.Write((short)8); w.Write((short)0);
        w.Write("data"u8); w.Write(mulaw.Length); w.Write(mulaw);
        if ((mulaw.Length & 1) == 1) w.Write((byte)0);
        return ms.ToArray();
    }
}
