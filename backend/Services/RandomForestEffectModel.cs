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

    public void Retrain(List<(double[] features, bool label)> trainingData)
    {
        if (trainingData.Count < 50) return;

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
}
