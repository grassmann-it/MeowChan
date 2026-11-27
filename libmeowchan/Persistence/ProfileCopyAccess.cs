using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.Persistence;

internal readonly record struct ProfileDirectoryAccess(uint EffectiveRights, bool ProtectedOwnerOnly);

internal interface IProfileCopyAccess
{
    void CreateProtectedDirectory(string path);
    ProfileDirectoryAccess Inspect(string path);
}

internal static class ProfileCopyAccess
{
    internal static void CreateVerified(string source, string copy, IProfileCopyAccess access)
    {
        uint originalRights = access.Inspect(source).EffectiveRights;
        access.CreateProtectedDirectory(copy);
        ProfileDirectoryAccess copied = access.Inspect(copy);
        if (!copied.ProtectedOwnerOnly || (copied.EffectiveRights & ~originalRights) != 0)
            throw new UnauthorizedAccessException("The profile copy would widen Windows access rights.");
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsProfileCopyAccess : IProfileCopyAccess
{
    internal static WindowsProfileCopyAccess Instance { get; } = new();

    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public void CreateProtectedDirectory(string path)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = GetUser(identity);
        DirectorySecurity security = new();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, Inherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    public ProfileDirectoryAccess Inspect(string path)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        SecurityIdentifier user = GetUser(identity);
        DirectorySecurity security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        bool ownerOnly = security.AreAccessRulesProtected && user.Equals(security.GetOwner(typeof(SecurityIdentifier)))
            && rules.Count == 1 && rules[0] is FileSystemAccessRule rule
            && !rule.IsInherited && rule.IdentityReference.Equals(user) && rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights == FileSystemRights.FullControl && rule.InheritanceFlags == Inherit
            && rule.PropagationFlags == PropagationFlags.None;
        return new(EffectiveRights(security, identity.AccessToken), ownerOnly);
    }

    private static SecurityIdentifier GetUser(WindowsIdentity identity) => identity.User
        ?? throw new UnauthorizedAccessException("The Windows profile owner could not be identified.");

    private static uint EffectiveRights(DirectorySecurity security, SafeAccessTokenHandle primaryToken)
    {
        bool duplicated = DuplicateToken(primaryToken, 2 /* SecurityImpersonation */, out SafeAccessTokenHandle token);
        using (token)
        {
            if (!duplicated) throw NativeFailure();
            var mapping = new FileAccessMapping
            {
                Read = (uint)(FileSystemRights.Read | FileSystemRights.Synchronize),
                Write = (uint)(FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize),
                Execute = (uint)(FileSystemRights.ExecuteFile | FileSystemRights.ReadAttributes
                    | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize),
                All = (uint)FileSystemRights.FullControl
            };
            byte[] privileges = new byte[64];
            uint length = (uint)privileges.Length;
            if (!AccessCheck(security.GetSecurityDescriptorBinaryForm(), token, 0x02000000 /* MAXIMUM_ALLOWED */,
                ref mapping, privileges, ref length, out uint granted, out bool allowed)) throw NativeFailure();
            return allowed ? granted : 0;
        }
    }

    private static IOException NativeFailure() => new("Windows profile access rights could not be verified.",
        new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAccessMapping
    {
        internal uint Read, Write, Execute, All;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateToken(SafeAccessTokenHandle token, int impersonationLevel,
        out SafeAccessTokenHandle duplicate);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AccessCheck(byte[] descriptor, SafeAccessTokenHandle token, uint desired,
        ref FileAccessMapping mapping, [Out] byte[] privileges, ref uint privilegesLength, out uint granted,
        [MarshalAs(UnmanagedType.Bool)] out bool allowed);
}
