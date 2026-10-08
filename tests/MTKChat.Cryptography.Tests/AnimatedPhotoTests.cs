using System.Text;
using MTKChat.Server.Services;
using SkiaSharp;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class AnimatedPhotoTests
{
    internal static byte[] Gif()
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("GIF89a"));
        bytes.AddRange([1,0,1,0,0x80,0,0,255,0,0,0,0,255]);
        bytes.AddRange([0x21,0xff,11]); bytes.AddRange(Encoding.ASCII.GetBytes("NETSCAPE2.0")); bytes.AddRange([3,1,0,0,0]);
        foreach (var color in new byte[] { 0x44, 0x4c })
        {
            bytes.AddRange([0x21,0xf9,4,0,10,0,0,0]);
            bytes.AddRange([0x2c,0,0,0,0,1,0,1,0,0,2,2,color,1,0]);
        }
        bytes.Add(0x3b); return bytes.ToArray();
    }

    [Fact]
    public void GifRemainsAnimatedAndAppendedDataIsStripped()
    {
        var input = Gif(); var photo = ProfilePhotoCodec.Normalize([..input, .."SECRET-APPENDED-DATA"u8.ToArray()]);
        Assert.Equal("image/gif", photo.ContentType); Assert.Equal(input, photo.Jpeg);
        using var data = SKData.CreateCopy(photo.Jpeg); using var codec = SKCodec.Create(data);
        Assert.Equal(2, codec.FrameCount); Assert.Equal(1, codec.Info.Width); Assert.Equal(1, codec.Info.Height);
        Assert.Equal(photo.Version, ProfilePhotoCodec.Normalize(input).Version);
    }

    [Fact]
    public void GifRejectsOversizedCanvasAndTruncatedFrameData()
    {
        var large = Gif(); large[6] = 1; large[7] = 1; // 257px canvas.
        Assert.Throws<InvalidDataException>(() => ProfilePhotoCodec.Normalize(large));
        Assert.Throws<InvalidDataException>(() => ProfilePhotoCodec.Normalize(Gif()[..^8]));
    }
}
