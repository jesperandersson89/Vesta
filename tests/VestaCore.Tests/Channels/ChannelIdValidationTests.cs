using VestaCore.Channels;

namespace VestaCore.Tests.Channels;

public class ChannelIdValidationTests
{
    [Theory]
    [InlineData("chat")]
    [InlineData("my-todo-list")]
    [InlineData("myapp/chat/general")]
    [InlineData("game-room-42")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("a1")]
    [InlineData("my-app/todo-list")]
    [InlineData("0")]
    [InlineData("123")]
    public void IsValid_ValidChannelIds_ReturnsTrue(string channelId)
    {
        Assert.True(ChannelId.IsValid(channelId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-starts-with-hyphen")]
    [InlineData("ends-with-hyphen-")]
    [InlineData("/starts-with-slash")]
    [InlineData("ends-with-slash/")]
    [InlineData("UPPERCASE")]
    [InlineData("Mixed-Case")]
    [InlineData("has spaces")]
    [InlineData("has_underscore")]
    [InlineData("has.dot")]
    [InlineData("double--hyphen")]
    [InlineData("double//slash")]
    [InlineData("special!char")]
    [InlineData("emoji-😀")]
    public void IsValid_InvalidChannelIds_ReturnsFalse(string? channelId)
    {
        Assert.False(ChannelId.IsValid(channelId));
    }

    [Fact]
    public void IsValid_MaxLength_ReturnsTrue()
    {
        // 128 chars: starts with 'a', filled with 'b', ends with 'c'
        string channelId = "a" + new string('b', 126) + "c";
        Assert.Equal(128, channelId.Length);
        Assert.True(ChannelId.IsValid(channelId));
    }

    [Fact]
    public void IsValid_ExceedsMaxLength_ReturnsFalse()
    {
        string channelId = "a" + new string('b', 127) + "c";
        Assert.Equal(129, channelId.Length);
        Assert.False(ChannelId.IsValid(channelId));
    }

    [Fact]
    public void Validate_ValidChannelId_ReturnsValue()
    {
        string result = ChannelId.Validate("my-todo-list");
        Assert.Equal("my-todo-list", result);
    }

    [Fact]
    public void Validate_InvalidChannelId_ThrowsArgumentException()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => ChannelId.Validate("INVALID!"));
        Assert.Contains("Invalid channel ID", ex.Message);
    }

    [Fact]
    public void Validate_Null_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ChannelId.Validate(null));
    }

    [Theory]
    [InlineData("app/chat/room1")]
    [InlineData("org/team/project/events")]
    public void IsValid_NestedSlashes_Allowed(string channelId)
    {
        Assert.True(ChannelId.IsValid(channelId));
    }
}
