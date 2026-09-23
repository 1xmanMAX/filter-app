using FilterApp.Core;

namespace FilterApp.Core.Tests;

public class NameResolverTests
{
    const string Dir = @"C:\dest";
    static readonly Func<string, bool> Nothing = _ => false;

    [Fact]
    public void Uses_card_name_and_original_extension() =>
        Assert.Equal(Path.Combine(Dir, "Factura Enero.jpg"), NameResolver.Resolve(Dir, "Factura Enero", "foto.jpg", Nothing));

    [Fact]
    public void Keeps_extension_case() =>
        Assert.Equal(Path.Combine(Dir, "X.JPG"), NameResolver.Resolve(Dir, "X", "IMG_1.JPG", Nothing));

    [Fact]
    public void File_without_extension_gets_none() =>
        Assert.Equal(Path.Combine(Dir, "X"), NameResolver.Resolve(Dir, "X", "README", Nothing));

    [Fact]
    public void Adds_counter_when_name_is_taken()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Dir, "X.pdf"), Path.Combine(Dir, "X (2).pdf"),
        };
        Assert.Equal(Path.Combine(Dir, "X (3).pdf"), NameResolver.Resolve(Dir, "X", "a.pdf", taken.Contains));
    }

    [Theory]
    [InlineData("Contrato/Juan", "Contrato_Juan")]
    [InlineData("a:b*c?d", "a_b_c_d")]
    [InlineData("  Nombre  ", "Nombre")]
    [InlineData("Nombre. . ", "Nombre")]
    [InlineData("...", "_")]
    [InlineData("   ", "_")]
    [InlineData("CON", "_CON")]
    [InlineData("com1", "_com1")]
    public void Sanitize_produces_a_valid_windows_name(string input, string expected) =>
        Assert.Equal(expected, NameResolver.Sanitize(input));
}
