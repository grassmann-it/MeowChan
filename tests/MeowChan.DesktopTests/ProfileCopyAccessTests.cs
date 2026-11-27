using System.Security.AccessControl;
using System.Security.Principal;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class ProfileCopyAccessTests
{
    [WindowsProfileAclFact]
    public void UpgradeAndReturnProtectTheCopyUnderASharedParent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using TempDirectory temp = new();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = Assert.IsType<SecurityIdentifier>(identity.User);
        SecurityIdentifier everyone = new(WellKnownSidType.WorldSid, null);
        DirectoryInfo shared = new(Path.Combine(temp.Path, "shared"));
        DirectorySecurity sharedAcl = OwnerOnly(user);
        sharedAcl.AddAccessRule(Rule(everyone, FileSystemRights.ReadAndExecute));
        shared.Create(sharedAcl);

        DirectoryInfo source = new(Path.Combine(shared.FullName, "legacy"));
        source.Create(OwnerOnly(user));
        new Config { DownloadPath = Path.Combine(source.FullName, "downloads") }.SaveConfig(source.FullName);

        using ProfileLease working = ProfileUpgrade.Open(source.FullName, temp.Path);
        string snapshot = Assert.IsType<string>(ProfileUpgrade.SnapshotFor(working.ProfileDirectory));
        string restored = Path.Combine(shared.FullName, "restored");
        ProfileSnapshot.Read(snapshot).Restore(snapshot, restored);

        AssertProtected(Path.GetDirectoryName(working.ProfileDirectory)!, user);
        AssertProtected(working.ProfileDirectory, user);
        AssertProtected(snapshot, user);
        AssertProtected(restored, user);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static DirectorySecurity OwnerOnly(SecurityIdentifier user)
    {
        DirectorySecurity acl = new();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(user);
        acl.AddAccessRule(Rule(user, FileSystemRights.FullControl));
        return acl;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileSystemAccessRule Rule(SecurityIdentifier user, FileSystemRights rights) =>
        new(user, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertProtected(string path, SecurityIdentifier user)
    {
        DirectorySecurity acl = new DirectoryInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        Assert.Equal(user, acl.GetOwner(typeof(SecurityIdentifier)));
        FileSystemAccessRule rule = Assert.Single(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
        Assert.Equal(user, rule.IdentityReference);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
    }

    private sealed class WindowsProfileAclFactAttribute : FactAttribute
    {
        public WindowsProfileAclFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires native Windows ACLs.";
        }
    }
}
