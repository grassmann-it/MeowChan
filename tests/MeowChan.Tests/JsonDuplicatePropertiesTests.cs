using System.Text;
using System.Text.Json;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests;

/// <summary>
/// The streaming helper behind the "repeated property is damage" rule of the reader groups, ratings and Booru catalog. The stores'
/// own corruption tests only repeat a root property; these pin the helper's scoping, escape, case and depth rules directly so that a
/// later rewrite (one shared name set, case-insensitive names) cannot pass unnoticed.
/// </summary>
public sealed class JsonDuplicatePropertiesTests(ITestOutputHelper log)
{
    private static bool Any(string json, int maxDepth = 64) => JsonDuplicateProperties.Any(Encoding.UTF8.GetBytes(json), maxDepth);

    [Theory]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"a":1,"b":2,"a":3}""")]
    [InlineData("""{"a":{"x":1},"a":{"y":2}}""")]
    [InlineData("""{"a":[1],"a":[2]}""")]
    [InlineData("""{"a":1,"a":1}""")]
    public void ARepeatedNameInTheRootObjectIsFound(string json) => Assert.True(Any(json));

    [Theory]
    [InlineData("""{"e":{"k":1,"k":2}}""")]
    [InlineData("""{"a":{"b":{"c":{"d":1,"d":2}}}}""")]
    [InlineData("""{"list":[{"id":1},{"id":2,"id":3}]}""")]
    [InlineData("""[[{"x":1,"x":2}]]""")]
    [InlineData("""[{"a":1},{"b":1},{"c":1,"c":1}]""")]
    [InlineData("""{"outer":1,"inner":{"outer":1,"outer":2}}""")]
    public void ARepeatedNameInsideANestedObjectOrAnArrayElementIsFound(string json) => Assert.True(Any(json));

    [Theory]
    [InlineData("""{"a":{"x":1},"b":{"x":1}}""")]
    [InlineData("""{"x":{"x":{"x":1}}}""")]
    [InlineData("""{"items":[{"id":1},{"id":2},{"id":3}]}""")]
    [InlineData("""[{"a":1},{"a":2}]""")]
    [InlineData("""{"a":1,"b":{"a":2},"c":[{"a":3},{"a":4}]}""")]
    [InlineData("""{"k":{"a":1},"l":[{"a":2}],"m":{"a":3}}""")]
    public void TheSameNameInDifferentObjectsIsNotARepeat(string json) => Assert.False(Any(json));

    [Fact]
    public void ANameThatRepeatsAfterASiblingObjectClosedIsStillFound()
    {
        // The scope of the first object is gone when it closes; the root's own names must survive it.
        Assert.True(Any("""{"a":{"q":1},"b":2,"a":3}"""));
        Assert.False(Any("""{"a":{"q":1},"b":{"q":2},"c":3}"""));
    }

    [Theory]
    [InlineData("""{"ab":1,"\u0061b":2}""")]
    [InlineData("""{"\u0061\u0062":1,"ab":2}""")]
    [InlineData("""{"a\/b":1,"a/b":2}""")]
    [InlineData("""{"line\nbreak":1,"line\u000abreak":2}""")]
    [InlineData("""{"quote\"":1,"quote\u0022":2}""")]
    [InlineData("""{"\u00e9":1,"é":2}""")]
    [InlineData("""{"\ud83d\ude00":1,"😀":2}""")]
    [InlineData("""{"\\":1,"\u005c":2}""")]
    public void NamesAreComparedAfterUnescaping(string json) => Assert.True(Any(json));

    [Theory]
    [InlineData("""{"a":1,"A":2}""")]
    [InlineData("""{"Entries":[],"entries":[]}""")]
    [InlineData("""{"\u0041":1,"a":2}""")]
    [InlineData("""{"É":1,"é":2}""")]
    [InlineData("""{"ab":1,"a b":2}""")]
    [InlineData("""{"a":1,"a ":2}""")]
    public void NamesDifferingOnlyByCaseOrByAnyCharacterAreDistinct(string json) => Assert.False(Any(json));

    [Fact]
    public void ANamedValueEqualToANameIsNotAName()
    {
        // Only property names count, never string values.
        Assert.False(Any("""{"a":"a","b":"a","c":["a","a"]}"""));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("""{"a":{}}""")]
    [InlineData("1")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[1,2,3]")]
    public void EmptyContainersAndScalarRootsHaveNoRepeat(string json) => Assert.False(Any(json));

    [Fact]
    public void AByteOrderMarkIsSkipped()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        Assert.True(JsonDuplicateProperties.Any(bom.Concat(Encoding.UTF8.GetBytes("""{"a":1,"a":2}""")).ToArray()));
        Assert.False(JsonDuplicateProperties.Any(bom.Concat(Encoding.UTF8.GetBytes("""{"a":1,"b":2}""")).ToArray()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("""{"a":1""")]
    [InlineData("""{"a":1,}""")]
    [InlineData("""{"a" 1}""")]
    [InlineData("""{"a":1}{""")]
    [InlineData("""{"a":1} x""")]
    [InlineData("[1,2")]
    [InlineData("""{"a":tru}""")]
    public void MalformedJsonThrowsAJsonException(string json) => Assert.ThrowsAny<JsonException>(() => Any(json));

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(63)]
    [InlineData(64)]
    public void NestingUpToTheDepthLimitIsRead(int depth)
    {
        Assert.False(Any(Nested(depth, """{"a":1}""")));
        Assert.True(Any(Nested(depth, """{"a":1,"a":2}""")));
    }

    [Theory]
    [InlineData(65)]
    [InlineData(200)]
    [InlineData(100000)]
    public void NestingBeyondTheDepthLimitThrowsAJsonExceptionAndNeverOverflowsTheStack(int depth) =>
        Assert.ThrowsAny<JsonException>(() => Any(Nested(depth, "{}")));

    [Fact]
    public void TheDepthLimitIsTheCallersAndArraysCountAsLevels()
    {
        Assert.ThrowsAny<JsonException>(() => Any("[[[1]]]", maxDepth: 2));
        Assert.False(Any("[[[1]]]", maxDepth: 3));
        Assert.ThrowsAny<JsonException>(() => Any("""{"a":{"b":{"c":1}}}""", maxDepth: 2));
        Assert.False(Any("""{"a":{"b":{"c":1}}}""", maxDepth: 3));
    }

    // <paramref name="levels"/> containers in all (the last one is <paramref name="inner"/>, itself a container); objects and arrays alternate.
    private static string Nested(int depth, string inner)
    {
        StringBuilder text = new();
        for (int level = 0; level < depth - 1; level++) text.Append(level % 2 == 0 ? "{\"k\":" : "[");
        text.Append(inner);
        for (int level = depth - 2; level >= 0; level--) text.Append(level % 2 == 0 ? "}" : "]");
        return text.ToString();
    }

    // A large document: bounded by memory (no tree is built, one name set per open object) and by completing at all.
    // No timing assertion: the elapsed time is only written to the test log.
    [Fact]
    public void ALargeDocumentIsScannedWithoutBuildingATree()
    {
        const int objects = 150_000;
        StringBuilder text = new("{\"entries\":[");
        for (int index = 0; index < objects; index++)
        {
            if (index > 0) text.Append(',');
            text.Append("{\"key\":{\"thread\":{\"source\":\"s\",\"board\":\"b\",\"thread\":\"").Append(index).Append("\"},\"media\":\"m").Append(index)
                .Append("\"},\"value\":").Append(index % 5 + 1).Append('}');
        }
        string clean = text.Append("]}").ToString();
        byte[] bytes = Encoding.UTF8.GetBytes(clean);
        // The same document with one repeat inside the very last entry.
        byte[] repeated = Encoding.UTF8.GetBytes(clean.Replace("\"value\":" + ((objects - 1) % 5 + 1) + "}]}", "\"value\":1,\"value\":2}]}", StringComparison.Ordinal));
        Assert.NotEqual(bytes.Length, repeated.Length);
        Assert.True(bytes.Length > 10 * 1024 * 1024, $"The fixture is only {bytes.Length} bytes.");

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool cleanResult = JsonDuplicateProperties.Any(bytes);
        long cleanAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long middle = GC.GetAllocatedBytesForCurrentThread();
        bool repeatedResult = JsonDuplicateProperties.Any(repeated);
        long repeatedAllocated = GC.GetAllocatedBytesForCurrentThread() - middle;
        watch.Stop();

        log.WriteLine($"{bytes.Length} bytes, {objects} entries: {watch.ElapsedMilliseconds} ms for two scans, allocated {cleanAllocated} / {repeatedAllocated} bytes.");
        Assert.False(cleanResult);
        Assert.True(repeatedResult);
        // Generous: about 28 allocations of a few dozen bytes per entry stay far below a tree of the document (which costs
        // several times the file) and below 64 bytes per input byte, however the runtime sizes its sets.
        Assert.True(cleanAllocated < 64L * bytes.Length, $"A clean scan allocated {cleanAllocated} bytes for {bytes.Length} input bytes.");
        Assert.True(repeatedAllocated < 64L * bytes.Length, $"A scan that stops at a repeat allocated {repeatedAllocated} bytes.");
    }

    [Fact]
    public void OneObjectWithManyDistinctNamesIsScannedAndItsRepeatIsFoundAtTheEnd()
    {
        const int names = 200_000;
        StringBuilder text = new("{");
        for (int index = 0; index < names; index++) text.Append(index > 0 ? "," : "").Append("\"n").Append(index).Append("\":").Append(index);
        string clean = text.Append('}').ToString();
        Assert.False(Any(clean));
        Assert.True(Any(clean[..^1] + ",\"n0\":1}"));
        Assert.False(Any(clean[..^1] + ",\"N0\":1}"));
    }

    [Fact]
    public void AHugeArrayOfScalarsAllocatesNothingPerElement()
    {
        StringBuilder text = new("[");
        for (int index = 0; index < 1_000_000; index++) text.Append(index > 0 ? "," : "").Append(index);
        byte[] bytes = Encoding.UTF8.GetBytes(text.Append(']').ToString());
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(JsonDuplicateProperties.Any(bytes));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        log.WriteLine($"{bytes.Length} bytes of scalars: allocated {allocated} bytes.");
        Assert.True(allocated < 1024 * 1024, $"Scalars allocated {allocated} bytes.");
    }
}
