namespace FractalFlameCurator.Generation;

public sealed class SpaceFillingSimplexSampler
{
    public const string Version = "space-filling-simplex-v1";

    private static readonly int[] Bases = [2, 3, 5, 7];
    private readonly long[] _sampleIndices = new long[6];
    private readonly double[,] _shifts = new double[6, 4];

    public SpaceFillingSimplexSampler(long seed)
    {
        var random = new DeterministicRandom(seed);
        for (var count = 2; count <= 5; count++)
        {
            for (var dimension = 0; dimension < count - 1; dimension++)
                _shifts[count, dimension] = random.NextDouble();
        }
    }

    public double[] NextWeights(int variationCount, double minimumShare, DeterministicRandom random)
    {
        if (variationCount is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(variationCount), "Variation count must be between 1 and 5.");
        if (!double.IsFinite(minimumShare) || minimumShare < 0 || minimumShare > 1d / variationCount)
            throw new ArgumentOutOfRangeException(nameof(minimumShare), $"Minimum share must be between 0 and {1d / variationCount}.");
        if (variationCount == 1) return [1];

        var sampleIndex = ++_sampleIndices[variationCount];
        var cuts = new double[variationCount - 1];
        for (var dimension = 0; dimension < cuts.Length; dimension++)
        {
            var coordinate = RadicalInverse((ulong)sampleIndex, Bases[dimension]) + _shifts[variationCount, dimension];
            cuts[dimension] = coordinate - Math.Floor(coordinate);
        }
        Array.Sort(cuts);

        var weights = new double[variationCount];
        var remainingShare = 1 - variationCount * minimumShare;
        var previousCut = 0d;
        for (var i = 0; i < cuts.Length; i++)
        {
            weights[i] = minimumShare + remainingShare * (cuts[i] - previousCut);
            previousCut = cuts[i];
        }
        weights[^1] = minimumShare + remainingShare * (1 - previousCut);

        for (var i = weights.Length - 1; i > 0; i--)
        {
            var swapIndex = random.NextInt(0, i + 1);
            (weights[i], weights[swapIndex]) = (weights[swapIndex], weights[i]);
        }
        return weights;
    }

    private static double RadicalInverse(ulong index, int numberBase)
    {
        var result = 0d;
        var factor = 1d / numberBase;
        while (index > 0)
        {
            result += factor * (index % (ulong)numberBase);
            index /= (ulong)numberBase;
            factor /= numberBase;
        }
        return result;
    }
}
