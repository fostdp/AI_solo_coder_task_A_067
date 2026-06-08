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
        public float FftDominantFreq { get; set; }
        public float FftSpectralEnergy { get; set; }
        public float FftHighFreqRatio { get; set; }
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

    private static string[] FeatureNames => new[]
    {
        nameof(ConcentrationInput.VoltageMean),
        nameof(ConcentrationInput.VoltageStd),
        nameof(ConcentrationInput.VoltageNoise),
        nameof(ConcentrationInput.CurrentDistMean),
        nameof(ConcentrationInput.CurrentDistStd),
        nameof(ConcentrationInput.VoltageSlope),
        nameof(ConcentrationInput.FftDominantFreq),
        nameof(ConcentrationInput.FftSpectralEnergy),
        nameof(ConcentrationInput.FftHighFreqRatio)
    };

    private void TrainInitialModel()
    {
        var trainingData = GenerateSyntheticTrainingData();
        var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

        var pipeline = _mlContext.Transforms.Concatenate("Features", FeatureNames)
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
        public float FftDominantFreq { get; set; }
        public float FftSpectralEnergy { get; set; }
        public float FftHighFreqRatio { get; set; }
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

            var fftDominantFreq = (float)(0.5 + rng.NextDouble() * 1.0 + Math.Max(0, 2.0 - concentration) * 2.5);
            var fftSpectralEnergy = (float)(0.01 + rng.NextDouble() * 0.05 + Math.Max(0, 2.5 - concentration) * 0.08);
            var fftHighFreqRatio = (float)(0.05 + rng.NextDouble() * 0.1 + Math.Max(0, 2.0 - concentration) * 0.3);

            samples.Add(new TrainingSample
            {
                VoltageMean = voltageMean,
                VoltageStd = voltageStd,
                VoltageNoise = voltageNoise,
                CurrentDistMean = currentDistMean,
                CurrentDistStd = currentDistStd,
                VoltageSlope = voltageSlope,
                FftDominantFreq = fftDominantFreq,
                FftSpectralEnergy = fftSpectralEnergy,
                FftHighFreqRatio = fftHighFreqRatio,
                Label = concentration
            });
        }

        return samples;
    }

    public double PredictConcentration(List<double> recentVoltages, List<double> recentCurrents,
        double voltageNoise, double voltageSlope,
        double fftDominantFreq, double fftSpectralEnergy, double fftHighFreqRatio)
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
            VoltageSlope = (float)voltageSlope,
            FftDominantFreq = (float)fftDominantFreq,
            FftSpectralEnergy = (float)fftSpectralEnergy,
            FftHighFreqRatio = (float)fftHighFreqRatio
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
            FftDominantFreq = (float)d.features[6],
            FftSpectralEnergy = (float)d.features[7],
            FftHighFreqRatio = (float)d.features[8],
            Label = (float)d.label
        }).ToList();

        var dataView = _mlContext.Data.LoadFromEnumerable(samples);

        var pipeline = _mlContext.Transforms.Concatenate("Features", FeatureNames)
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

    public static double[] ComputeFft(double[] signal)
    {
        var n = signal.Length;
        if (n == 0) return Array.Empty<double>();

        var m = (int)Math.Ceiling(Math.Log2(n));
        var paddedLen = 1 << m;
        var real = new double[paddedLen];
        var imag = new double[paddedLen];
        for (int i = 0; i < n; i++) real[i] = signal[i];

        for (int i = 1, j = 0; i < paddedLen; i++)
        {
            var bit = paddedLen >> 1;
            while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (int len = 2; len <= paddedLen; len <<= 1)
        {
            var halfLen = len >> 1;
            var angle = -2.0 * Math.PI / len;
            var wReal = Math.Cos(angle);
            var wImag = Math.Sin(angle);

            for (int i = 0; i < paddedLen; i += len)
            {
                var curReal = 1.0;
                var curImag = 0.0;
                for (int j = 0; j < halfLen; j++)
                {
                    var tReal = curReal * real[i + j + halfLen] - curImag * imag[i + j + halfLen];
                    var tImag = curReal * imag[i + j + halfLen] + curImag * real[i + j + halfLen];
                    real[i + j + halfLen] = real[i + j] - tReal;
                    imag[i + j + halfLen] = imag[i + j] - tImag;
                    real[i + j] += tReal;
                    imag[i + j] += tImag;
                    var newCurReal = curReal * wReal - curImag * wImag;
                    curImag = curReal * wImag + curImag * wReal;
                    curReal = newCurReal;
                }
            }
        }

        var halfSpectrum = paddedLen / 2;
        var magnitudes = new double[halfSpectrum];
        for (int i = 0; i < halfSpectrum; i++)
        {
            magnitudes[i] = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]) / paddedLen;
        }

        return magnitudes;
    }

    public static (double DominantFreq, double SpectralEnergy, double HighFreqRatio) ExtractFftFeatures(
        double[] magnitudes, double samplingRateHz)
    {
        if (magnitudes.Length < 2) return (0, 0, 0);

        var maxIdx = 0;
        var maxMag = 0.0;
        var totalEnergy = 0.0;
        var highFreqEnergy = 0.0;
        var midPoint = magnitudes.Length / 2;

        for (int i = 1; i < magnitudes.Length; i++)
        {
            if (magnitudes[i] > maxMag)
            {
                maxMag = magnitudes[i];
                maxIdx = i;
            }
            totalEnergy += magnitudes[i] * magnitudes[i];
            if (i >= midPoint / 2) highFreqEnergy += magnitudes[i] * magnitudes[i];
        }

        var dominantFreq = (double)maxIdx / magnitudes.Length * samplingRateHz / 2.0;
        var highFreqRatio = totalEnergy > 0 ? highFreqEnergy / totalEnergy : 0;

        return (dominantFreq, totalEnergy, highFreqRatio);
    }
}
