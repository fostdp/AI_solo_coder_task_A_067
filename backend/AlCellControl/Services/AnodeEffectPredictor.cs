using System.Collections.Concurrent;

namespace AlCellControl.Services;

public class AnodeEffectPredictor
{
    private const double NominalTemperature = 960.0;
    private const double HighVoltageThreshold = 4.5;
    private const int DriftCheckWindow = 200;
    private const double DriftThreshold = 0.15;
    private const int RetrainMinSamples = 100;

    private readonly ConcurrentDictionary<int, PredictionRecord> _predictionHistory = new();
    private readonly ConcurrentQueue<TrainingSample> _trainingBuffer = new();
    private int _totalPredictions = 0;
    private int _correctPredictions = 0;
    private DateTime _lastRetrainTime = DateTime.UtcNow;
    private readonly object _retrainLock = new();
    private volatile TreeWeights _weights = new();

    public AnodeEffectPredictor()
    {
        _weights = new TreeWeights(1.0, 1.0, 1.0, 1.0, 1.0);
    }

    public double Predict(double[] recentVoltages, double recentTemperature)
    {
        var features = ExtractFeatures(recentVoltages, recentTemperature);

        double p1 = Tree1(features);
        double p2 = Tree2(features);
        double p3 = Tree3(features);
        double p4 = Tree4(features);
        double p5 = Tree5(features);

        var w = _weights;
        double weightedSum = w.W1 * p1 + w.W2 * p2 + w.W3 * p3 + w.W4 * p4 + w.W5 * p5;
        double avg = weightedSum / (w.W1 + w.W2 + w.W3 + w.W4 + w.W5);
        return Math.Clamp(avg, 0.0, 1.0);
    }

    public void RecordPrediction(int cellId, double predictedProbability)
    {
        _predictionHistory.AddOrUpdate(cellId,
            _ => new PredictionRecord(predictedProbability, DateTime.UtcNow),
            (_, existing) =>
            {
                existing.Predictions.Enqueue((predictedProbability, DateTime.UtcNow));
                while (existing.Predictions.Count > DriftCheckWindow)
                    existing.Predictions.TryDequeue(out _);
                return existing;
            });

        Interlocked.Increment(ref _totalPredictions);

        bool isHighRisk = predictedProbability > 0.8;
        _trainingBuffer.Enqueue(new TrainingSample(cellId, predictedProbability, isHighRisk, DateTime.UtcNow));

        CheckDriftAndRetrain();
    }

    public void RecordActualOutcome(int cellId, bool anodeEffectOccurred)
    {
        if (_predictionHistory.TryGetValue(cellId, out var record))
        {
            if (record.Predictions.TryPeek(out var latest))
            {
                bool predictedHigh = latest.Probability > 0.8;
                if (predictedHigh == anodeEffectOccurred)
                {
                    Interlocked.Increment(ref _correctPredictions);
                }
            }
        }
    }

    public ModelPerformanceMetrics GetPerformanceMetrics()
    {
        int total = Interlocked.CompareExchange(ref _totalPredictions, 0, 0);
        int correct = Interlocked.CompareExchange(ref _correctPredictions, 0, 0);
        double accuracy = total > 0 ? (double)correct / total : 0;

        return new ModelPerformanceMetrics(
            total,
            correct,
            accuracy,
            _lastRetrainTime,
            _trainingBuffer.Count,
            _weights
        );
    }

    private void CheckDriftAndRetrain()
    {
        int total = Interlocked.CompareExchange(ref _totalPredictions, 0, 0);
        if (total < RetrainMinSamples) return;
        if (total % 50 != 0) return;

        int correct = Interlocked.CompareExchange(ref _correctPredictions, 0, 0);
        double accuracy = (double)correct / total;

        if (accuracy < (1.0 - DriftThreshold) || (DateTime.UtcNow - _lastRetrainTime).TotalHours > 24)
        {
            lock (_retrainLock)
            {
                if ((DateTime.UtcNow - _lastRetrainTime).TotalMinutes < 10) return;

                RetrainWeights();
                _lastRetrainTime = DateTime.UtcNow;
                Interlocked.Exchange(ref _totalPredictions, 0);
                Interlocked.Exchange(ref _correctPredictions, 0);
            }
        }
    }

    private void RetrainWeights()
    {
        var samples = new List<TrainingSample>();
        while (_trainingBuffer.TryDequeue(out var sample))
        {
            samples.Add(sample);
        }

        if (samples.Count < RetrainMinSamples) return;

        int highRiskCount = samples.Count(s => s.IsHighRisk);
        int lowRiskCount = samples.Count - highRiskCount;

        if (highRiskCount < 5 || lowRiskCount < 5) return;

        double highRiskRatio = (double)highRiskCount / samples.Count;

        double w1 = highRiskRatio < 0.1 ? 1.3 : 1.0;
        double w2 = highRiskRatio > 0.3 ? 1.2 : 1.0;
        double w3 = 1.0;
        double w4 = lowRiskCount < highRiskCount * 2 ? 1.25 : 1.0;
        double w5 = 1.0;

        double recentHighRisk = samples.TakeLast(50).Count(s => s.IsHighRisk) / 50.0;
        if (recentHighRisk > highRiskRatio * 1.5)
        {
            w1 *= 1.2;
            w2 *= 1.2;
            w5 *= 1.15;
        }

        _weights = new TreeWeights(w1, w2, w3, w4, w5);
    }

    private static double[] ExtractFeatures(double[] voltages, double temperature)
    {
        double vMean = voltages.Average();

        double sumVar = 0;
        for (int i = 0; i < voltages.Length; i++)
        {
            double diff = voltages[i] - vMean;
            sumVar += diff * diff;
        }
        double vStd = Math.Sqrt(sumVar / voltages.Length);

        double vMax = voltages.Max();

        double noisePower = 0;
        for (int i = 1; i < voltages.Length; i++)
        {
            double diff = voltages[i] - voltages[i - 1];
            noisePower += diff * diff;
        }
        noisePower /= Math.Max(voltages.Length - 1, 1);

        int n = voltages.Length;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += voltages[i];
            sumXY += i * voltages[i];
            sumX2 += (double)i * i;
        }
        double denom = n * sumX2 - sumX * sumX;
        double vSlope = Math.Abs(denom) < 1e-12 ? 0.0 : (n * sumXY - sumX * sumY) / denom;

        double tempDeviation = temperature - NominalTemperature;

        int spikeCount = 0;
        for (int i = 0; i < voltages.Length; i++)
        {
            if (voltages[i] > HighVoltageThreshold) spikeCount++;
        }

        double vRateOfChange = 0;
        if (voltages.Length >= 10)
        {
            double recent5 = voltages.Skip(voltages.Length - 5).Take(5).Average();
            double prev5 = voltages.Skip(voltages.Length - 10).Take(5).Average();
            vRateOfChange = recent5 - prev5;
        }

        return new double[] { vMean, vStd, vMax, noisePower, vSlope, tempDeviation, spikeCount, vRateOfChange };
    }

    private static double Tree1(double[] f)
    {
        double vMean = f[0], vStd = f[1], vMax = f[2], noisePower = f[3];
        double vSlope = f[4], tempDeviation = f[5], spikeCount = f[6], vRateOfChange = f[7];

        if (vMax > 4.3)
        {
            if (spikeCount > 10)
            {
                if (vStd > 0.15) return 0.95;
                return 0.82;
            }
            if (spikeCount > 3)
            {
                if (vSlope > 0.002) return 0.78;
                return 0.60;
            }
            if (vRateOfChange > 0.05) return 0.55;
            return 0.30;
        }
        if (vMean > 4.15)
        {
            if (vSlope > 0.001)
            {
                if (tempDeviation < -3) return 0.65;
                return 0.40;
            }
            if (noisePower > 0.005) return 0.35;
            return 0.15;
        }
        if (tempDeviation < -5)
        {
            if (vSlope > 0.0005) return 0.45;
            return 0.20;
        }
        if (vStd > 0.08)
        {
            if (vRateOfChange > 0.02) return 0.35;
            return 0.18;
        }
        return 0.05;
    }

    private static double Tree2(double[] f)
    {
        double vMean = f[0], vStd = f[1], vMax = f[2], noisePower = f[3];
        double vSlope = f[4], tempDeviation = f[5], spikeCount = f[6], vRateOfChange = f[7];

        if (spikeCount > 5)
        {
            if (vMax > 4.5)
            {
                if (vStd > 0.12) return 0.92;
                return 0.80;
            }
            if (vSlope > 0.0015) return 0.75;
            return 0.55;
        }
        if (vSlope > 0.002)
        {
            if (vMean > 4.1)
            {
                if (noisePower > 0.003) return 0.70;
                return 0.50;
            }
            if (tempDeviation < -4) return 0.55;
            return 0.35;
        }
        if (noisePower > 0.008)
        {
            if (vRateOfChange > 0.03) return 0.50;
            return 0.28;
        }
        if (tempDeviation < -5)
        {
            if (vStd > 0.06) return 0.40;
            return 0.22;
        }
        if (vMean > 4.2)
        {
            if (vRateOfChange > 0.02) return 0.30;
            return 0.12;
        }
        return 0.03;
    }

    private static double Tree3(double[] f)
    {
        double vMean = f[0], vStd = f[1], vMax = f[2], noisePower = f[3];
        double vSlope = f[4], tempDeviation = f[5], spikeCount = f[6], vRateOfChange = f[7];

        if (vStd > 0.1)
        {
            if (spikeCount > 8)
            {
                return 0.90;
            }
            if (vMax > 4.35)
            {
                if (vSlope > 0.001) return 0.75;
                return 0.58;
            }
            if (noisePower > 0.006) return 0.52;
            return 0.30;
        }
        if (vRateOfChange > 0.04)
        {
            if (vMean > 4.15)
            {
                if (spikeCount > 2) return 0.68;
                return 0.45;
            }
            return 0.25;
        }
        if (tempDeviation < -3)
        {
            if (vSlope > 0.0008)
            {
                if (noisePower > 0.003) return 0.55;
                return 0.35;
            }
            return 0.15;
        }
        if (vMean > 4.25)
        {
            if (vSlope > 0.001) return 0.35;
            return 0.18;
        }
        return 0.04;
    }

    private static double Tree4(double[] f)
    {
        double vMean = f[0], vStd = f[1], vMax = f[2], noisePower = f[3];
        double vSlope = f[4], tempDeviation = f[5], spikeCount = f[6], vRateOfChange = f[7];

        if (tempDeviation < -8)
        {
            if (spikeCount > 3) return 0.85;
            if (vSlope > 0.001) return 0.65;
            if (vStd > 0.07) return 0.50;
            return 0.30;
        }
        if (noisePower > 0.01)
        {
            if (vMax > 4.4)
            {
                if (spikeCount > 5) return 0.88;
                return 0.65;
            }
            if (vRateOfChange > 0.03) return 0.55;
            return 0.35;
        }
        if (vSlope > 0.003)
        {
            if (vMean > 4.2) return 0.60;
            if (spikeCount > 1) return 0.50;
            return 0.30;
        }
        if (vMax > 4.3)
        {
            if (spikeCount > 2) return 0.45;
            if (vRateOfChange > 0.02) return 0.32;
            return 0.18;
        }
        if (vMean > 4.2 && vStd > 0.05) return 0.20;
        return 0.05;
    }

    private static double Tree5(double[] f)
    {
        double vMean = f[0], vStd = f[1], vMax = f[2], noisePower = f[3];
        double vSlope = f[4], tempDeviation = f[5], spikeCount = f[6], vRateOfChange = f[7];

        if (vRateOfChange > 0.05)
        {
            if (vMax > 4.3)
            {
                if (spikeCount > 5) return 0.93;
                return 0.72;
            }
            if (vSlope > 0.002) return 0.65;
            return 0.40;
        }
        if (vMax > 4.5)
        {
            if (spikeCount > 3) return 0.85;
            if (vStd > 0.1) return 0.70;
            return 0.45;
        }
        if (vStd > 0.08)
        {
            if (noisePower > 0.005)
            {
                if (vSlope > 0.001) return 0.60;
                return 0.38;
            }
            if (tempDeviation < -5) return 0.42;
            return 0.22;
        }
        if (tempDeviation < -5)
        {
            if (vSlope > 0.001) return 0.40;
            if (vMean > 4.15) return 0.25;
            return 0.15;
        }
        if (vSlope > 0.001 && vMean > 4.15) return 0.22;
        return 0.04;
    }
}

public record TreeWeights(double W1, double W2, double W3, double W4, double W5);

public record ModelPerformanceMetrics(
    int TotalPredictions,
    int CorrectPredictions,
    double Accuracy,
    DateTime LastRetrainTime,
    int TrainingBufferSize,
    TreeWeights CurrentWeights
);

public class PredictionRecord
{
    public ConcurrentQueue<(double Probability, DateTime Timestamp)> Predictions { get; } = new();

    public PredictionRecord(double probability, DateTime timestamp)
    {
        Predictions.Enqueue((probability, timestamp));
    }
}

public record TrainingSample(int CellId, double PredictedProbability, bool IsHighRisk, DateTime Timestamp);
