using System.Net;
using System.Reflection;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Ratings;
using MeowChan.Storage;

namespace MeowChan.Tests;

/// <summary>Public shape checks accompanying the 1.0.2 to 1.1.0 inventory delta.</summary>
public sealed class ApiReleaseReviewTests
{
    [Fact]
    public void NewOptionalEvidencePreservesReleasedRecordConstructorsAndDeconstruction()
    {
        AssertRecordShape(typeof(FailureInfo), [typeof(FailureKind), typeof(string), typeof(HttpStatusCode?)]);
        FailureInfo failure = new(FailureKind.Unknown, "legacy");
        Assert.Null(failure.DiagnosticCode);
        Assert.Null(failure.NetworkStage);
        Assert.Null(failure.RetryAt);
        foreach (string name in new[] { "DiagnosticCode", "NetworkStage", "RetryAt" })
            AssertInitOnly(typeof(FailureInfo).GetProperty(name)!);

        AssertRecordShape(typeof(ArchiveHtmlExportOptions), [typeof(bool)]);
        Assert.Equal("en", new ArchiveHtmlExportOptions().Language);
        AssertInitOnly(typeof(ArchiveHtmlExportOptions).GetProperty("Language")!);

        AssertRecordShape(typeof(MediaAttachmentBinding), [typeof(AttachmentRevisionReference), typeof(string),
            typeof(string), typeof(MediaProviderMetadata), typeof(MediaActualIntegrity), typeof(MediaAcceptanceRecord)]);
        AssertInitOnly(typeof(MediaAttachmentBinding).GetProperty("PassiveImport")!);
        Assert.Equal(2, (int)MediaAcceptanceKind.PassiveImport);
    }

    [Fact]
    public void ReviewedConstantValueChangesRemainObsoleteIntFieldsNotRemovedMembers()
    {
        foreach (Type store in new[] { typeof(ThreadFavoriteStore), typeof(MediaRatingStore) })
        {
            FieldInfo field = store.GetField("MaximumBytes")!;
            Assert.True(field.IsLiteral);
            Assert.Equal(typeof(int), field.FieldType);
            Assert.Equal(8_388_608, field.GetRawConstantValue());
            Assert.NotNull(field.GetCustomAttribute<ObsoleteAttribute>());
        }
    }

    [Fact]
    public void ExplicitStartupAndConcurrencyExtensionsHaveTheirReviewedSignatures()
    {
        MethodInfo start = typeof(Registry).GetMethod("StartAsync", [typeof(ProfileLease), typeof(CancellationToken)])!;
        Assert.NotNull(start);
        Assert.Equal(typeof(Task), start.ReturnType);
        Assert.True(start.GetParameters()[1].IsOptional);
        Assert.Equal(typeof(void), typeof(DownloadQueue).GetMethod("UpdateConcurrencyLimits", [typeof(int), typeof(int)])!.ReturnType);
        Assert.Equal(typeof(void), typeof(DownloadQueue).GetMethod("UpdateParallelDownloadsSafetyLimit", [typeof(int)])!.ReturnType);
        Assert.Equal(256, DownloadQueue.MaximumParallelDownloadsSafetyLimit);
        Assert.Equal(32, DownloadQueue.ProtectiveParallelDownloadsCeiling);
        Assert.Equal(32, new SessionOptions().ParallelDownloadsSafetyLimit);
        AssertInitOnly(typeof(SessionOptions).GetProperty("ParallelDownloadsSafetyLimit")!);
        Assert.Equal(typeof(FailureInfo), typeof(IFailureInfoProvider).GetProperty("FailureInfo")!.PropertyType);
        Assert.Null(typeof(IFailureInfoProvider).GetProperty("FailureInfo")!.SetMethod);
    }

    [Fact]
    public async Task ReleasedDurableRestoreCallSequenceNowRequiresExplicitStartup()
    {
        // Compatibility finding, not an assertion that the old call sequence still works.
        // Only public APIs establish ownership; no internal Ready/admission shortcut.
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        SessionStore sessions = new(profile.Path, lease);
        WorkStateStore work = new(profile.Path, lease);
        await using Registry registry = new([], new DownloadQueue(1, 1024 * 1024), profile.Path,
            TimeSpan.FromMinutes(1), 60, sessionStore: sessions, workStore: work);

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RestoreAsync());
        await registry.StartAsync(lease).WaitAsync(TimeSpan.FromSeconds(15));
        await registry.RestoreAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await registry.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void CoreBooruImplementationDoesNotBecomeAPublicContract()
    {
        Type[] exported = typeof(Registry).Assembly.GetExportedTypes();
        Assert.DoesNotContain(exported, type => type.Namespace == "MeowChan.Booru" ||
            type.Namespace?.StartsWith("MeowChan.Booru.", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(exported, type => type.Name.Contains("Booru", StringComparison.Ordinal));
    }

    private static void AssertRecordShape(Type type, Type[] signature)
    {
        ConstructorInfo constructor = Assert.Single(type.GetConstructors());
        Assert.Equal(signature, constructor.GetParameters().Select(p => p.ParameterType).ToArray());
        MethodInfo deconstruct = Assert.Single(type.GetMethods().Where(m => m.Name == "Deconstruct"));
        Assert.Equal(signature.Select(t => t.MakeByRefType()).ToArray(),
            deconstruct.GetParameters().Select(p => p.ParameterType).ToArray());
    }

    private static void AssertInitOnly(PropertyInfo property)
        => Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit),
            property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
}
