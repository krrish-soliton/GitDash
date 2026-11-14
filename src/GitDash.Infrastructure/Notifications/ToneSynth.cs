using GitDash.Application.Settings;

namespace GitDash.Infrastructure.Notifications;

/// <summary>
/// Builds the built-in notification sounds as small WAV files in memory, so the app ships no audio assets and
/// every platform can play them the same way.
/// </summary>
public static class ToneSynth
{
    private const int SampleRate = 22050;

    /// <summary>One note: pitch, length, silence after it, how fast it fades, and how much of a brighter overtone to add.</summary>
    private sealed record Note(double Hz, int Ms, int GapMs = 0, double Decay = 5, double Overtone = 0.25, bool Hard = false);

    public static byte[] Render(string soundId)
    {
        var samples = new List<short>();
        foreach (var note in Score(SoundCatalog.Normalise(soundId))) Append(samples, note);
        return Wav(samples);
    }

    private static Note[] Score(string id) => id switch
    {
        "bell" => new Note[] { new(880, 1100, Decay: 3.2, Overtone: 0.55) },
        "beep" => new Note[] { new(1000, 130, 90, 0, 0), new(1000, 130, 0, 0, 0) },
        "soft" => new Note[] { new(520, 220, Decay: 11, Overtone: 0.1) },
        "digital" => new Note[] { new(1400, 70, 55, 0, 0, true), new(1400, 70, 55, 0, 0, true), new(1900, 110, 0, 0, 0, true) },
        "alarm" => Enumerable.Range(0, 6).Select(i => new Note(i % 2 == 0 ? 880 : 660, 170, 0, 0, 0, true)).ToArray(),
        _ => new Note[] { new(1046.5, 170, Decay: 6), new(1318.5, 170, Decay: 6), new(1568, 520, Decay: 4.5) },
    };

    private static void Append(List<short> samples, Note note)
    {
        var count = SampleRate * note.Ms / 1000;
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            var wave = Math.Sin(2 * Math.PI * note.Hz * t) + note.Overtone * Math.Sin(2 * Math.PI * note.Hz * 2.76 * t);
            if (note.Hard) wave = Math.Sign(wave) * 0.6; // square-ish, for the electronic sounds
            // Quick attack and release avoid clicks; decay shapes bells and chimes.
            var attack = Math.Min(1, i / (SampleRate * 0.006));
            var release = Math.Min(1, (count - i) / (SampleRate * 0.012));
            var fade = note.Decay <= 0 ? 1 : Math.Exp(-note.Decay * t);
            samples.Add((short)(wave * attack * release * fade * 0.42 * short.MaxValue / (1 + note.Overtone)));
        }

        samples.AddRange(new short[SampleRate * note.GapMs / 1000]);
    }

    private static byte[] Wav(List<short> samples)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var dataBytes = samples.Count * 2;
            w.Write("RIFF"u8);
            w.Write(36 + dataBytes);
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);          // PCM
            w.Write((short)1);          // mono
            w.Write(SampleRate);
            w.Write(SampleRate * 2);    // byte rate
            w.Write((short)2);          // block align
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(dataBytes);
            foreach (var s in samples) w.Write(s);
        }

        return stream.ToArray();
    }
}
