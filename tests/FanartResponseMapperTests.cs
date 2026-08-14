using System.Security.Cryptography;
using System.Text;
using Shoko.Abstractions.Metadata.Enums;
using Xunit;

namespace Shoko.ImagePlanner.Tests;

public sealed class FanartResponseMapperTests
{
    private static ProviderCandidate? MapItem(string itemJson)
    {
        var json = Encoding.UTF8.GetBytes($$"""{"tvposter":[{{itemJson}}]}""");
        return FanartResponseMapper.Map(json, "tv", "123", 10).SingleOrDefault();
    }

    private static ProviderCandidate MappedItem(string itemJson) => Assert.IsType<ProviderCandidate>(MapItem(itemJson));

    [Fact]
    public void MapsRealFanartTvResponseWithStringLikesWithoutThrowing()
    {
        // Fanart.tv v3 returns image items where "likes" is a JSON string and
        // dimensions only exist as the "size" text ("WxH"); there is no numeric
        // width/height. The mapper must not throw and must keep the candidates.
        var json = Encoding.UTF8.GetBytes("""
            {
              "name": "Game of Thrones",
              "thetvdb_id": "121361",
              "tvposter": [
                { "id": "12345", "url": "https://assets.fanart.tv/fanart/tv/121361/tvposter/game-of-thrones-5a857a2f2f8ad.jpg", "lang": "en", "likes": "12", "size": "1000x1500" }
              ],
              "showbackground": [
                { "id": "67890", "url": "https://assets.fanart.tv/fanart/tv/121361/showbackground/game-of-thrones-5a857a2f2f8ad.jpg", "lang": "en", "likes": "3", "size": "1920x1080" }
              ]
            }
            """);

        var candidates = FanartResponseMapper.Map(json, "tv", "121361", 10);

        Assert.Equal(2, candidates.Count);
        var poster = Assert.Single(candidates, item => item.ImageType == ImageEntityType.Primary);
        Assert.Equal(12, poster.RatingVotes);
        Assert.Equal("en", poster.LanguageCode);
        Assert.Equal("tv:121361:tvposter:12345", poster.ResourceId);
        // Real responses carry only the "size" text; dimensions fall back to null.
        Assert.Null(poster.Width);
        Assert.Null(poster.Height);
        var background = Assert.Single(candidates, item => item.ImageType == ImageEntityType.Backdrop);
        Assert.Equal(3, background.RatingVotes);
    }

    [Fact]
    public void MapsDimensionsAndLikesFromStringFields()
    {
        var item = MappedItem("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","lang":"en","likes":"42","width":"1000","height":"1500"}""");
        Assert.Equal(1000, item.Width);
        Assert.Equal(1500, item.Height);
        Assert.Equal(42, item.RatingVotes);
    }

    [Fact]
    public void MapsMixedNumericAndStringFields()
    {
        var item = MappedItem("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":7,"width":1000,"height":"1500"}""");
        Assert.Equal(1000, item.Width);
        Assert.Equal(1500, item.Height);
        Assert.Equal(7, item.RatingVotes);
    }

    [Theory]
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":5}""", 5)]
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":"5"}""", 5)]
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":"020"}""", 20)]
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":"5.0"}""", 5)]
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":2147483647}""", int.MaxValue)]
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":"2147483647"}""", int.MaxValue)]
    public void AcceptsPositiveInt32FromJsonNumbersAndDecimalStrings(string itemJson, int expected)
        => Assert.Equal(expected, MappedItem(itemJson).RatingVotes);

    [Theory]
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg"}""")]                                  // missing
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":null}""")]                     // JSON null
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":0}""")]                        // zero
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":-3}""")]                       // negative
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":"0"}""")]                      // zero string
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":"-7"}""")]                     // negative string
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":2147483648}""")]               // Int32 overflow number
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":"2147483648"}""")]             // Int32 overflow string
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":5.5}""")]                      // non-integer number
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":"5.5"}""")]                    // non-integer string
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":""}""")]                       // empty string
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":"abc"}""")]                    // malformed string
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":"1e3"}""")]                    // scientific notation
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":"0x10"}""")]                   // hex notation
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":"   "}""")]                    // whitespace only
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":true}""")]                     // boolean
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":[1]}""")]                      // array
    [InlineData("""{"id":"1","url":"https://assets.fanart.tv/a.jpg","likes":{"value":1}}""")]              // object
    public void ReturnsNullForMissingInvalidOrNonIntegerLikes(string itemJson)
    {
        var item = MappedItem(itemJson);
        Assert.Null(item.RatingVotes);
        Assert.Null(item.Width);
        Assert.Null(item.Height);
    }

    [Theory]
    [InlineData("""{"id":"1","url":123}""")]
    [InlineData("""{"id":"1","url":null}""")]
    [InlineData("""{"id":"1","url":true}""")]
    [InlineData("""{"id":"1","url":["https://assets.fanart.tv/a.jpg"]}""")]
    [InlineData("""{"id":"1","url":{"href":"https://assets.fanart.tv/a.jpg"}}""")]
    public void SkipsItemsWithNonStringUrlsWithoutThrowing(string itemJson)
    {
        var json = Encoding.UTF8.GetBytes($$"""{"tvposter":[{{itemJson}}]}""");
        Assert.Empty(FanartResponseMapper.Map(json, "tv", "123", 10));
    }

    [Fact]
    public void FallsBackToUrlHashForNonStringIdsAndDropsNonStringLanguage()
    {
        var item = MappedItem("""{"id":123,"url":"https://assets.fanart.tv/a.jpg","lang":5,"likes":2}""");
        Assert.Equal(2, item.RatingVotes);
        Assert.Null(item.LanguageCode);
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("https://assets.fanart.tv/a.jpg"))).ToLowerInvariant();
        Assert.Equal($"tv:123:tvposter:{expectedHash}", item.ResourceId);
    }
}
