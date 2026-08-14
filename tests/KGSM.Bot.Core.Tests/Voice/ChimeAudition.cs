using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// Writes the two tones out as playable files.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a test and deliberately not part of the suite</b> — it asserts nothing, because the
/// question it answers is whether the pair sounds right, which no assertion reaches. The properties
/// that <em>can</em> be checked are checked in <see cref="VoiceChimesTests"/>.
/// </para>
/// <para>
/// Off unless <c>KGSM_VOICE_CHIMES_OUT</c> names a directory to write into, so tuning the notes never
/// means editing the code twice or leaving files behind on a build server.
/// </para>
/// </remarks>
public class ChimeAudition
{
    [Fact]
    public void WriteTheTones()
    {
        string? into = Environment.GetEnvironmentVariable("KGSM_VOICE_CHIMES_OUT");
        if (string.IsNullOrWhiteSpace(into)) return;

        Directory.CreateDirectory(into);

        foreach (VoiceChime chime in Enum.GetValues<VoiceChime>())
        {
            File.WriteAllBytes(
                Path.Combine(into, $"chime-{chime.ToString().ToLowerInvariant()}.wav"),
                Wav(VoiceChimes.Pcm(chime)));

            File.WriteAllLines(
                Path.Combine(into, $"chime-{chime.ToString().ToLowerInvariant()}.txt"),
                Describe(VoiceChimes.Pcm(chime)));
        }
    }

    /// <summary>How the tone's brightness and level move across its length, in tenths of a second.</summary>
    private static IEnumerable<string> Describe(byte[] pcm)
    {
        var samples = new short[pcm.Length / 4];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(pcm, i * 4);

        var step = (int)(0.1 * VoiceChimes.SampleRate);

        for (int at = 0; at + step <= samples.Length; at += step)
        {
            double energy = 0;
            double difference = 0;
            short peak = 0;

            for (int i = at + 1; i < at + step; i++)
            {
                double x = samples[i];
                double d = samples[i] - samples[i - 1];
                energy += x * x;
                difference += d * d;
                peak = Math.Max(peak, Math.Abs(samples[i]) > short.MaxValue ? short.MaxValue : (short)Math.Abs(samples[i]));
            }

            double brightness = energy <= 0 ? 0 : difference / energy;
            yield return $"{(double)at / VoiceChimes.SampleRate:F2}s  peak={peak,6}  brightness={brightness:F4}";
        }
    }

    /// <summary>Wraps raw PCM in the 44-byte header that makes it a file anything will play.</summary>
    private static byte[] Wav(byte[] pcm)
    {
        const int Channels = 2;
        const int Bits = 16;
        int byteRate = VoiceChimes.SampleRate * Channels * Bits / 8;

        using var buffer = new MemoryStream();
        using var write = new BinaryWriter(buffer);

        write.Write("RIFF"u8);
        write.Write(36 + pcm.Length);
        write.Write("WAVE"u8);
        write.Write("fmt "u8);
        write.Write(16);                                  // PCM header length
        write.Write((short)1);                            // uncompressed
        write.Write((short)Channels);
        write.Write(VoiceChimes.SampleRate);
        write.Write(byteRate);
        write.Write((short)(Channels * Bits / 8));        // block align
        write.Write((short)Bits);
        write.Write("data"u8);
        write.Write(pcm.Length);
        write.Write(pcm);

        write.Flush();
        return buffer.ToArray();
    }
}
