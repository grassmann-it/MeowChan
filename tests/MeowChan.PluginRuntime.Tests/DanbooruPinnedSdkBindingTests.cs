using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using MeowChan.PluginProtocol;

namespace MeowChan.PluginRuntime.Tests;

/// <summary>The 16 Nov cat-search failure loaded a pinned binary referencing the removed SourcePostId type,
/// not the freshly staged Danbooru binary. Keep this distinct from the missing-constructor regression.</summary>
public sealed class DanbooruPinnedSdkBindingTests
{
    [Fact]
    public void PinnedBinaryReferencingRemovedSourcePostIdGetsAnActionableSdkMismatch()
    {
        string directory = Path.Combine(Path.GetTempPath(), "danbooru-pinned-sdk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        AssemblyLoadContext oldContext = new("danbooru-old-sdk", isCollectible: true);
        AssemblyLoadContext pluginContext = new("danbooru-pinned-plugin", isCollectible: true);
        try
        {
            // Same assembly name, but an obsolete API: describe can work while search cannot even bind.
            PersistedAssemblyBuilder sdk = new(new AssemblyName(PluginSdkBinding.SdkAssemblyName), typeof(object).Assembly);
            TypeBuilder oldType = sdk.DefineDynamicModule("old-sdk").DefineType("MeowChan.PluginSdk.SourcePostId", TypeAttributes.Public);
            oldType.DefineDefaultConstructor(MethodAttributes.Public);
            oldType.CreateType();
            string sdkPath = Path.Combine(directory, "MeowChan.PluginSdk.dll");
            sdk.Save(sdkPath);
            Type removed = oldContext.LoadFromAssemblyPath(sdkPath).GetType("MeowChan.PluginSdk.SourcePostId")!;
            PersistedAssemblyBuilder plugin = new(new AssemblyName("PinnedDanbooruFixture"), typeof(object).Assembly);
            TypeBuilder caller = plugin.DefineDynamicModule("plugin").DefineType("PinnedSearch", TypeAttributes.Public);
            ILGenerator il = caller.DefineMethod("Search", MethodAttributes.Public | MethodAttributes.Static,
                typeof(object), Type.EmptyTypes).GetILGenerator();
            il.Emit(OpCodes.Newobj, removed.GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Ret);
            caller.CreateType();
            string pluginPath = Path.Combine(directory, "plugin.dll");
            plugin.Save(pluginPath);
            // The plugin's SDK resolution falls back to the actual SDK in the default context.
            Assert.NotNull(typeof(MeowChan.PluginSdk.IBooruSourcePlugin).Assembly);
            string? problem = PluginSdkBinding.FindMissingSdkReference(pluginContext.LoadFromAssemblyPath(pluginPath), pluginPath);
            Assert.NotNull(problem);
            Assert.StartsWith("TypeLoadException: ", problem);
            Assert.Contains("MeowChan.PluginSdk.SourcePostId", problem);
            Assert.Equal("sdk-mismatch", PluginSdkBinding.MismatchCode);
        }
        finally
        {
            pluginContext.Unload();
            oldContext.Unload();
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
