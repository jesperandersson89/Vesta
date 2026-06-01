using VestaCore.Channels;

namespace VestaCore.Tests.Channels;

public class AppIdTests
{
  [Theory]
  [InlineData("chat")]
  [InlineData("my-app")]
  [InlineData("game42")]
  [InlineData("a")]
  [InlineData("ab")]
  public void IsValid_AcceptsValidIds(string id)
  {
    Assert.True(AppId.IsValid(id));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("-bad")]
  [InlineData("bad-")]
  [InlineData("with/slash")]
  [InlineData("UPPER")]
  [InlineData("double--dash")]
  public void IsValid_RejectsInvalidIds(string? id)
  {
    Assert.False(AppId.IsValid(id));
  }

  [Fact]
  public void IsValid_RejectsTooLong()
  {
    Assert.False(AppId.IsValid(new string('a', AppId.MaxLength + 1)));
  }

  [Theory]
  [InlineData("myapp/chat", "myapp")]
  [InlineData("myapp", "myapp")]
  [InlineData("myapp/chat/general", "myapp")]
  [InlineData("", null)]
  [InlineData(null, null)]
  public void ExtractFromChannelId_ReturnsFirstSegment(string? channelId, string? expected)
  {
    Assert.Equal(expected, AppId.ExtractFromChannelId(channelId));
  }
}
