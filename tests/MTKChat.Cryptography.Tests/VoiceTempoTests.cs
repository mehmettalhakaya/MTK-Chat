using MTKChat.Desktop;
using NAudio.Wave;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class VoiceTempoTests
{
    [Theory]
    [InlineData(1.5)]
    [InlineData(2)]
    public void FasterVoiceHasShorterDurationAndPreservesPitch(double speed)
    {
        var source = new Tone();
        var tempo = new TempoSampleProvider(source, speed);
        var output = new List<float>(); var buffer = new float[1024]; int count;
        while ((count = tempo.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.AddRange(buffer.Take(count));
            Assert.True(output.Count <= 70000, "Tempo must reach end of input.");
        }
        Assert.InRange(output.Count, 64000 / speed - 1600, 64000 / speed + 1600);
        var core = output.Skip(1600).Take(output.Count - 3200).ToArray();
        var crossings = Enumerable.Range(1, core.Length - 1).Count(i => core[i - 1] <= 0 && core[i] > 0);
        Assert.InRange(crossings * 16000d / core.Length, 425, 455);
    }
    private sealed class Tone : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, 64000 - _position);
            for (var i = 0; i < read; i++) buffer[offset + i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * _position++ / 16000));
            return read;
        }
    }
}
