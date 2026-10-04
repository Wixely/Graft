using System.Buffers.Binary;

namespace Graft.Core;

/// <summary>A validated raster image, with the content type the server will serve it back as.</summary>
public sealed record ValidatedImage(string ContentType, int Width, int Height, byte[] Bytes);

/// <summary>
/// Structural validation for uploaded images.
///
/// <para>
/// This parses the container to confirm the bytes really are the format they claim, and reads the declared
/// dimensions so an absurd pixel count is refused before anything stores it. It deliberately does not decode
/// pixels or re-encode: that would mean taking an image library as a dependency, and the one in common use
/// for .NET carries a split licence with commercial terms. What it does instead is refuse anything it cannot
/// positively identify, which keeps the accepted set small and known.
/// </para>
///
/// <para>
/// The practical risk for an avatar is a file that a browser decides to treat as markup or script. That is
/// handled by accepting only these raster formats, never SVG, and by serving the stored bytes with the exact
/// content type below plus <c>X-Content-Type-Options: nosniff</c>. Validating structure is not the same as
/// proving an image is safe to decode; it bounds what reaches a decoder rather than certifying the content.
/// </para>
/// </summary>
public static class ImageContent
{
    /// <summary>Largest accepted pixel dimension on either axis.</summary>
    public const int MaximumDimension = 4096;

    /// <summary>Largest accepted encoded size.</summary>
    public const int MaximumBytes = 2 * 1024 * 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Returns the validated image, or null when the bytes are not a supported image. Never throws for
    /// malformed input: callers treat null as a refusal.
    /// </summary>
    public static ValidatedImage? Validate(byte[] bytes)
    {
        if (bytes is null || bytes.Length is 0 or > MaximumBytes) return null;
        ValidatedImage? image = ReadPng(bytes) ?? ReadGif(bytes) ?? ReadJpeg(bytes) ?? ReadWebp(bytes);
        if (image is null) return null;
        return image.Width is > 0 and <= MaximumDimension && image.Height is > 0 and <= MaximumDimension ? image : null;
    }

    private static ValidatedImage? ReadPng(byte[] bytes)
    {
        // Signature, then an IHDR chunk whose length is fixed at 13 bytes and which must come first.
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(PngSignature)) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13) return null;
        if (!bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return null;
        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        return new("image/png", width, height, bytes);
    }

    private static ValidatedImage? ReadGif(byte[] bytes)
    {
        if (bytes.Length < 10) return null;
        if (!bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8) && !bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8)) return null;
        return new("image/gif",
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2)), bytes);
    }

    private static ValidatedImage? ReadWebp(byte[] bytes)
    {
        // RIFF container with a WEBP form type. Only the simple lossy and lossless chunks are accepted;
        // extended WebP can carry animation and alpha chunks that are not worth parsing for an avatar.
        if (bytes.Length < 30 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return null;
        if (bytes.AsSpan(12, 4).SequenceEqual("VP8 "u8))
        {
            // Key frame header: a three byte start code then two 14 bit dimensions.
            if (bytes.Length < 30 || bytes[23] != 0x9D || bytes[24] != 0x01 || bytes[25] != 0x2A) return null;
            return new("image/webp",
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2)) & 0x3FFF,
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)) & 0x3FFF, bytes);
        }
        if (bytes.AsSpan(12, 4).SequenceEqual("VP8L"u8))
        {
            if (bytes.Length < 25 || bytes[20] != 0x2F) return null;
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(21, 4));
            return new("image/webp", (int)(packed & 0x3FFF) + 1, (int)((packed >> 14) & 0x3FFF) + 1, bytes);
        }
        return null;
    }

    private static ValidatedImage? ReadJpeg(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
        int index = 2;
        while (index + 3 < bytes.Length)
        {
            if (bytes[index] != 0xFF) return null;
            byte marker = bytes[index + 1];
            index += 2;
            // Standalone markers carry no payload.
            if (marker == 0xFF) { index--; continue; }
            if (marker is 0x01 or >= 0xD0 and <= 0xD9) continue;
            if (index + 1 >= bytes.Length) return null;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index, 2));
            if (length < 2 || index + length > bytes.Length) return null;
            // Any start-of-frame marker but the arithmetic and hierarchical ones carries the dimensions.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 7) return null;
                return new("image/jpeg",
                    BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index + 5, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index + 3, 2)), bytes);
            }
            // Entropy-coded data follows the start of scan, so dimensions must have appeared already.
            if (marker == 0xDA) return null;
            index += length;
        }
        return null;
    }
}

/// <summary>Which kind of thing an avatar belongs to.</summary>
public enum AvatarOwner
{
    User,
    Project
}

/// <summary>A stored avatar, with the digest used as its cache validator.</summary>
public sealed record StoredAvatar(string ContentType, byte[] Bytes, string Sha256);
