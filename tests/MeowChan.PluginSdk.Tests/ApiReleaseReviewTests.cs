using System.Reflection;
using System.Text;
using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Tests;

/// <summary>Reviewed 1.1.0 ABI shapes, not a general promise of 1.x binary compatibility.</summary>
public sealed class ApiReleaseReviewTests
{
    [Fact]
    public void BooruIdentityRenameHasNoObsoletePublicAliasAndKeepsTheWireShape()
    {
        Assembly sdk = typeof(BooruPostId).Assembly;
        Assert.Null(sdk.GetType("MeowChan.PluginSdk.SourcePostId"));
        Assert.True(typeof(BooruPostId).IsSealed);
        Assert.NotNull(typeof(BooruPostId).GetConstructor([typeof(string), typeof(string)]));
        Assert.Equal(typeof(BooruPostId), typeof(BooruPost).GetProperty("Id")!.PropertyType);
        foreach (Type result in new[] { typeof(BooruPostRequest), typeof(BooruPostNotFound),
                     typeof(BooruPostDeferred), typeof(BooruPostFailure) })
            Assert.Equal(typeof(BooruPostId), result.GetProperty("Post")!.PropertyType);

        BooruPostId id = new("synthetic", "42");
        Assert.Equal("{\"sourceId\":\"synthetic\",\"postId\":\"42\"}",
            Encoding.UTF8.GetString(SdkJson.Serialize(id)));
        Assert.Equal(id, SdkJson.Deserialize<BooruPostId>(
            Encoding.UTF8.GetBytes("{\"sourceId\":\"synthetic\",\"postId\":\"42\"}")));
        Assert.NotEqual(id, id with { SourceId = "other" });
    }

    [Fact]
    public void OptionalDeclarationStatusPreservesTheReleasedConstructorAndDeconstruction()
    {
        // The released 1.0.2 constructor had 12 parameters; DeclarationStatus is init-only,
        // not a new optional positional argument (which would replace the binary signature).
        Type[] signature = [typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(IReadOnlyList<SourceDescription>), typeof(IReadOnlyList<FilterDescription>),
            typeof(SettingsSchema), typeof(IReadOnlyList<InformationEntry>), typeof(string), typeof(Uri), typeof(Uri)];
        ConstructorInfo constructor = Assert.Single(typeof(PluginDescription).GetConstructors());
        Assert.Equal(signature, constructor.GetParameters().Select(p => p.ParameterType).ToArray());
        MethodInfo deconstruct = Assert.Single(typeof(PluginDescription).GetMethods(), m => m.Name == "Deconstruct");
        Assert.Equal(signature.Select(t => t.MakeByRefType()).ToArray(),
            deconstruct.GetParameters().Select(p => p.ParameterType).ToArray());
        AssertInitOnly(typeof(PluginDescription).GetProperty("DeclarationStatus")!);

        // Frozen legacy JSON, independent of the current serializer's output/defaults.
        const string legacy = """
            {"pluginId":"test.plugin","name":"Test","pluginVersion":"1.0","sdkVersion":"1.0.0",
             "description":"Metadata only","sources":[],"filters":[],
             "settings":{"version":"1","fields":[]},"information":[]}
            """;
        PluginDescription description = SdkJson.Deserialize<PluginDescription>(Encoding.UTF8.GetBytes(legacy));
        Assert.Equal(PluginDeclarationStatus.Available, description.DeclarationStatus);
        Assert.DoesNotContain("declarationStatus", Encoding.UTF8.GetString(SdkJson.Serialize(description)));
        PluginDescription blocked = description with { DeclarationStatus = PluginDeclarationStatus.Blocked };
        Assert.Empty(SdkValidation.ValidateDescription(blocked));
        Assert.Contains("\"declarationStatus\":\"Blocked\"", Encoding.UTF8.GetString(SdkJson.Serialize(blocked)));
    }

    [Fact]
    public void BooruIsAnIndependentInterfaceAndDoesNotAddObligationsToReleasedSourcePlugins()
    {
        Assert.Equal(new[] { typeof(IPlugin) }, typeof(IBooruSourcePlugin).GetInterfaces());
        Assert.False(typeof(ISourcePlugin).IsAssignableFrom(typeof(IBooruSourcePlugin)));
        Assert.Equal(new[] { "ReadCatalogAsync", "ReadThreadAsync" },
            typeof(ISourcePlugin).GetMethods().Select(m => m.Name).Order().ToArray());
        Assert.Equal(new[] { "ReadPostAsync", "SearchPostsAsync" },
            typeof(IBooruSourcePlugin).GetMethods().Select(m => m.Name).Order().ToArray());
        foreach ((string name, Type request, Type result) in new[]
                 { ("ReadPostAsync", typeof(BooruPostRequest), typeof(BooruPostResult)),
                   ("SearchPostsAsync", typeof(BooruSearchRequest), typeof(BooruSearchResult)) })
        {
            MethodInfo method = typeof(IBooruSourcePlugin).GetMethod(name)!;
            Assert.Equal(typeof(Task<>).MakeGenericType(result), method.ReturnType);
            Assert.Equal(new[] { request, typeof(PluginCallContext), typeof(CancellationToken) },
                method.GetParameters().Select(p => p.ParameterType).ToArray());
            Assert.True(method.GetParameters()[2].IsOptional);
        }
    }

    [Fact]
    public void ReviewedCursorParametersAndPresenceMaskStayFixedForTheRelease()
    {
        AssertCursorConstructor(typeof(BooruSearchRequest),
            [typeof(string), typeof(string), typeof(int), typeof(int), typeof(string)], "BeforePostId");
        AssertCursorConstructor(typeof(BooruSearchPage),
            [typeof(string), typeof(string), typeof(int), typeof(IReadOnlyList<BooruPost>), typeof(bool), typeof(string)],
            "NextBeforePostId");
        Assert.Equal(32, (int)SourceCapabilities.PostSearch);
        Assert.Equal(511, (int)BooruFieldPresence.All);
        Assert.Equal(10, Enum.GetValues<BooruFieldPresence>().Count(v => v != BooruFieldPresence.All));
    }

    private static void AssertCursorConstructor(Type type, Type[] signature, string cursorName)
    {
        ConstructorInfo constructor = Assert.Single(type.GetConstructors());
        Assert.Equal(signature, constructor.GetParameters().Select(p => p.ParameterType).ToArray());
        ParameterInfo cursor = constructor.GetParameters()[^1];
        Assert.Equal(cursorName, cursor.Name);
        Assert.True(cursor.IsOptional);
        Assert.Null(cursor.DefaultValue);
        AssertInitOnly(type.GetProperty(cursorName)!);
    }

    private static void AssertInitOnly(PropertyInfo property)
        => Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit),
            property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
}
