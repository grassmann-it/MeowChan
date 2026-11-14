using System.Globalization;
using System.Text;
using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Only the new navigation-failure key: embedded resources and formatting, no UI,
// profile, provider or localization-service state. Not a human linguistic review.
public sealed class BooruNavigationFailurePackTests
{
    private const string Key = "booru.crossLinks.navigationFailed";

    [Theory]
    [InlineData("cs", "Příspěvek #{0} se nepodařilo zobrazit v sekci Média. Zkuste to znovu.")]
    [InlineData("es", "No se pudo mostrar la publicación #{0} en Medios. Inténtalo de nuevo.")]
    [InlineData("fr", "Impossible d’afficher la publication #{0} dans Médias. Réessayez.")]
    [InlineData("it", "Impossibile mostrare il post #{0} in Media. Riprova.")]
    [InlineData("ja", "投稿 #{0} をメディアで表示できませんでした。もう一度お試しください。")]
    [InlineData("ko", "게시물 #{0}을(를) 미디어에 표시하지 못했습니다. 다시 시도하세요.")]
    [InlineData("pl", "Nie udało się wyświetlić postu #{0} w sekcji Media. Spróbuj ponownie.")]
    [InlineData("pt-BR", "Não foi possível mostrar a postagem #{0} em Mídia. Tente novamente.")]
    [InlineData("ru", "Не удалось показать публикацию #{0} в разделе «Медиа». Попробуйте ещё раз.")]
    [InlineData("zh-Hans", "无法在媒体中显示帖子 #{0}。请重试。")]
    public void EmbeddedNavigationFailureHasTranslatedCopyAndExactlyOneUnchangedPostPlaceholder(string code, string expected)
    {
        string resource = "MeowChan.Desktop.Localization.Packs." + code + ".json";
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Required embedded pack missing: " + resource);
        using JsonDocument document = JsonDocument.Parse(stream);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        JsonProperty entry = Assert.Single(document.RootElement.GetProperty("strings").EnumerateObject()
            .Where(property => property.NameEquals(Key)));
        Assert.Equal(JsonValueKind.String, entry.Value.ValueKind);
        string value = entry.Value.GetString()!;
        Assert.Equal(expected, value);
        Assert.NotEqual("Could not show post #{0} in Media. Try again.", value);
        Assert.NotEqual("Post #{0} konnte nicht in Medien angezeigt werden. Versuche es erneut.", value);

        Assert.Equal(1, CompositeFormat.Parse(value).MinimumArgumentCount);
        int placeholder = value.IndexOf("{0}", StringComparison.Ordinal);
        Assert.True(placeholder >= 0, "The exact post placeholder {0} must remain present.");
        string withoutPlaceholder = value.Remove(placeholder, "{0}".Length);
        Assert.DoesNotContain("{", withoutPlaceholder);
        Assert.DoesNotContain("}", withoutPlaceholder);
        string formatted = string.Format(CultureInfo.GetCultureInfo(code), value, "123456");
        Assert.Equal(expected.Replace("{0}", "123456", StringComparison.Ordinal), formatted);
    }
}
