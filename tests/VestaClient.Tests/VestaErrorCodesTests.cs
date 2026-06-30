using VestaClient;

namespace VestaClient.Tests;

/// <summary>
/// Tests for <see cref="VestaErrorCodes.Classify"/> — the client's policy mapping server error
/// codes to limit / retry semantics.
/// </summary>
public sealed class VestaErrorCodesTests
{
    [Fact]
    public void Classify_RateLimited_IsTransientLimit_NotFatal()
    {
        VestaErrorCodes.Classification c = VestaErrorCodes.Classify(VestaErrorCodes.RateLimited);

        Assert.True(c.IsLimit);
        Assert.True(c.IsTransient);
        Assert.False(c.IsEventFatal);
    }

    [Theory]
    [InlineData("QUOTA_EXCEEDED")]
    [InlineData("UNKNOWN_APP")]
    [InlineData("ACCESS_DENIED")]
    [InlineData("APP_NOT_ALLOWED")]
    public void Classify_PermanentLimits_AreFatalAndNonTransient(string code)
    {
        VestaErrorCodes.Classification c = VestaErrorCodes.Classify(code);

        Assert.True(c.IsLimit);
        Assert.False(c.IsTransient);
        Assert.True(c.IsEventFatal);
    }

    [Theory]
    [InlineData("INVALID_CHANNEL")]
    [InlineData("INVALID_SIGNATURE")]
    [InlineData("CLIENT_ID_MISMATCH")]
    [InlineData("PROTOCOL_NAMESPACE_RESERVED")]
    [InlineData("CHANNEL_DELETED")]
    public void Classify_DoomedClientErrors_AreFatalButNotLimits(string code)
    {
        VestaErrorCodes.Classification c = VestaErrorCodes.Classify(code);

        Assert.False(c.IsLimit);
        Assert.True(c.IsEventFatal);
    }

    [Fact]
    public void Classify_UnknownCode_IsConservative_NotFatalNotLimit()
    {
        VestaErrorCodes.Classification c = VestaErrorCodes.Classify("SOMETHING_NEW");

        Assert.False(c.IsLimit);
        Assert.False(c.IsEventFatal);
        Assert.True(c.IsTransient);
    }
}
