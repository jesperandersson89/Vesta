namespace VestaCore.Utilities;

/// <summary>
/// Base64Url encoding/decoding utilities (RFC 4648 §5).
/// URL-safe variant: uses - instead of +, _ instead of /, no padding.
/// </summary>
public static class Base64Url
{
    public static string Encode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static string Encode(ReadOnlySpan<byte> data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static byte[] Decode(string base64Url)
    {
        string base64 = base64Url
            .Replace('-', '+')
            .Replace('_', '/');

        int padding = (4 - base64.Length % 4) % 4;
        base64 += new string('=', padding);

        return Convert.FromBase64String(base64);
    }
}
