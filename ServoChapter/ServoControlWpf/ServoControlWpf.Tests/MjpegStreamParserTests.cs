using System.Text;
using ServoControlWpf.Services;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>MJPEG 파서: 청크가 어디서 잘려도, Content-Length 가 있든 없든 JPEG 를 정확히 꺼내야 한다</summary>
public class MjpegStreamParserTests
{
    private static byte[] FakeJpeg(int payload, byte fill)
    {
        var b = new byte[payload + 4];
        b[0] = 0xFF; b[1] = 0xD8;
        for (var i = 2; i < payload + 2; i++) b[i] = fill;
        b[^2] = 0xFF; b[^1] = 0xD9;
        return b;
    }

    private static byte[] Part(byte[] jpeg, bool withLength)
    {
        var header = "--frame\r\nContent-Type: image/jpeg\r\n" + (withLength ? $"Content-Length: {jpeg.Length}\r\n" : "") + "\r\n";
        return Encoding.ASCII.GetBytes(header).Concat(jpeg).Concat(Encoding.ASCII.GetBytes("\r\n")).ToArray();
    }

    private static List<byte[]> Drain(MjpegStreamParser p)
    {
        var list = new List<byte[]>();
        while (p.TryTakeFrame(out var f)) list.Add(f);
        return list;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoParts_OneFeed(bool withLength)
    {
        var a = FakeJpeg(100, 0x11);
        var b = FakeJpeg(200, 0x22);
        var p = new MjpegStreamParser();
        p.Feed(Part(a, withLength).Concat(Part(b, withLength)).ToArray());
        var frames = Drain(p);
        Assert.Equal(2, frames.Count);
        Assert.Equal(a, frames[0]);
        Assert.Equal(b, frames[1]);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(7, true)]
    [InlineData(7, false)]
    [InlineData(1000, true)]
    public void ArbitraryChunking_StillYieldsExactFrames(int chunk, bool withLength)
    {
        var frames = new[] { FakeJpeg(333, 0x41), FakeJpeg(50, 0x42), FakeJpeg(2000, 0x43) };
        var stream = frames.SelectMany(f => Part(f, withLength)).ToArray();
        var p = new MjpegStreamParser();
        var got = new List<byte[]>();
        for (var i = 0; i < stream.Length; i += chunk)
        {
            p.Feed(stream.AsSpan(i, Math.Min(chunk, stream.Length - i)));
            got.AddRange(Drain(p));
        }
        Assert.Equal(3, got.Count);
        for (var i = 0; i < 3; i++) Assert.Equal(frames[i], got[i]);
        Assert.Equal(3, p.FramesParsed);
    }

    [Fact]
    public void ContentLength_IsTrusted_EvenIfPayloadContainsEoiMarker()
    {
        // 본문 안에 FFD9 가 섞여 있어도 Content-Length 가 있으면 그 길이대로
        var jpeg = new byte[] { 0xFF, 0xD8, 0x01, 0xFF, 0xD9, 0x02, 0x03, 0xFF, 0xD9 };
        var p = new MjpegStreamParser();
        p.Feed(Part(jpeg, true));
        var got = Drain(p);
        Assert.Single(got);
        Assert.Equal(jpeg, got[0]);
    }

    [Fact]
    public void ExtraHeaders_AndCaseInsensitiveLength()
    {
        var jpeg = FakeJpeg(10, 0x55);
        var raw = Encoding.ASCII.GetBytes("--frame\r\nX-Timestamp: 123\r\ncontent-length: " + jpeg.Length + "\r\nContent-Type: image/jpeg\r\n\r\n")
            .Concat(jpeg).ToArray();
        var p = new MjpegStreamParser();
        p.Feed(raw);
        Assert.True(p.TryTakeFrame(out var f));
        Assert.Equal(jpeg, f);
    }
}
