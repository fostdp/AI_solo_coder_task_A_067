using Microsoft.ML;
using Microsoft.ML.Data;

namespace AluminumCellControl.Services;

public class RandomForestEffectModel
{
    private readonly MLContext _mlContext;
    private ITransformer? _trainedModel;
    private DataViewSchema? _modelSchema;
    private bool _isModelLoaded;
    private readonly object _lock = new();

    private readonly Queue<(double[] features, bool actual, double predictedProb, DateTime timestamp)> _recentPredictions = new();
    private readonly object _monitorLock = new();
    private DateTime _lastRetrainTime = DateTime.UtcNow;
    private int _retrainCount;
    private double _lastAccuracy;
    private double _lastAuc;
    private const int MonitorWindowSize = 500;
    private const double AccuracyThreshold = 0.75;
    private const double DriftThreshold = 0.15;
    private static readonly TimeSpan MinRetrainInterval = TimeSpan.FromMinutes(30);

    private class EffectInput
    {
        public float VoltageNoise { get; set; }
        public float VoltageFluctuationFreq { get; set; }
        public float VoltageMean { get; set; }
        public float VoltageSpikeCount { get; set; }
        public float VoltageRange { get; set; }
        public float Concentration { get; set; }
    }

    private class EffectOutput : EffectInput
    {
        [ColumnName("PredictedLabel")]
        public bool WillOccurEffect { get; set; }

        [ColumnName("Score")]
        public float Probability { get; set; }
    }

    private class TrainingSample
    {
        public float VoltageNoise { get; set; }
        public float VoltageFluctuationFreq { get; set; }
        public float VoltageMean { get; set; }
        public float VoltageSpikeCount { get; set; }
        public float VoltageRange { get; set; }
        public float Concentration { get; set; }
        public bool Label { get; set; }
    }

    private class EvaluationSample
    {
        public float VoltageNoise { get; set; }
        public float VoltageFluctuationFreq { get; set; }
        public float VoltageMean { get; set; }
        public float VoltageSpikeCount { get; set; }
        public float VoltageRange { get; set; }
        public float Concentration { get; set; }
        public bool Label { get; set; }
        public float PredictedProbability { get; set; }
    }

    public RandomForestEffectModel()
    {
        _mlContext = new MLContext(seed: 42);
        TrainInitialModel();
    }

    private void TrainInitialModel()
    {
        var trainingData = GenerateSyntheticTrainingData();
        var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

        var pipeline = _mlContext.Transforms.Concatenate("Features",
                nameof(EffectInput.VoltageNoise),
                nameof(EffectInput.VoltageFluctuationFreq),
                nameof(EffectInput.VoltageMean),
                nameof(EffectInput.VoltageSpikeCount),
                nameof(EffectInput.VoltageRange),
                nameof(EffectInput.Concentration))
            .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(_mlContext.BinaryClassification.Trainers.FastTree(
                labelColumnName: "Label",
                featureColumnName: "Features",
                numberOfLeaves: 20,
                numberOfTrees: 100,
                minimumExampleCountPerLeaf: 5,
                learningRate: 0.2));

        _trainedModel = pipeline.Fit(dataView);
        _modelSchema = dataView.Schema;
        _isModelLoaded = true;
    }

    private List<TrainingSample> GenerateSyntheticTrainingData()
    {
        var samples = new List<TrainingSample>();
        var rng = new Random(42);

        for (int i = 0; i < 3000; i++)
        {
            var willEffect = rng.NextDouble() < 0.15;
            float noise, freq, mean, spikeCount, range, concentration;

            if (willEffect)
            {
                noise = (float)(0.08 + rng.NextDouble() * 0.12);
                freq = (float)(2.0 + rng.NextDouble() * 3.0);
                mean = (float)(4.5 + rng.NextDouble() * 1.5);
                spikeCount = (float)(5 + rng.NextDouble() * 15);
                range = (float)(0.5 + rng.NextDouble() * 1.5);
                concentration = (float)(0.5 + rng.NextDouble() * 1.5);
            }
            else
            {
                noise = (float)(rng.NextDouble() * 0.04);
                freq = (float)(rng.NextDouble() * 1.0);
                mean = (float)(3.8 + rng.NextDouble() * 0.5);
                spikeCount = (float)(rng.NextDouble() * 3);
                range = (float)(rng.NextDouble() * 0.3);
                concentration = (float)(2.0 + rng.NextDouble() * 3.0);
            }

            samples.Add(new TrainingSample
            {
                VoltageNoise = noise,
                VoltageFluctuationFreq = freq,
                VoltageMean = mean,
                VoltageSpikeCount = spikeCount,
                VoltageRange = range,
                Concentration = concentration,
                Label = willEffect
            });
        }

        return samples;
    }

    public double PredictAnodeEffect(double voltageNoise, double voltageFluctuationFreq,
        double voltageMean, double voltageSpikeCount, double voltageRange, double concentration)
    {
        if (!_isModelLoaded || _trainedModel == null) return 0;

        var input = new EffectInput
        {
            VoltageNoise = (float)voltageNoise,
            VoltageFluctuationFreq = (float)voltageFluctuationFreq,
            VoltageMean = (float)voltageMean,
            VoltageSpikeCount = (float)voltageSpikeCount,
            VoltageRange = (float)voltageRange,
            Concentration = (float)concentration
        };

        lock (_lock)
        {
            var predictionEngine = _mlContext.Model.CreatePredictionEngine<EffectInput, EffectOutput>(_trainedModel);
            var result = predictionEngine.Predict(input);
            return Math.Clamp(result.Probability, 0.0, 1.0);
        }
    }

    public void RecordPrediction(double[] features, bool actual, double predictedProb)
    {
        lock (_monitorLock)
        {
            _recentPredictions.Enqueue((features, actual, predictedProb, DateTime.UtcNow));
            while (_recentPredictions.Count > MonitorWindowSize)
                _recentPredictions.Dequeue();
        }
    }

    public (double Accuracy, double Auc, bool NeedsRetrain, string Reason) EvaluateModel()
    {
        lock (_monitorLock)
        {
            if (_recentPredictions.Count < 50)
                return (_lastAccuracy, _lastAuc, false, "样本不足");

            var predictions = _recentPredictions.ToList();
            var correct = 0;
            var tp = 0; var fp = 0; var tn = 0; var fn = 0;

            foreach (var p in predictions)
            {
                var predicted = p.predictedProb >= 0.5;
                if (predicted == p.actual) correct++;
                if (predicted && p.actual) tp++;
                else if (predicted && !p.actual) fp++;
                else if (!predicted && !p.actual) tn++;
                else fn++;
            }

            var accuracy = (double)correct / predictions.Count;
            var auc = CalculateAuc(predictions);
            _lastAccuracy = accuracy;
            _lastAuc = auc;

            var needsRetrain = false;
            var reason = "";

            if (accuracy < AccuracyThreshold)
            {
                needsRetrain = true;
                reason = $"准确率{accuracy:P1}低于阈值{AccuracyThreshold:P0}";
            }
            else if (DetectConceptDrift(predictions))
            {
                needsRetrain = true;
                reason = "检测到概念漂移：近期预测误差显著高于历史";
            }
            else if (DateTime.UtcNow - _lastRetrainTime > TimeSpan.FromHours(6) && predictions.Count >= 300)
            {
                needsRetrain = true;
                reason = "距上次重训练已超过6小时，定期重训练";
            }

            return (accuracy, auc, needsRetrain, reason);
        }
    }

    private bool DetectConceptDrift(List<(double[] features, bool actual, double predictedProb, DateTime timestamp)> predictions)
    {
        if (predictions.Count < 100) return false;

        var halfCount = predictions.Count / 2;
        var older = predictions.Take(halfCount).ToList();
        var newer = predictions.Skip(halfCount).ToList();

        var olderError = older.Count(p => (p.predictedProb >= 0.5) != p.actual) / (double)older.Count;
        var newerError = newer.Count(p => (p.predictedProb >= 0.5) != p.actual) / (double)newer.Count;

        return newerError - olderError > DriftThreshold;
    }

    private static double CalculateAuc(List<(double[] features, bool actual, double predictedProb, DateTime timestamp)> predictions)
    {
        var positives = predictions.Where(p => p.actual).OrderBy(p => p.predictedProb).ToList();
        var negatives = predictions.Where(p => !p.actual).OrderBy(p => p.predictedProb).ToList();

        if (positives.Count == 0 || negatives.Count == 0) return 0.5;

        long concordant = 0, discordant = 0;
        foreach (var pos in positives)
        {
            foreach (var neg in negatives)
            {
                if (pos.predictedProb > neg.predictedProb) concordant++;
                else if (pos.predictedProb < neg.predictedProb) discordant++;
            }
        }

        var total = concordant + discordant;
        return total > 0 ? (double)concordant / total : 0.5;
    }

    public bool TryAutoRetrain(List<(double[] features, bool label)> recentData)
    {
        lock (_monitorLock)
        {
            if (DateTime.UtcNow - _lastRetrainTime < MinRetrainInterval)
                return false;

            if (recentData.Count < 100) return false;
        }

        var positiveRatio = recentData.Count(d => d.label) / (double)recentData.Count;
        if (positiveRatio < 0.05 || positiveRatio > 0.95) return false;

        DoRetrain(recentData);

        lock (_monitorLock)
        {
            _lastRetrainTime = DateTime.UtcNow;
            _retrainCount++;
            _recentPredictions.Clear();
        }

        return true;
    }

    public void Retrain(List<(double[] features, bool label)> trainingData)
    {
        if (trainingData.Count < 50) return;
        DoRetrain(trainingData);

        lock (_monitorLock)
        {
            _lastRetrainTime = DateTime.UtcNow;
            _retrainCount++;
        }
    }

    private void DoRetrain(List<(double[] features, bool label)> trainingData)
    {
        var samples = trainingData.Select(d => new TrainingSample
        {
            VoltageNoise = (float)d.features[0],
            VoltageFluctuationFreq = (float)d.features[1],
            VoltageMean = (float)d.features[2],
            VoltageSpikeCount = (float)d.features[3],
            VoltageRange = (float)d.features[4],
            Concentration = (float)d.features[5],
            Label = d.label
        }).ToList();

        var dataView = _mlContext.Data.LoadFromEnumerable(samples);

        var pipeline = _mlContext.Transforms.Concatenate("Features",
                nameof(TrainingSample.VoltageNoise),
                nameof(TrainingSample.VoltageFluctuationFreq),
                nameof(TrainingSample.VoltageMean),
                nameof(TrainingSample.VoltageSpikeCount),
                nameof(TrainingSample.VoltageRange),
                nameof(TrainingSample.Concentration))
            .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(_mlContext.BinaryClassification.Trainers.FastTree(
                labelColumnName: "Label",
                featureColumnName: "Features",
                numberOfLeaves: 20,
                numberOfTrees: 100,
                minimumExampleCountPerLeaf: 5,
                learningRate: 0.2));

        lock (_lock)
        {
            _trainedModel = pipeline.Fit(dataView);
        }
    }

    public ModelHealthReport GetHealthReport()
    {
        lock (_monitorLock)
        {
            return new ModelHealthReport
            {
                LastRetrainTime = _lastRetrainTime,
                RetrainCount = _retrainCount,
                CurrentAccuracy = _lastAccuracy,
                CurrentAuc = _lastAuc,
                MonitoredSampleCount = _recentPredictions.Count,
                TimeSinceLastRetrain = DateTime.UtcNow - _lastRetrainTime
            };
        }
    }
}

public class ModelHealthReport
{
    public DateTime LastRetrainTime { get; set; }
    public int RetrainCount { get; set; }
    public double CurrentAccuracy { get; set; }
    public double CurrentAuc { get; set; }
    public int MonitoredSampleCount { get; set; }
    public TimeSpan TimeSinceLastRetrain { get; set; }
}
