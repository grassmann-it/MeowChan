using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Persistence;

namespace MeowChan.Secrets;

/// <summary>The profile holds one opaque selection. Its OS address depends on the live local directory.</summary>
internal static class BooruSecretReference
{
    internal const string FileName = "booru-account-ref";
    private const string Namespace = "MeowChan.Booru.ApiKey.v1";

    internal static Guid? Read(ProfileLease lease) => lease.RunExclusive(() =>
    {
        lease.EnsureDirectoryIdentity();
        return ReadFile(lease);
    });

    internal static string Address(ProfileLease lease, Guid reference) => lease.RunExclusive(() =>
    {
        lease.EnsureDirectoryIdentity();
        if (reference == Guid.Empty || !ProfileDirectoryIdentity.IsOwnedByCurrentUser(lease.ProfileDirectory))
            throw new InvalidDataException("The profile account is not locally bound.");

        ProfileInstanceIdentity identity = ProfileInstanceIdentity.TryRead(lease.ProfileDirectory)
            ?? throw new InvalidDataException("The profile account has no profile identity.");
        string directoryToken = ProfileDirectoryIdentity.Capture(lease.ProfileDirectory).DiscardToken;
        lease.EnsureDirectoryIdentity();
        string path = ProfileInstanceIdentity.PathFingerprint(lease.ProfileDirectory);
        string fingerprint = FormattableString.Invariant($"{identity.Id:N}:{path}:{directoryToken}:{reference:N}");
        return Namespace + "." + Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(fingerprint)));
    });

    internal static void Change(ProfileLease lease, Guid? expected, Guid? next)
    {
        if (next == Guid.Empty) throw new ArgumentException("A profile account reference must be nonempty.", nameof(next));

        using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
        lease.RunStoreWrite(admission, () =>
        {
            lease.EnsureDirectoryIdentity();
            if (ReadFile(lease) != expected)
                throw new InvalidDataException("The profile account reference changed.");

            string path = Path.Combine(lease.ProfileDirectory, FileName);
            if (next is null)
            {
                File.Delete(path);
                return true;
            }

            string pending = path + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream file = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(Encoding.ASCII.GetBytes(next.Value.ToString("N")));
                    file.Flush(flushToDisk: true);
                }
                lease.EnsureDirectoryIdentity();
                File.Move(pending, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(pending); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return true;
        });
    }

    private static Guid? ReadFile(ProfileLease lease)
    {
        Span<byte> bytes = stackalloc byte[32];
        try
        {
            using FileStream file = RegularProfileFile.OpenRead(Path.Combine(lease.ProfileDirectory, FileName));
            if (file.Length != bytes.Length)
                throw new InvalidDataException("The profile account reference is invalid.");
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1 || !Guid.TryParseExact(Encoding.ASCII.GetString(bytes), "N", out Guid id)
                || id == Guid.Empty)
                throw new InvalidDataException("The profile account reference is invalid.");
            return id;
        }
        catch (FileNotFoundException) { return null; }
    }
}
