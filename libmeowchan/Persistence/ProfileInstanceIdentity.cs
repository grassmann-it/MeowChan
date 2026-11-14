using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowChan.Persistence;

/// <summary>
/// Durable identity of one profile folder: a random id kept inside the profile (<see cref="FileName"/>), so it moves
/// and is copied together with the profile. Unlike <see cref="ProfileDirectoryIdentity"/> (an in-process fence) it
/// survives restarts. A copied profile keeps the id; <see cref="PathFingerprint"/> tells callers that the folder it
/// was recorded for is no longer the one in use (registry, and later the account store of signed-in sources, which
/// asks before an account follows a copied or moved profile). The file never names a path.
/// </summary>
internal sealed record ProfileInstanceIdentity(Guid Id, DateTimeOffset CreatedUtc)
{
    internal const string FileName = "profile-identity.json";
    internal const int SchemaVersion = 1;
    private const int MaximumBytes = 4096;

    /// <summary>The identity of the profile, or null when it has none yet. Never writes. A damaged or newer file
    /// throws <see cref="InvalidDataException"/>; the caller keeps working without an id rather than replace it.</summary>
    internal static ProfileInstanceIdentity? TryRead(string profileDirectory)
    {
        string directory = ProfileLease.NormalizeDirectory(profileDirectory);
        if (!Directory.Exists(directory)) return null;
        string path = Path.Combine(directory, FileName);
        byte[] bytes;
        try
        {
            using FileStream stream = RegularProfileFile.OpenRead(path);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("The profile identity is too large.");
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        return Parse(bytes);
    }

    /// <summary>
    /// Reads the identity of the leased profile, creating it once when the profile has none. Creation publishes a
    /// complete file atomically and never replaces an existing one, also not a damaged one (that throws).
    /// </summary>
    internal static ProfileInstanceIdentity ReadOrCreate(ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
        return lease.RunStoreWrite(admission, () => TryRead(lease.ProfileDirectory) ?? Create(lease.ProfileDirectory));
    }

    /// <summary>Fixed-length fingerprint of the normalized folder path (SHA-256, hex). Compares folders without
    /// storing their path; on Windows the comparison ignores case like the file system.</summary>
    internal static string PathFingerprint(string profileDirectory)
    {
        string normalized = ProfileLease.NormalizeDirectory(profileDirectory);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    internal static ProfileInstanceIdentity Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (JsonDuplicateProperties.Any(bytes)) throw new InvalidDataException("The profile identity repeats a property.");
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The profile identity is not an object.");
            int count = 0;
            foreach (JsonProperty _ in root.EnumerateObject()) count++;
            if (count != 3 || !root.TryGetProperty("schemaVersion", out JsonElement version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int schema) || schema != SchemaVersion)
                throw new InvalidDataException("The profile identity has an unsupported version.");
            if (!root.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(id.GetString(), "D", out Guid parsed) || parsed == Guid.Empty)
                throw new InvalidDataException("The profile identity has no valid id.");
            if (!root.TryGetProperty("createdUtc", out JsonElement created) || created.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParseExact(created.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset createdUtc))
                throw new InvalidDataException("The profile identity has no valid creation time.");
            return new(parsed, createdUtc.ToUniversalTime());
        }
        catch (JsonException error) { throw new InvalidDataException("The profile identity is not valid JSON.", error); }
    }

    internal static byte[] Serialize(ProfileInstanceIdentity identity)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("id", identity.Id.ToString("D"));
            writer.WriteString("createdUtc", identity.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static ProfileInstanceIdentity Create(string directory)
    {
        ProfileInstanceIdentity identity = new(Guid.NewGuid(), DateTimeOffset.UtcNow);
        string destination = Path.Combine(directory, FileName);
        string pending = destination + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream file = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(Serialize(identity));
                file.Flush(flushToDisk: true);
            }
            // Never overwrite: an identity that appeared meanwhile wins and is read back.
            try { File.Move(pending, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { return TryRead(directory) ?? throw new InvalidDataException("The profile identity vanished."); }
            return identity;
        }
        finally
        {
            try { File.Delete(pending); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
