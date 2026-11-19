namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// The small audio conversions a vendor that does not speak the telephone's format needs: G.711 mu-law to and from 16-bit PCM, whole-number down-sampling by averaging, linear up-sampling, and a WAV container.
/// Pure and allocation-light; no vendor, network or time in here.
/// </summary>
public static class PhoneAudio
{
    private const int Bias = 0x84, Clip = 32635;

    /// <summary>G.711 mu-law byte to a 16-bit sample.</summary>
    public static short MulawToPcm(byte mulaw)
    {
        var u = (byte)~mulaw;
        var sign = u & 0x80; var exponent = (u >> 4) & 0x07; var mantissa = u & 0x0F;
        var sample = (((mantissa << 3) + Bias) << exponent) - Bias;
        return (short)(sign != 0 ? -sample : sample);
    }

    /// <summary>A 16-bit sample to a G.711 mu-law byte.</summary>
    public static byte PcmToMulaw(short pcm)
    {
        int sample = pcm; var sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = -sample;
        if (sample > Clip) sample = Clip;
        sample += Bias;
        var exponent = 7;
        for (var mask = 0x4000; (sample & mask) == 0 && exponent > 0; mask >>= 1) exponent--;
        var mantissa = (sample >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    public static short[] DecodeMulaw(ReadOnlySpan<byte> mulaw)
    {
        var pcm = new short[mulaw.Length];
        for (var i = 0; i < mulaw.Length; i++) pcm[i] = MulawToPcm(mulaw[i]);
        return pcm;
    }

    /// <summary>Linear up-sampling by a whole factor (8 kHz to 16 kHz is 2): each new sample is on the line between its neighbours.</summary>
    public static short[] Upsample(short[] pcm, int factor)
    {
        if (factor <= 1 || pcm.Length == 0) return pcm;
        var o = new short[pcm.Length * factor];
        for (var i = 0; i < pcm.Length; i++)
        {
            var a = pcm[i]; var b = i + 1 < pcm.Length ? pcm[i + 1] : pcm[i];
            for (var k = 0; k < factor; k++) o[i * factor + k] = (short)(a + (b - a) * k / factor);
        }
        return o;
    }

    /// <summary>A mono 16-bit PCM WAV file (44-byte header) around the samples.</summary>
    public static byte[] Wav(short[] pcm, int sampleRate)
    {
        var data = pcm.Length * 2;
        using var ms = new MemoryStream(44 + data);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (var s in pcm) w.Write(s);
        return ms.ToArray();
    }

    /// <summary>The vendor's raw PCM s16le mono at a whole multiple of 8 kHz (16 kHz, 48 kHz) to mu-law 8 kHz, as chunks arrive: odd bytes and part of a group wait for the next chunk.</summary>
    public sealed class PcmToMulaw8k
    {
        private readonly int _ratio;
        private byte _carryByte; private bool _hasCarryByte;
        private readonly List<short> _group = [];

        public PcmToMulaw8k(int sourceRate)
        {
            if (sourceRate < 8000 || sourceRate % 8000 != 0) throw new NotSupportedException("The sample rate is not a whole multiple of 8 kHz.");
            _ratio = sourceRate / 8000;
        }

        public byte[] Add(ReadOnlySpan<byte> pcm16le)
        {
            var o = new List<byte>(pcm16le.Length / (2 * _ratio) + 2);
            var i = 0;
            while (i < pcm16le.Length || false)
            {
                byte lo, hi;
                if (_hasCarryByte) { lo = _carryByte; _hasCarryByte = false; }
                else if (i < pcm16le.Length) lo = pcm16le[i++];
                else break;
                if (i < pcm16le.Length) hi = pcm16le[i++]; else { _carryByte = lo; _hasCarryByte = true; break; }
                _group.Add((short)(lo | (hi << 8)));
                if (_group.Count == _ratio)
                {
                    var sum = 0; foreach (var s in _group) sum += s;
                    o.Add(PcmToMulaw((short)(sum / _ratio)));
                    _group.Clear();
                }
            }
            return [.. o];
        }
    }
}
