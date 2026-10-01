namespace AudioOptimizer.Art.Tests;

using System.Numerics;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;

static class SyntheticSignals
{
    public const int SampleRate = 48000;

    public static double[] MinPhase(params double[] zerosInsideUnitCircle)
    {
        double[] impulse = [1];
        foreach (double zero in zerosInsideUnitCircle)
            impulse = Direct(impulse, [1, -zero]);
        return impulse;
    }

    public static double[] FirstOrderAllpass(double pole, int length)
    {
        var impulse = new double[length];
        impulse[0] = -pole;
        double gain = 1 - pole * pole;
        double power = 1;
        for (int n = 1; n < length; n++)
        {
            impulse[n] = gain * power;
            power *= pole;
        }
        return impulse;
    }

    public static double[] Direct(double[] a, double[] b)
    {
        var y = new double[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                y[i + j] += a[i] * b[j];
        return y;
    }

    public static ImpulseResponse Ir(double[] samples, int sampleRate = SampleRate)
        => new(samples, sampleRate);

    public static double[] AllpassPhase(double pole, double[] frequencyHz, int sampleRate)
    {
        var wrapped = new double[frequencyHz.Length];
        for (int i = 0; i < frequencyHz.Length; i++)
        {
            double omega = 2.0 * Math.PI * frequencyHz[i] / sampleRate;
            Complex inverseZ = Complex.Exp(new Complex(0, -omega));
            Complex value = (inverseZ - pole) / (1 - pole * inverseZ);
            wrapped[i] = Math.Atan2(value.Imaginary, value.Real);
        }
        return ComplexMath.Unwrap(wrapped);
    }

    public static double AlignedRms(double[] left, double[] right, double[] frequencyHz, double lowHz, double highHz)
    {
        double offset = 0;
        int count = 0;
        for (int i = 0; i < frequencyHz.Length; i++)
        {
            if (frequencyHz[i] < lowHz || frequencyHz[i] > highHz) continue;
            offset += left[i] - right[i];
            count++;
        }

        if (count == 0) return double.PositiveInfinity;
        offset /= count;
        double energy = 0;
        for (int i = 0; i < frequencyHz.Length; i++)
        {
            if (frequencyHz[i] < lowHz || frequencyHz[i] > highHz) continue;
            double delta = left[i] - right[i] - offset;
            energy += delta * delta;
        }
        return Math.Sqrt(energy / count);
    }

    public static int ArgMaxAbs(double[] samples)
    {
        int peak = 0;
        double best = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double abs = Math.Abs(samples[i]);
            if (abs > best)
            {
                best = abs;
                peak = i;
            }
        }
        return peak;
    }
}
