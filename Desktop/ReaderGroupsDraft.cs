using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

/// <summary>Explicit dialog draft; compares full group identity, name, membership and order.</summary>
internal sealed class ReaderGroupsDraft
{
    private ReaderGroupsSnapshot _saved;
    internal ReaderGroupsDraft(ReaderGroupsSnapshot saved) { _saved = ReaderGroupsValidation.Copy(saved); Groups = _saved.Groups.ToList(); }
    internal List<ReaderGroup> Groups { get; private set; }
    internal long Revision => _saved.Revision;
    internal bool IsDirty => !ReaderGroupsValidation.SameGroups(_saved.Groups, Groups);
    internal ReaderGroupsSnapshot Snapshot => new(1, Revision, Groups.ToArray());
    internal void Accept(ReaderGroupsSnapshot saved) { _saved = ReaderGroupsValidation.Copy(saved); Groups = _saved.Groups.ToList(); }
    internal void Discard() => Groups = _saved.Groups.ToList();
    internal ReaderGroup Create()
    {
        ReaderGroup group = new(Guid.NewGuid(), "", Array.Empty<ResourceId>());
        Groups.Add(group); return group;
    }
    internal void Rename(Guid id, string name) => Replace(id, group => group with { Name = name });
    internal void Add(Guid id, IEnumerable<ResourceId> members) => Replace(id, group => group with
        { Members = group.Members.Concat(members).Distinct().ToArray() });
    internal void Remove(Guid id, ResourceId member) => Replace(id, group => group with
        { Members = group.Members.Where(item => item != member).ToArray() });
    internal void Move(Guid id, int index, int delta) => Replace(id, group =>
    {
        ResourceId[] members = group.Members.ToArray();
        if (index < 0 || index >= members.Length || index + delta < 0 || index + delta >= members.Length) return group;
        (members[index], members[index + delta]) = (members[index + delta], members[index]);
        return group with { Members = members };
    });
    private void Replace(Guid id, Func<ReaderGroup, ReaderGroup> transform)
    {
        int index = Groups.FindIndex(group => group.Id == id);
        if (index >= 0) Groups[index] = transform(Groups[index]);
    }
}
