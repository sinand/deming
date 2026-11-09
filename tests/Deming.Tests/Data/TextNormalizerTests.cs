using Deming.Data;

namespace Deming.Tests.Data;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("DRIFT", "drift")]
    [InlineData("köprü", "kopru")]
    [InlineData("KÖPRÜ", "kopru")]
    [InlineData("İstanbul", "istanbul")]
    [InlineData("ISTANBUL", "istanbul")]
    [InlineData("ıstanbul", "istanbul")]
    [InlineData("Çağrı Şeyma", "cagri seyma")]
    [InlineData("  many   spaces\there \n", "many spaces here")]
    [InlineData("Café crème", "cafe creme")]
    public void Folds_case_diacritics_turkish_i_and_whitespace(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void Turkish_spellings_collide() =>
        Assert.Equal(TextNormalizer.Normalize("Köprü İşi"), TextNormalizer.Normalize("kopru isi"));

    [Fact]
    public async Task Sql_function_matches_csharp()
    {
        await using var t = await TempDatabase.CreateAsync(migrate: false);
        var folded = await t.ScalarAsync("SELECT deming_normalize('Köprü İŞİ'), deming_normalize(NULL) IS NULL;");
        Assert.Equal("kopru isi", folded);
        Assert.Equal(1L, await t.ScalarAsync("SELECT deming_normalize(NULL) IS NULL;"));
    }
}
