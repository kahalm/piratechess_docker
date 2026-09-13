using PirateChess.Api.Models.DTOs;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class BrowserCourseAssemblerTests
{
    private const string Line = "{\"game\":{\"data\":[{\"san\":\"e4\"}]}}";

    [Fact]
    public void Align_NothingMissing_KeepsChapterJsonByteForByte()
    {
        const string chapter = "{\"list\":{\"name\":\"Ä\",\"data\":[{\"id\":1,\"name\":\"a\"},{\"id\":2,\"name\":\"b\"}]}}";
        var aligned = BrowserCourseAssembler.Align(
            new DirectParseChapter(chapter, [Line, Line], ["1", "2"]), new Dictionary<int, string>());

        Assert.Equal(chapter, aligned.Chapter.ChapterJsonContent);
        Assert.Equal(new[] { 1, 2 }, aligned.Chapter.ResponseLineList.Select(l => l.Oid));
        Assert.Equal((2, 0, 0), (aligned.Provided, aligned.FromCache, aligned.Missing));
    }

    [Fact]
    public void Align_UsesCacheForGaps_PrunesMissing_KeepsListOrder()
    {
        const string chapter = "{\"list\":{\"data\":[{\"id\":1},{\"id\":2},{\"id\":3}]}}";
        var aligned = BrowserCourseAssembler.Align(
            new DirectParseChapter(chapter, [Line, null], ["3", "1"]),
            new Dictionary<int, string> { [1] = "cached-1" });

        Assert.Equal(new[] { 1, 3 }, aligned.Chapter.ResponseLineList.Select(l => l.Oid));
        Assert.Equal("cached-1", aligned.Chapter.ResponseLineList[0].LineJsonContent);
        Assert.Equal((1, 1, 1), (aligned.Provided, aligned.FromCache, aligned.Missing));
        Assert.Contains("{\"id\":1},{\"id\":3}", aligned.Chapter.ChapterJsonContent);
        Assert.DoesNotContain("\"id\":2", aligned.Chapter.ChapterJsonContent);
    }

    [Fact]
    public void Align_ReadsPropertyNamesCaseInsensitive_AndStringIds()
    {
        const string chapter = "{\"List\":{\"Data\":[{\"Id\":\"7\"}]}}";
        var aligned = BrowserCourseAssembler.Align(new DirectParseChapter(chapter, [Line], ["7"]), new Dictionary<int, string>());
        Assert.Equal(7, Assert.Single(aligned.Chapter.ResponseLineList).Oid);
    }

    [Theory]
    [InlineData("{\"game\":{}}", true)]
    [InlineData("{\"Game\":{\"data\":[]}}", true)]
    [InlineData("{}", false)]
    [InlineData("", false)]
    [InlineData("{\"x\":1}", false)]
    [InlineData("{\"game\":{\"data\":[", false)]
    public void IsCacheableLine(string content, bool expected)
        => Assert.Equal(expected, BrowserCourseAssembler.IsCacheableLine(content));

    [Fact]
    public void CourseChapterCount_ReadsCourseData_OrNull()
    {
        Assert.Equal(2, BrowserCourseAssembler.CourseChapterCount("{\"course\":{\"data\":[{\"id\":1},{\"id\":2}]}}"));
        Assert.Null(BrowserCourseAssembler.CourseChapterCount("{\"nope\":1}"));
        Assert.Null(BrowserCourseAssembler.CourseChapterCount("kaputt"));
    }

    [Fact]
    public void Validate_AcceptsLegacyAndWellFormedOids()
    {
        Assert.Null(BrowserCourseAssembler.Validate([new DirectParseChapter("{}", [Line], null)]));
        Assert.Null(BrowserCourseAssembler.Validate([new DirectParseChapter("{}", [Line, null], ["1", "2"])]));
    }
}
