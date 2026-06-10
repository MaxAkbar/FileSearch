using FileSearch.Core.Indexing;

namespace FileSearch.Core.Tests;

public sealed class SqlFormatTests
{
    [Fact]
    public void QuotesAndEscapesStringValues()
    {
        var hostile = "O'Brien; -- 'x'";

        var sql = Sql.Format($"name = {hostile}");

        Assert.Equal("name = 'O''Brien; -- ''x'''", sql);
    }

    [Fact]
    public void RendersNullStringsAsSqlNull()
    {
        string? error = null;

        Assert.Equal("error = NULL", Sql.Format($"error = {error}"));
    }

    [Fact]
    public void RendersNumbersAsInvariantDigits()
    {
        Assert.Equal("id = 9223372036854775807", Sql.Format($"id = {long.MaxValue}"));
        Assert.Equal("kind = -42", Sql.Format($"kind = {-42}"));
    }

    [Fact]
    public void JoinsIdLists()
    {
        var ids = new Sql.IdList(new long[] { 1, 2, 3 });

        Assert.Equal("id IN (1,2,3)", Sql.Format($"id IN ({ids})"));
    }

    [Fact]
    public void RendersEmptyIdListsAsMatchNothing()
    {
        var ids = new Sql.IdList(Array.Empty<long>());

        Assert.Equal("id IN (NULL)", Sql.Format($"id IN ({ids})"));
    }

    [Fact]
    public void AppendsValidIdentifiersRaw()
    {
        var table = new Sql.Identifier("pending_changes");

        Assert.Equal("SELECT MAX(id) FROM pending_changes", Sql.Format($"SELECT MAX(id) FROM {table}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("files; DROP TABLE files")]
    [InlineData("files--")]
    [InlineData("fi les")]
    [InlineData("files'")]
    public void RejectsHostileIdentifiers(string name)
    {
        Assert.Throws<ArgumentException>(() => new Sql.Identifier(name));
    }
}
