using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativeMediaConflictDomainTests
{
    private static readonly MeowChan.ResourceId Resource = new("4chan", "g", "123");
    private const string Request = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ResourceSet = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Occurrence = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string Active = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string Candidate = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string Other = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    [Fact]
    public void Identity_is_stable_across_operation_retries_and_provenance_is_separate()
    {
        NativeMediaConflictIdentity first = Identity(Active, Candidate);
        NativeMediaConflictIdentity retry = Identity(Active, Candidate);
        NativeMediaConflictIdentity changed = Identity(Active, Other);

        Assert.Equal(first, retry);
        Assert.Equal(first.IdentitySha256, retry.IdentitySha256);
        Assert.NotEqual(first.IdentitySha256, changed.IdentitySha256);

        NativeMediaConflictProvenance provenance = new(Guid.NewGuid(), Request, ResourceSet);
        Assert.Equal(Resource, first.Resource);
        Assert.Equal(Request, provenance.RequestSha256);
        Assert.NotEqual(provenance.OperationId, Guid.Empty);
    }

    [Fact]
    public void Inventory_is_content_free_bounded_and_order_independent()
    {
        NativeMediaConflictProvenance provenance = Provenance();
        NativeMediaConflictEntry first = Entry(Occurrence, Active, Candidate);
        NativeMediaConflictEntry second = Entry(Other, Candidate, Active,
            NativeMediaConflictResolutionStatus.Resolved);
        NativeMediaConflictInventory a = new(provenance, [second, first]);
        NativeMediaConflictInventory b = new(provenance, [first, second]);

        Assert.Equal(a.InventorySha256, b.InventorySha256);
        Assert.Equal(a.Entries.Select(entry => entry.Identity.IdentitySha256),
            b.Entries.Select(entry => entry.Identity.IdentitySha256));
        Assert.DoesNotContain(a.Entries.SelectMany(EntryStrings), value => value.Contains("/", StringComparison.Ordinal));
        Assert.Equal(1, a.ReadPage(0, 1).NextOffset);
        NativeMediaConflictInventoryPage page = a.ReadPage(0, 1);
        Assert.Single(page.Entries);
        Assert.Equal(a.InventorySha256, page.InventorySha256);
        Assert.Equal(1, page.NextOffset);
        Assert.Empty(a.ReadPage(2, 100).Entries);
    }

    [Fact]
    public void Inventory_pages_are_bounded_and_duplicate_conflicts_are_rejected()
    {
        NativeMediaConflictInventory inventory = new(Provenance(), [Entry(Occurrence, Active, Candidate)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.ReadPage(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.ReadPage(0, NativeMediaConflictInventory.MaximumPageSize + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.ReadPage(2, 1));
        Assert.Throws<InvalidDataException>(() => new NativeMediaConflictInventory(Provenance(),
            [Entry(Occurrence, Active, Candidate), Entry(Occurrence, Active, Candidate)]));
    }

    [Fact]
    public void Projections_reject_paths_private_values_and_mismatched_identity()
    {
        Assert.Throws<ArgumentException>(() => new NativeMediaConflictVariantProjection("/tmp/private-note", NativeMediaConflictVariantStatus.Active, 1));
        Assert.Throws<ArgumentException>(() => new NativeMediaConflictIdentity(Resource, Occurrence, Active, Active));
        NativeMediaConflictIdentity identity = Identity(Active, Candidate);
        NativeMediaConflictVariantProjection wrong = new(Other, NativeMediaConflictVariantStatus.Active, 1);
        NativeMediaConflictVariantProjection candidate = new(Candidate, NativeMediaConflictVariantStatus.Inactive, 1);
        Assert.Throws<ArgumentException>(() => new NativeMediaConflictEntry(identity, wrong, candidate));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_action_has_a_deterministic_idempotent_decision_identity(int actionValue)
    {
        NativeMediaConflictActionKind action = (NativeMediaConflictActionKind)actionValue;
        NativeMediaConflictIdentity identity = Identity(Active, Candidate);
        NativeMediaConflictActionProposal first = new(identity, action, ResourceSet);
        NativeMediaConflictActionProposal retry = new(identity, action, ResourceSet);
        NativeMediaConflictActionProposal changedInventory = new(identity, action, Request);

        Assert.Equal(first.DecisionIdentity, retry.DecisionIdentity);
        Assert.Equal(first.DecisionIdentity.Value, retry.DecisionIdentity.Value);
        Assert.NotEqual(first.DecisionIdentity.Value, changedInventory.DecisionIdentity.Value);
        Assert.Equal(identity.IdentitySha256, first.DecisionIdentity.ConflictSha256);
        Assert.Equal(action, first.DecisionIdentity.Action);
    }

    [Fact]
    public void Decision_is_an_inert_fact_and_does_not_change_inventory_or_expose_apply_handles()
    {
        NativeMediaConflictActionProposal proposal = new(Identity(Active, Candidate),
            NativeMediaConflictActionKind.Promote, ResourceSet);
        NativeMediaConflictDecision decision = new(proposal, DateTimeOffset.UnixEpoch.AddDays(1));

        Assert.Same(proposal, decision.Proposal);
        Assert.Equal(proposal.DecisionIdentity, decision.Identity);
        Assert.DoesNotContain(typeof(NativeMediaConflictDecision).GetMethods(), method =>
            method.Name.Contains("Apply", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(NativeMediaConflictActionProposal).GetMethods(), method =>
            method.Name.Contains("Apply", StringComparison.OrdinalIgnoreCase));
    }

    private static NativeMediaConflictProvenance Provenance()
        => new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Request, ResourceSet);

    private static NativeMediaConflictIdentity Identity(string active, string candidate)
        => new(Resource, Occurrence, active, candidate);

    private static NativeMediaConflictEntry Entry(string occurrence, string active, string candidate,
        NativeMediaConflictResolutionStatus resolution = NativeMediaConflictResolutionStatus.Open)
        => new(new(Resource, occurrence, active, candidate),
            new(active, resolution == NativeMediaConflictResolutionStatus.Promoted
                ? NativeMediaConflictVariantStatus.Inactive : NativeMediaConflictVariantStatus.Active, 12),
            new(candidate, resolution == NativeMediaConflictResolutionStatus.Promoted
                ? NativeMediaConflictVariantStatus.Active : NativeMediaConflictVariantStatus.Inactive, 13), resolution);

    private static IEnumerable<string> EntryStrings(NativeMediaConflictEntry entry)
    {
        yield return entry.Identity.Resource.Source;
        yield return entry.Identity.Resource.Board;
        yield return entry.Identity.Resource.Thread!;
        yield return entry.Identity.OccurrenceSha256;
        yield return entry.Identity.ActiveVariantSha256;
        yield return entry.Identity.CandidateVariantSha256;
        yield return entry.Target.VariantSha256;
        yield return entry.Candidate.VariantSha256;
    }
}
