using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The audio conversions the Yiddish Labs adapters need (decision V-22): G.711 mu-law, down- and up-sampling, a WAV container.</summary>
public class PhoneAudioTests
{
    [Fact]
    public void Mulaw_matches_the_G711_reference_values()
    {
        Assert.Equal(0, PhoneAudio.MulawToPcm(0xFF));                    // +0
        Assert.Equal(0, PhoneAudio.MulawToPcm(0x7F));                    // -0
        Assert.Equal(32124, PhoneAudio.MulawToPcm(0x80));                // the largest positive code
        Assert.Equal(-32124, PhoneAudio.MulawToPcm(0x00));               // the largest negative code
        Assert.Equal(0xFF, PhoneAudio.PcmToMulaw(0));
        Assert.Equal(0x80, PhoneAudio.PcmToMulaw(32767));                // clipped to the largest code
        Assert.Equal(0x00, PhoneAudio.PcmToMulaw(-32768));
        Assert.Equal(0xFF, PhoneAudio.PcmToMulaw(1));                    // tiny samples are the zero code
    }

    [Fact]
    public void Every_mulaw_code_survives_a_trip_through_pcm_except_the_second_zero()
    {
        for (var b = 0; b < 256; b++)
        {
            var back = PhoneAudio.PcmToMulaw(PhoneAudio.MulawToPcm((byte)b));
            if (b == 0x7F) Assert.Equal(0xFF, back);                     // -0 and +0 are one sample value
            else Assert.Equal((byte)b, back);
        }
    }

    [Fact]
    public void Encoding_keeps_the_sign_and_the_order_of_loudness()
    {
        short prev = short.MinValue;
        for (var s = -32000; s <= 32000; s += 97)
        {
            var d = PhoneAudio.MulawToPcm(PhoneAudio.PcmToMulaw((short)s));
            Assert.True(Math.Sign(d) == Math.Sign(s) || Math.Abs(s) < 40, $"sign of {s}");    // a value near zero may land on either zero
            Assert.True(d >= prev, $"order at {s}");
            prev = d;
            Assert.InRange(Math.Abs(d - s), 0, Math.Max(8, Math.Abs(s) / 16 + 16));           // companding error stays small relative to the level
        }
    }

    [Fact]
    public void Decoding_a_buffer_gives_one_sample_per_byte() => Assert.Equal([0, 32124, -32124], PhoneAudio.DecodeMulaw([0xFF, 0x80, 0x00]));

    [Fact]
    public void Upsampling_puts_each_new_sample_on_the_line_between_its_neighbours()
    {
        Assert.Equal([0, 50, 100, 100], PhoneAudio.Upsample([0, 100], 2));
        Assert.Equal([0, 100], PhoneAudio.Upsample([0, 100], 1));
        Assert.Empty(PhoneAudio.Upsample([], 2));
    }

    [Fact]
    public void A_wav_has_the_44_byte_header_the_rate_and_the_samples()
    {
        var wav = PhoneAudio.Wav([1, -2, 3], 8000);
        Assert.Equal(44 + 6, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4)); Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(36 + 6, BitConverter.ToInt32(wav, 4)); Assert.Equal(6, BitConverter.ToInt32(wav, 40));
        Assert.Equal((1, 16, 8000, 16000), (BitConverter.ToInt16(wav, 22), BitConverter.ToInt16(wav, 34), BitConverter.ToInt32(wav, 24), BitConverter.ToInt32(wav, 28)));   // mono, 16 bit, rate, bytes per second
        Assert.Equal([1, -2, 3], new[] { BitConverter.ToInt16(wav, 44), BitConverter.ToInt16(wav, 46), BitConverter.ToInt16(wav, 48) });
    }

    private static byte[] Pcm(params short[] s) { var b = new byte[s.Length * 2]; Buffer.BlockCopy(s, 0, b, 0, b.Length); return b; }

    [Fact]
    public void Sixteen_kHz_pcm_becomes_8_kHz_mulaw_by_averaging_pairs()
    {
        var c = new PhoneAudio.PcmToMulaw8k(16000);
        var o = c.Add(Pcm(1000, 3000, -2000, -4000));                   // pairs average to 2000 and -3000
        Assert.Equal([PhoneAudio.PcmToMulaw(2000), PhoneAudio.PcmToMulaw(-3000)], o);
    }

    [Fact]
    public void Forty_eight_kHz_averages_groups_of_six()
    {
        var c = new PhoneAudio.PcmToMulaw8k(48000);
        Assert.Equal([PhoneAudio.PcmToMulaw(600)], c.Add(Pcm(100, 300, 500, 700, 900, 1100)));   // mean 600
    }

    [Fact]
    public void Chunk_boundaries_never_change_the_result_even_in_the_middle_of_a_sample_or_a_group()
    {
        var all = Pcm(Enumerable.Range(0, 120).Select(i => (short)((i * 487) % 20000 - 10000)).ToArray());
        var whole = new PhoneAudio.PcmToMulaw8k(16000).Add(all);
        for (var step = 1; step <= 7; step++)
        {
            var c = new PhoneAudio.PcmToMulaw8k(16000); var parts = new List<byte>();
            for (var i = 0; i < all.Length; i += step) parts.AddRange(c.Add(all.AsSpan(i, Math.Min(step, all.Length - i))));
            Assert.Equal(whole, parts.ToArray());
        }
        Assert.Equal(60, whole.Length);
    }

    [Theory]
    [InlineData(0)] [InlineData(7999)] [InlineData(12000)] [InlineData(22050)]
    public void A_rate_that_is_not_a_whole_multiple_of_8_kHz_is_refused(int rate) => Assert.Throws<NotSupportedException>(() => new PhoneAudio.PcmToMulaw8k(rate));
}
