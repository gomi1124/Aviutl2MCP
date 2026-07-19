using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Diagnostics;

public sealed record PreviewPixelDifference(
    bool IsDifferent,
    bool AreDimensionsDifferent,
    int BeforeWidth,
    int BeforeHeight,
    int AfterWidth,
    int AfterHeight,
    long? ChangedPixelCount,
    double? ChangedPixelRatio,
    int? MaximumChannelDelta,
    double? MeanAbsoluteChannelDelta,
    string BeforePixelSha256,
    string AfterPixelSha256);

public sealed record BeforeAfterVerification(
    Revision BeforeRevision,
    Revision AfterRevision,
    bool IsRevisionChanged,
    PreviewPixelDifference Preview);

public static class BeforeAfterVerifier
{
    public static BeforeAfterVerification Verify(
        Revision beforeRevision,
        Revision afterRevision,
        ReadOnlyMemory<byte> beforePng,
        ReadOnlyMemory<byte> afterPng)
    {
        DecodedRgbaImage before = RgbaPngDecoder.Decode(beforePng.Span);
        DecodedRgbaImage after = RgbaPngDecoder.Decode(afterPng.Span);
        PreviewPixelDifference preview = ComparePixels(before, after);
        return new BeforeAfterVerification(
            beforeRevision,
            afterRevision,
            beforeRevision != afterRevision,
            preview);
    }

    private static PreviewPixelDifference ComparePixels(
        DecodedRgbaImage before,
        DecodedRgbaImage after)
    {
        string beforeHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(before.Pixels));
        string afterHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(after.Pixels));
        bool areDimensionsDifferent = before.Width != after.Width || before.Height != after.Height;
        if (areDimensionsDifferent)
        {
            return new PreviewPixelDifference(
                true,
                true,
                before.Width,
                before.Height,
                after.Width,
                after.Height,
                null,
                null,
                null,
                null,
                beforeHash,
                afterHash);
        }

        long changedPixelCount = 0;
        int maximumChannelDelta = 0;
        ulong absoluteChannelDelta = 0;
        for (int pixelOffset = 0; pixelOffset < before.Pixels.Length; pixelOffset += 4)
        {
            bool isPixelChanged = false;
            for (int channel = 0; channel < 4; channel++)
            {
                int delta = Math.Abs(before.Pixels[pixelOffset + channel] - after.Pixels[pixelOffset + channel]);
                absoluteChannelDelta += (uint)delta;
                maximumChannelDelta = Math.Max(maximumChannelDelta, delta);
                isPixelChanged |= delta != 0;
            }
            if (isPixelChanged)
            {
                changedPixelCount++;
            }
        }

        long pixelCount = checked((long)before.Width * before.Height);
        return new PreviewPixelDifference(
            changedPixelCount != 0,
            false,
            before.Width,
            before.Height,
            after.Width,
            after.Height,
            changedPixelCount,
            changedPixelCount / (double)pixelCount,
            maximumChannelDelta,
            absoluteChannelDelta / (pixelCount * 4.0),
            beforeHash,
            afterHash);
    }
}
