using Factory.Core;

namespace Factory.UnitTests;

public class ProductTextTests
{
    private static string Product(int features, string home = "- Home: redirect: /books") =>
        "# Product: Booklist\n\n## Purpose\nTrack books.\n\n## Users\nReaders.\n\n## Home\n" + home + "\n\n## Features\n"
        + string.Concat(Enumerable.Range(1, features).Select(k => $"### Feature {k}\nWhat feature {k} does.\nMore of it.\n\n"));

    [Fact]
    public void Parse_reads_the_title_the_home_line_and_the_features_in_order()
    {
        var p = ProductText.Parse(Product(2));

        Assert.Equal("Booklist", p.Title);
        Assert.Equal("- Home: redirect: /books", p.Home);
        Assert.Equal([("Feature 1", "What feature 1 does.\nMore of it."), ("Feature 2", "What feature 2 does.\nMore of it.")],
            p.Features.Select(f => (f.Heading, f.Text)));
    }

    [Fact]
    public void Parse_ignores_level_three_headings_outside_features()
    {
        var p = ProductText.Parse("# Product: X\n## Purpose\n### not a feature\n## Home\n- Home: keep: fine\n## Features\n### Only one\nIt.\n## Notes\n### also not\n");

        Assert.Equal(["Only one"], p.Features.Select(f => f.Heading));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void Error_is_null_for_1_to_10_features_and_a_home_decision(int n) => Assert.Null(ProductText.Error(Product(n)));

    [Theory]
    [InlineData(0, "product refused: list 1 to 10 features, each a ### heading under ## Features (it has 0)")]
    [InlineData(11, "product refused: list 1 to 10 features, each a ### heading under ## Features (it has 11)")]
    public void Error_refuses_too_few_or_too_many_features(int n, string why) => Assert.Equal(why, ProductText.Error(Product(n)));

    [Theory]
    [InlineData("")]
    [InlineData("- Home: maybe later")]
    [InlineData("Redirect to /books")]
    public void Error_refuses_a_missing_home_decision(string home) =>
        Assert.Equal("product refused: ## Home needs a '- Home: replace|redirect|keep: <detail>' line", ProductText.Error(Product(1, home)));

    [Fact]
    public void Error_refuses_a_duplicate_heading() =>
        Assert.Equal("product refused: feature 'a' is listed twice",
            ProductText.Error("# Product: X\n## Home\n- Home: keep: ok\n## Features\n### A\nx\n### a \ny\n"));

    [Fact]
    public void Error_refuses_no_title() =>
        Assert.Equal("product refused: the first line must be '# Product: <app name>'", ProductText.Error("## Home\n- Home: keep: ok\n## Features\n### A\nx\n"));

    [Fact]
    public void Same_heading_ignores_case_and_surrounding_space()
    {
        Assert.True(ProductText.SameHeading(" Borrow a Book ", "borrow a book"));
        Assert.False(ProductText.SameHeading("Borrow", "Borrow a book"));
    }
}
