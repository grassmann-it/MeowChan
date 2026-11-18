using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace MeowChan.PluginProtocol;

/// <summary>
/// Checks that a loaded plugin binary binds to the MeowChan.PluginSdk the worker runs with. A plugin built against
/// another SDK build (for example a pinned rollback from an earlier 1.1.0 prerelease whose record constructors have
/// since gained parameters) loads and describes normally; without this check only its first call fails, deep inside
/// the worker, with a MissingMethodException that callers saw as an anonymous "plugin-error".
/// </summary>
internal static class PluginSdkBinding
{
    internal const string SdkAssemblyName = "MeowChan.PluginSdk";
    internal const string MismatchCode = "sdk-mismatch";
    private const int MaximumReferences = 65536;
    private const int MaximumDetailLength = 512;

    /// <summary>
    /// Resolves every type and member reference of <paramref name="plugin"/> into the SDK assembly. Returns a bounded,
    /// single-line description of the first one the loaded SDK does not provide, or null when all of them bind (or the
    /// metadata cannot be read, which the loader itself would already have refused).
    /// </summary>
    internal static string? FindMissingSdkReference(Assembly plugin, string assemblyPath)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(assemblyPath);
        try
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using PEReader pe = new(stream);
            if (!pe.HasMetadata) return null;
            MetadataReader reader = pe.GetMetadataReader();
            Module module = plugin.ManifestModule;
            int seen = 0;
            foreach (TypeReferenceHandle handle in reader.TypeReferences)
            {
                if (!IsSdk(reader, handle, 0)) continue;
                if (++seen > MaximumReferences) return null;
                try { module.ResolveType(MetadataTokens.GetToken(handle)); }
                catch (Exception exception) when (IsBindingFailure(exception)) { return Detail(exception); }
            }
            foreach (MemberReferenceHandle handle in reader.MemberReferences)
            {
                MemberReference member = reader.GetMemberReference(handle);
                // Members of generic instantiations (TypeSpec parents) need a generic context; the SDK declares none.
                if (member.Parent.Kind != HandleKind.TypeReference || !IsSdk(reader, (TypeReferenceHandle)member.Parent, 0)) continue;
                if (++seen > MaximumReferences) return null;
                try { module.ResolveMember(MetadataTokens.GetToken(handle)); }
                catch (Exception exception) when (IsBindingFailure(exception)) { return Detail(exception); }
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException
            or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A runtime binding failure (missing type, method or field), not a plugin's own native-library failure.</summary>
    internal static bool IsBindingFailure(Exception exception) =>
        exception is MissingMemberException
        || exception is TypeLoadException and not (DllNotFoundException or EntryPointNotFoundException);

    private static bool IsSdk(MetadataReader reader, TypeReferenceHandle handle, int depth)
    {
        if (depth > 16) return false;
        EntityHandle scope = reader.GetTypeReference(handle).ResolutionScope;
        return scope.Kind switch
        {
            HandleKind.AssemblyReference => reader.StringComparer.Equals(
                reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name, SdkAssemblyName),
            HandleKind.TypeReference => IsSdk(reader, (TypeReferenceHandle)scope, depth + 1),
            _ => false
        };
    }

    private static string Detail(Exception exception)
    {
        string text = exception.GetType().Name + ": " + exception.Message.ReplaceLineEndings(" ");
        return text.Length > MaximumDetailLength ? text[..MaximumDetailLength] : text;
    }
}
