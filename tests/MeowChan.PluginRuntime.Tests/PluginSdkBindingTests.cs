using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using MeowChan.PluginProtocol;
using MeowChan.TestSupport;

namespace MeowChan.PluginRuntime.Tests;

// The worker's describe-time check (PluginSdkBinding): a plugin binary built against another MeowChan.PluginSdk build
// (the 16 Nov 2026 Danbooru "plugin-error" was an 11 Nov build calling a BooruSearchPage constructor that today's SDK
// no longer has) is named before its first call instead of failing there with a MissingMethodException.
public sealed class PluginSdkBindingTests
{
    [Fact]
    public void TheShippedDanbooruPluginBindsToThisSdk()
    {
        string path = BuiltTestBinaries.LibraryOf(Path.Combine("plugins", "Official", "Danbooru"), "MeowChan.Plugin.Danbooru");
        AssemblyLoadContext context = new("danbooru-binding", isCollectible: true);
        try { Assert.Null(PluginSdkBinding.FindMissingSdkReference(context.LoadFromAssemblyPath(path), path)); }
        finally { context.Unload(); }
    }

    [Fact]
    public void AConstructorThatThisSdkNoLongerHasIsNamed()
    {
        using Scratch scratch = new();
        string? problem = Check(scratch, sdk => DefineConstructor(sdk, "MeowChan.PluginSdk.BooruSearchPage"),
            (il, member) => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Newobj, (ConstructorInfo)member); });
        Assert.NotNull(problem);
        Assert.StartsWith("MissingMethodException: ", problem);
        Assert.Contains("MeowChan.PluginSdk.BooruSearchPage..ctor(System.String)", problem);
        Assert.DoesNotContain('\n', problem);
    }

    [Fact]
    public void ATypeThatThisSdkDoesNotDeclareIsNamed()
    {
        using Scratch scratch = new();
        string? problem = Check(scratch, sdk => DefineConstructor(sdk, "MeowChan.PluginSdk.RemovedInThisBuild"),
            (il, member) => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Newobj, (ConstructorInfo)member); });
        Assert.NotNull(problem);
        Assert.StartsWith("TypeLoadException: ", problem);
        Assert.Contains("MeowChan.PluginSdk.RemovedInThisBuild", problem);
    }

    [Fact]
    public void OnlyBindingFailuresCountNotAPluginsOwnNativeLibraryFailures()
    {
        Assert.True(PluginSdkBinding.IsBindingFailure(new MissingMethodException("m")));
        Assert.True(PluginSdkBinding.IsBindingFailure(new MissingFieldException("f")));
        Assert.True(PluginSdkBinding.IsBindingFailure(new TypeLoadException("t")));
        Assert.False(PluginSdkBinding.IsBindingFailure(new DllNotFoundException("native")));
        Assert.False(PluginSdkBinding.IsBindingFailure(new EntryPointNotFoundException("native")));
        Assert.False(PluginSdkBinding.IsBindingFailure(new InvalidOperationException("plugin")));
    }

    // An "older SDK" stand-in under the real assembly name declares one type with a (string) constructor; a stale
    // plugin built against it calls that constructor. Loaded next to the real SDK, the call no longer binds.
    private static string? Check(Scratch scratch, Func<ModuleBuilder, Type> declare, Action<ILGenerator, MemberInfo> call)
    {
        string sdkPath = Path.Combine(scratch.Path, "old-sdk", "MeowChan.PluginSdk.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(sdkPath)!);
        PersistedAssemblyBuilder oldSdk = new(new AssemblyName(PluginSdkBinding.SdkAssemblyName), typeof(object).Assembly);
        declare(oldSdk.DefineDynamicModule(PluginSdkBinding.SdkAssemblyName + ".dll"));
        oldSdk.Save(sdkPath);

        AssemblyLoadContext oldContext = new("old-sdk", isCollectible: true);
        AssemblyLoadContext pluginContext = new("stale-plugin", isCollectible: true);
        try
        {
            Type oldType = oldContext.LoadFromAssemblyPath(sdkPath).GetExportedTypes().Single();
            string pluginPath = Path.Combine(scratch.Path, "StalePlugin.dll");
            PersistedAssemblyBuilder plugin = new(new AssemblyName("StalePlugin"), typeof(object).Assembly);
            TypeBuilder caller = plugin.DefineDynamicModule("StalePlugin.dll").DefineType("StalePlugin.Caller",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            MethodBuilder method = caller.DefineMethod("Call", MethodAttributes.Public | MethodAttributes.Static, typeof(object), [typeof(string)]);
            ILGenerator il = method.GetILGenerator();
            call(il, oldType.GetConstructor([typeof(string)])!);
            il.Emit(OpCodes.Ret);
            caller.CreateType();
            plugin.Save(pluginPath);

            // The stale plugin's SDK reference falls back to the default context, i.e. to the real, current SDK.
            Assert.NotNull(typeof(MeowChan.PluginSdk.BooruSearchPage).Assembly);
            return PluginSdkBinding.FindMissingSdkReference(pluginContext.LoadFromAssemblyPath(pluginPath), pluginPath);
        }
        finally
        {
            pluginContext.Unload();
            oldContext.Unload();
        }
    }

    private static Type DefineConstructor(ModuleBuilder module, string fullName)
    {
        TypeBuilder type = module.DefineType(fullName, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        ConstructorBuilder constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(string)]);
        ILGenerator il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);
        return type.CreateType();
    }

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meowchan-sdk-binding-" + Guid.NewGuid().ToString("N"));
        public Scratch() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
