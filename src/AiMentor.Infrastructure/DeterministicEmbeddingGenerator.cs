using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AiMentor.Application;

namespace AiMentor.Infrastructure;

/// <summary>
/// 用于开发与契约测试的特征哈希嵌入，不代表生产语义模型质量。
/// </summary>
public sealed class DeterministicEmbeddingGenerator : ITextEmbeddingGenerator
{
    public int Dimensions => 256;

    public Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vector = new float[Dimensions];
        foreach (var token in Tokenize(text))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var index = BitConverter.ToUInt32(hash, 0) % (uint)Dimensions;
            vector[index] += (hash[4] & 1) == 0 ? 1 : -1;
        }

        var norm = Math.Sqrt(vector.Sum(value => value * value));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        }
        return Task.FromResult(vector);
    }

    private static IEnumerable<string> Tokenize(string value)
    {
        foreach (Match match in Regex.Matches(value.ToLowerInvariant(), @"[a-z0-9][a-z0-9._-]*|[\u4e00-\u9fff]+"))
        {
            var token = match.Value;
            if (token.Any(character => character is >= '\u4e00' and <= '\u9fff'))
            {
                if (token.Length == 1) yield return token;
                for (var i = 0; i < token.Length - 1; i++) yield return token.Substring(i, 2);
            }
            else yield return token;
        }
    }
}
