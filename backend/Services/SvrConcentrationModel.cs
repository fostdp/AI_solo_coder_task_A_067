using Microsoft.ML;
using Microsoft.ML.Data;

namespace AluminumCellControl.Services;

public class SvrConcentrationModel
{
    private readonly MLContext _mlContext;
    private ITransformer? _trainedModel;
    private DataViewSchema? _modelSchema;
    private bool _isModelLoaded;
    private readonly object _lock = new();

    private class ConcentrationInput
    {
        public float VoltageMean { get; set; }
        public float VoltageStd { get; set; }
        public float VoltageNoise { get; set; }
        public float CurrentDistMean { get; set; }
        public float CurrentDistStd { get; set; }
        public float VoltageSlope { get; set; }
    }

    private class ConcentrationOutput : ConcentrationInput
    {
        [ColumnName("Score")]
        public float PredictedConcentration { get; set; }
    }

    public SvrConcentrationModel()
    {
        _mlContext = new MLContext(seed: 42);
        TrainInitialModel();
    }

    private void TrainInitialModel()
    {
        var trainingData = GenerateSyntheticTrainingData();
        var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

        var pipeline = _mlContext.Transforms.Concatenate("Features",
                nameof(ConcentrationInput.VoltageMean),
                nameof(ConcentrationInput.VoltageStd),
                nameof(ConcentrationInput.VoltageNoise),
                nameof(ConcentrationInput.CurrentDistMean),
                nameof(ConcentrationInput.CurrentDistStd),
                nameof(ConcentrationInput.VoltageSlope))
            .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(_mlContext.Regression.Trainers.Sdca(
                labelColumnName: "Label",
                maximumNumberOfIterations: 100,
                featureColumnName: "Features"));

        _trainedModel = pipeline.Fit(dataView);
        _modelSchema = dataView.Schema;
        _isModelLoaded = true;
    }

    private class TrainingSample
    {
        public float VoltageMean { get; set; }
        public float VoltageStd { get; set; }
        public float VoltageNoise { get; set; }
        public float CurrentDistMean { get; set; }
        public float CurrentDistStd { get; set; }
        public float VoltageSlope { get; set; }
        public float Label { get; set; }
    }

    private List<TrainingSample> GenerateSyntheticTrainingData()
    {
        var samples = new List<TrainingSample>();
        var rng = new Random(42);

        for (int i = 0; i < 2000; i++)
        {
            var concentration = (float)(rng.NextDouble() * 5.0 + 0.5);
            var voltageBase = 4.0f + (3.5f - concentration) * 0.15f;
            var voltageMean = voltageBase + (float)(rng.NextDouble() - 0.5) * 0.2;
            var voltageStd = (float)(0.01 + rng.NextDouble() * 0.05 + Math.Max(0, 2.5 - concentration) * 0.03);
            var voltageNoise = (float)(rng.NextDouble() * 0.02 + Math.Max(0, 2.0 - concentration) * 0.04);
            var currentDistMean = (float)(300 + rng.NextDouble() * 40 - 20);
            var currentDistStd = (float)(5 + rng.NextDouble() * 10 + Math.Max(0, 2.0 - concentration) * 15);
            var voltageSlope = (float)((rng.NextDouble() - 0.5) * 0.001 + Math.Max(0, 2.0 - concentration) * -0.0005);

            samples.Add(new TrainingSample
            {
                VoltageMean = voltageMean,
                VoltageStd = voltageStd,
                VoltageNoise = voltageNoise,
                CurrentDistMean = currentDistMean,
                CurrentDistStd = currentDistStd,
                VoltageSlope = voltageSlope,
                Label = concentration
            });
        }

        return samples;
    }

    public double PredictConcentration(List<double> recentVoltages, List<double> recentCurrents,
        double voltageNoise, double voltageSlope)
    {
        if (!_isModelLoaded || _trainedModel == null) return 3.0;

        var voltageMean = recentVoltages.Count > 0 ? (float)recentVoltages.Average() : 4.1f;
        var voltageStd = recentVoltages.Count > 1 ? (float)CalculateStd(recentVoltages) : 0.02f;
        var currentDistMean = recentCurrents.Count > 0 ? (float)recentCurrents.Average() : 300f;
        var currentDistStd = recentCurrents.Count > 1 ? (float)CalculateStd(recentCurrents) : 8f;

        var input = new ConcentrationInput
        {
            VoltageMean = voltageMean,
            VoltageStd = voltageStd,
            VoltageNoise = (float)voltageNoise,
            CurrentDistMean = currentDistMean,
            CurrentDistStd = currentDistStd,
            VoltageSlope = (float)voltageSlope
        };

        lock (_lock)
        {
            var predictionEngine = _mlContext.Model.CreatePredictionEngine<ConcentrationInput, ConcentrationOutput>(_trainedModel);
            var result = predictionEngine.Predict(input);
            return Math.Clamp(result.PredictedConcentration, 0.5, 8.0);
        }
    }

    private static double CalculateStd(List<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        var sumSquares = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSquares / (values.Count - 1));
    }

    public void Retrain(List<(double[] features, double label)> trainingData)
    {
        if (trainingData.Count < 50) return;

        var samples = trainingData.Select(d => new TrainingSample
        {
            VoltageMean = (float)d.features[0],
            VoltageStd = (float)d.features[1],
            VoltageNoise = (float)d.features[2],
            CurrentDistMean = (float)d.features[3],
            CurrentDistStd = (float)d.features[4],
            VoltageSlope = (float)d.features[5],
            Label = (float)d.label
        }).ToList();

        var dataView = _mlContext.Data.LoadFromEnumerable(samples);

        var pipeline = _mlContext.Transforms.Concatenate("Features",
                nameof(TrainingSample.VoltageMean),
                nameof(TrainingSample.VoltageStd),
                nameof(TrainingSample.VoltageNoise),
                nameof(TrainingSample.CurrentDistMean),
                nameof(TrainingSample.CurrentDistStd),
                nameof(TrainingSample.VoltageSlope))
            .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(_mlContext.Regression.Trainers.Sdca(
                labelColumnName: "Label",
                maximumNumberOfIterations: 100,
                featureColumnName: "Features"));

        lock (_lock)
        {
            _trainedModel = pipeline.Fit(dataView);
        }
    }
}
