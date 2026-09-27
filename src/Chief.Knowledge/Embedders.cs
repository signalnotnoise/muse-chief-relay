using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Chief.Knowledge;

public interface IEmbedder
{
    string ModelId { get; }
    int Dimensions { get; }
    float[] Embed(string text);
}

public static class MiniLm
{
    public const string ModelId = "xenova/all-MiniLM-L6-v2-q-afdb6f1a0e45";
    public const string OnnxSha256 = "afdb6f1a0e45b715d0bb9b11772f032c399babd23bfc31fed1c170afc848bdb1";
    public const string VocabSha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3";
    public const string OnnxUrl = "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/model_quantized.onnx";
    public const int Dimensions = 384;

    public static string DefaultVocabPath() =>
        Path.Combine(AppContext.BaseDirectory, "models", "vocab.txt");
}

/// <summary>
/// Offline stand-in behind <see cref="IEmbedder"/>. It hashes tokens; it does
/// not rank by meaning. Select it with CHIEF_KNOWLEDGE_EMBEDDER=hash.
/// </summary>
public sealed class HashingEmbedder : IEmbedder
{
    public string ModelId => "hashing-v1";
    public int Dimensions => MiniLm.Dimensions;

    public float[] Embed(string text)
    {
        var acc = new float[Dimensions];
        foreach (var token in text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var bucket = BitConverter.ToInt32(hash, 0) & 0x7fffffff;
            acc[bucket % acc.Length] += 1f;
        }
        return VectorMath.Normalize(acc);
    }
}

public static class VectorMath
{
    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v)
            sum += x * (double)x;
        if (sum <= 0)
            return (float[])v.Clone();
        var inv = 1.0 / Math.Sqrt(sum);
        var o = new float[v.Length];
        for (var i = 0; i < v.Length; i++)
            o[i] = (float)(v[i] * inv);
        return o;
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return -1;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }
        if (na <= 0 || nb <= 0)
            return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public static byte[] ToBlob(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), BitConverter.SingleToInt32Bits(v[i]));
        return bytes;
    }

    public static float[] FromBlob(byte[] bytes)
    {
        if (bytes.Length % 4 != 0)
            throw new KnowledgeException(2, "embedding blob length is not a multiple of 4");
        var v = new float[bytes.Length / 4];
        for (var i = 0; i < v.Length; i++)
        {
            var bits = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4));
            v[i] = BitConverter.Int32BitsToSingle(bits);
        }
        return v;
    }
}

public sealed class OnnxMiniLmEmbedder : IEmbedder, IDisposable
{
    private readonly BertWordPieceTokenizer _tokenizer;
    private readonly InferenceSession _session;

    private OnnxMiniLmEmbedder(string vocabPath, string onnxPath)
    {
        _tokenizer = new BertWordPieceTokenizer(vocabPath);
        _session = new InferenceSession(onnxPath);
    }

    public string ModelId => MiniLm.ModelId;
    public int Dimensions => MiniLm.Dimensions;

    public static OnnxMiniLmEmbedder Create(string vocabPath, string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        var onnx = Path.Combine(cacheDir, "model_quantized.onnx");
        EnsureOnnx(onnx);
        return new OnnxMiniLmEmbedder(vocabPath, onnx);
    }

    public static void EnsureOnnx(string destination)
    {
        if (File.Exists(destination) && Hash(destination) == MiniLm.OnnxSha256)
            return;
        var partial = destination + ".partial";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("chief-knowledge/0.1");
            var bytes = http.GetByteArrayAsync(MiniLm.OnnxUrl).GetAwaiter().GetResult();
            File.WriteAllBytes(partial, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (hash != MiniLm.OnnxSha256)
                throw new KnowledgeException(2, $"downloaded MiniLM hash is {hash}, expected {MiniLm.OnnxSha256}");
            File.Move(partial, destination, overwrite: true);
        }
        catch (KnowledgeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException agg ? agg.GetBaseException() : ex;
            throw new KnowledgeException(2,
                "could not fetch the MiniLM model (" + inner.GetType().Name +
                "). Set CHIEF_KNOWLEDGE_EMBEDDER=hash to rebuild with the offline hashing embedder, which does not rank by meaning.");
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    public float[] Embed(string text)
    {
        var ids = _tokenizer.Encode(text);
        var n = ids.Length;
        var idTensor = new DenseTensor<long>(new[] { 1, n });
        var mask = new DenseTensor<long>(new[] { 1, n });
        var types = new DenseTensor<long>(new[] { 1, n });
        for (var i = 0; i < n; i++)
        {
            idTensor[0, i] = ids[i];
            mask[0, i] = 1;
            types[0, i] = 0;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", idTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", types)
        };
        using var results = _session.Run(inputs);
        var hidden = results.First().AsTensor<float>();
        if (hidden.Rank != 3 || hidden.Dimensions[2] != Dimensions)
            throw new KnowledgeException(2, "MiniLM output was not [batch, sequence, 384]");

        var sum = new double[Dimensions];
        for (var t = 0; t < n; t++)
        {
            for (var d = 0; d < Dimensions; d++)
                sum[d] += hidden[0, t, d];
        }
        var vec = new float[Dimensions];
        for (var d = 0; d < Dimensions; d++)
            vec[d] = (float)(sum[d] / n);
        return VectorMath.Normalize(vec);
    }

    public void Dispose() => _session.Dispose();

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

public static class EmbedderFactory
{
    public static IEmbedder CreateDefault(string knowledgeDir)
    {
        var which = Environment.GetEnvironmentVariable("CHIEF_KNOWLEDGE_EMBEDDER");
        if (string.Equals(which, "hash", StringComparison.OrdinalIgnoreCase))
            return new HashingEmbedder();
        if (!string.IsNullOrEmpty(which) && !string.Equals(which, "minilm", StringComparison.OrdinalIgnoreCase))
            throw new KnowledgeException(2, "CHIEF_KNOWLEDGE_EMBEDDER must be minilm or hash");

        var vocab = Environment.GetEnvironmentVariable("CHIEF_KNOWLEDGE_VOCAB");
        if (string.IsNullOrEmpty(vocab))
            vocab = MiniLm.DefaultVocabPath();
        if (!File.Exists(vocab))
            throw new KnowledgeException(2, "MiniLM vocab.txt was not next to the tool (" + vocab + ")");

        var cache = Environment.GetEnvironmentVariable("CHIEF_KNOWLEDGE_MODEL_DIR");
        if (string.IsNullOrEmpty(cache))
            cache = Path.Combine(knowledgeDir, ".index", "models");
        return OnnxMiniLmEmbedder.Create(vocab, cache);
    }
}
