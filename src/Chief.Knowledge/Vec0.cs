using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Chief.Knowledge;

public enum VectorBackendPreference
{
    Auto,
    Pure
}

public sealed class IndexOptions
{
    public VectorBackendPreference Backend { get; init; } = VectorBackendPreference.Auto;
    public string? Vec0Path { get; init; }
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}

public static class Vec0Locator
{
    public const string Sha256 = "f285c46d5496fe7571be40ab40c0b151d091892c141f89a503e02392e0961233";

    public static string? Resolve(string? envPath, string baseDirectory)
    {
        foreach (var candidate in new[] { envPath, Path.Combine(baseDirectory, "vec0.so") })
        {
            if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
                continue;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidate))).ToLowerInvariant();
            if (hash == Sha256)
                return candidate;
        }
        return null;
    }

    public static bool TryLoad(SqliteConnection connection, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        try
        {
            connection.LoadExtension(path);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT vec_version()";
            _ = cmd.ExecuteScalar();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
