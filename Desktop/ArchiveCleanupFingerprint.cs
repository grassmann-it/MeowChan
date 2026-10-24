using System;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeowChan.Desktop;

internal static class ArchiveCleanupFingerprint
{
    internal static string Of<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
