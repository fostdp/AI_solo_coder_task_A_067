using MathNet.Numerics.IntegralTransforms;
using MathNet.Numerics.Statistics;

namespace AlCellControl.Services;

public class AluminaConcentrationEstimator
{
    private readonly double _gamma = 0.5;
    private readonly double _bias;
    private readonly double[][] _supportVectors;
    private readonly double[] _alphas;

    public AluminaConcentrationEstimator()
    {
        _supportVectors = new double[10][];
        _alphas = new double[10];

        _supportVectors[0] = new double[] { 4.10, 0.015, 0.0005, 320.0, 2.0, 0.20, 0.06, 0.0037, 0.01, 0.005, 0.002 };
        _alphas[0] = 0.85;

        _supportVectors[1] = new double[] { 4.08, 0.020, 0.0003, 318.0, 2.5, 0.18, 0.07, 0.0049, 0.015, 0.008, 0.003 };
        _alphas[1] = 0.72;

        _supportVectors[2] = new double[] { 4.12, 0.025, 0.0010, 322.0, 3.0, 0.15, 0.08, 0.0061, 0.025, 0.012, 0.005 };
        _alphas[2] = 0.55;

        _supportVectors[3] = new double[] { 4.15, 0.035, 0.0015, 325.0, 3.5, 0.10, 0.11, 0.0084, 0.08, 0.04, 0.015 };
        _alphas[3] = -0.30;

        _supportVectors[4] = new double[] { 4.20, 0.050, 0.0025, 328.0, 4.0, 0.05, 0.15, 0.0119, 0.18, 0.09, 0.035 };
        _alphas[4] = -0.65;

        _supportVectors[5] = new double[] { 4.05, 0.010, -0.0002, 315.0, 1.5, 0.25, 0.04, 0.0025, 0.008, 0.003, 0.001 };
        _alphas[5] = 0.95;

        _supportVectors[6] = new double[] { 4.18, 0.045, 0.0020, 327.0, 3.8, 0.08, 0.14, 0.0108, 0.14, 0.07, 0.028 };
        _alphas[6] = -0.50;

        _supportVectors[7] = new double[] { 4.03, 0.012, -0.0001, 314.0, 1.8, 0.22, 0.05, 0.0030, 0.012, 0.006, 0.002 };
        _alphas[7] = 0.90;

        _supportVectors[8] = new double[] { 4.25, 0.060, 0.0030, 330.0, 4.5, 0.03, 0.18, 0.0141, 0.25, 0.12, 0.05 };
        _alphas[8] = -0.80;

        _supportVectors[9] = new double[] { 4.10, 0.018, 0.0008, 320.0, 2.2, 0.19, 0.065, 0.0044, 0.02, 0.01, 0.004 };
        _alphas[9] = 0.60;

        _bias = 2.80;
    }

    public double Estimate(double[] recentVoltages, double[] recentCurrents)
    {
        var features = ExtractFeatures(recentVoltages, recentCurrents);
        var normalized = NormalizeFeatures(features);

        double result = _bias;
        for (int i = 0; i < _supportVectors.Length; i++)
        {
            double kernel = RbfKernel(normalized, _supportVectors[i]);
            result += _alphas[i] * kernel;
        }

        return Math.Clamp(result, 0.5, 6.0);
    }

    private double[] ExtractFeatures(double[] voltages, double[] currents)
    {
        double vMean = voltages.Average();
        double vStd = voltages.StandardDeviation();
        double vSlope = ComputeSlope(voltages);
        double cMean = currents.Average();
        double cStd = currents.StandardDeviation();
        double correlation = ComputeCorrelation(voltages, currents);
        double vRange = voltages.Max() - voltages.Min();
        double vCv = vStd / vMean;

        var fftFeatures = ExtractFftFeatures(voltages);
        double spectralEnergy = fftFeatures.SpectralEnergy;
        double dominantFreqMagnitude = fftFeatures.DominantFreqMagnitude;
        double highFreqRatio = fftFeatures.HighFreqRatio;

        return new double[] { vMean, vStd, vSlope, cMean, cStd, correlation, vRange, vCv, spectralEnergy, dominantFreqMagnitude, highFreqRatio };
    }

    private (double SpectralEnergy, double DominantFreqMagnitude, double HighFreqRatio) ExtractFftFeatures(double[] voltages)
    {
        if (voltages.Length < 4)
            return (0, 0, 0);

        int fftSize = 1;
        while (fftSize < voltages.Length) fftSize *= 2;

        var padded = new double[fftSize];
        for (int i = 0; i < voltages.Length; i++)
        {
            padded[i] = voltages[i] - voltages.Average();
        }
        for (int i = voltages.Length; i < fftSize; i++)
        {
            padded[i] = 0;
        }

        var complexData = new System.Numerics.Complex[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (fftSize - 1)));
            complexData[i] = new System.Numerics.Complex(padded[i] * window, 0);
        }

        Fourier.Forward(complexData, FourierOptions.Matlab);

        int halfN = fftSize / 2;
        double totalEnergy = 0;
        double maxMag = 0;
        double highFreqEnergy = 0;
        int lowBandEnd = Math.Max(1, halfN / 4);

        for (int i = 1; i < halfN; i++)
        {
            double mag = complexData[i].Magnitude / fftSize;
            totalEnergy += mag * mag;
            if (mag > maxMag) maxMag = mag;
            if (i > lowBandEnd) highFreqEnergy += mag * mag;
        }

        double highFreqRatio = totalEnergy > 1e-12 ? highFreqEnergy / totalEnergy : 0;

        return (totalEnergy, maxMag, highFreqRatio);
    }

    private double[] NormalizeFeatures(double[] features)
    {
        double[] scale = { 1.0, 20.0, 200.0, 0.01, 0.5, 2.0, 10.0, 100.0, 5.0, 15.0, 3.0 };
        double[] offset = { 4.0, 0.0, 0.0, 300.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 };

        var result = new double[features.Length];
        for (int i = 0; i < features.Length; i++)
        {
            result[i] = (features[i] - offset[i]) * scale[i];
        }
        return result;
    }

    private double RbfKernel(double[] x, double[] y)
    {
        int len = Math.Min(x.Length, y.Length);
        double sumSq = 0.0;
        for (int i = 0; i < len; i++)
        {
            double diff = x[i] - y[i];
            sumSq += diff * diff;
        }
        return Math.Exp(-_gamma * sumSq);
    }

    private static double ComputeSlope(double[] values)
    {
        int n = values.Length;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += values[i];
            sumXY += i * values[i];
            sumX2 += (double)i * i;
        }
        double denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-12) return 0.0;
        return (n * sumXY - sumX * sumY) / denom;
    }

    private static double ComputeCorrelation(double[] x, double[] y)
    {
        int n = Math.Min(x.Length, y.Length);
        if (n < 2) return 0.0;

        double meanX = x.Take(n).Average();
        double meanY = y.Take(n).Average();

        double sumXY = 0, sumX2 = 0, sumY2 = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            sumXY += dx * dy;
            sumX2 += dx * dx;
            sumY2 += dy * dy;
        }

        double denom = Math.Sqrt(sumX2 * sumY2);
        if (denom < 1e-12) return 0.0;
        return sumXY / denom;
    }
}
