using System.Text;

namespace ServoControlWpf.Services;

/// <summary>
/// HTTP MJPEG(multipart/x-mixed-replace) 바이트 흐름에서 JPEG 한 장씩 꺼내는 순수 파서. 네트워크를 모른다.
///
///   입력 형식 (파트마다):
///     --boundary\r\n
///     Content-Type: image/jpeg\r\n
///     Content-Length: 12345\r\n        ← 있으면 정확히 그 길이만큼
///     \r\n
///     FFD8 ... FFD9                      ← 없으면 JPEG 끝 표식(FFD9)까지 스캔
///
///   사용: Feed(bytes) 로 받은 만큼 넣고, TryTakeFrame(out jpeg) 이 true 인 동안 꺼낸다.
///   청크가 어디서 잘려도(헤더 중간, 길이 중간, 이미지 중간) 동작한다.
/// </summary>
internal sealed class MjpegStreamParser
{
    private readonly List<byte> _buf = new(256 * 1024);
    private int _pos;                        // _buf 안에서 아직 처리하지 않은 첫 위치
    private int _expectedLength = -1;        // 헤더에서 읽은 Content-Length (없으면 -1)
    private bool _inBody;                    // 파트 헤더를 지나 본문(JPEG) 대기 중인지

    public int BufferedBytes => _buf.Count - _pos;
    public int FramesParsed { get; private set; }

    public void Feed(ReadOnlySpan<byte> data)
    {
        // 처리한 앞부분을 가끔 버려 버퍼가 무한히 자라지 않게
        if (_pos > 64 * 1024 && _pos > _buf.Count / 2)
        {
            _buf.RemoveRange(0, _pos);
            _pos = 0;
        }
        _buf.AddRange(data.ToArray());
    }

    public bool TryTakeFrame(out byte[] jpeg)
    {
        jpeg = Array.Empty<byte>();
        while (true)
        {
            if (!_inBody)
            {
                // 파트 헤더 끝(\r\n\r\n)을 찾는다. 그 앞에서 Content-Length 를 읽는다.
                var hdrEnd = IndexOf(CrLfCrLf, _pos);
                if (hdrEnd < 0)
                {
                    // 헤더가 아직 다 안 왔다. 다만 경계 문자열 없이 바로 JPEG 가 오는 서버도 있으니 FFD8 이 먼저 보이면 본문으로 간주
                    var soi = IndexOf(Soi, _pos);
                    if (soi >= 0 && (hdrEnd < 0 || soi < hdrEnd))
                    {
                        _pos = soi;
                        _expectedLength = -1;
                        _inBody = true;
                        continue;
                    }
                    return false;
                }
                var header = Encoding.ASCII.GetString(_buf.GetRange(_pos, hdrEnd - _pos).ToArray());
                _expectedLength = ParseContentLength(header);
                _pos = hdrEnd + 4;
                _inBody = true;
            }

            if (_expectedLength >= 0)
            {
                if (_buf.Count - _pos < _expectedLength)
                    return false;                                     // 본문이 아직 다 안 옴
                jpeg = _buf.GetRange(_pos, _expectedLength).ToArray();
                _pos += _expectedLength;
                _inBody = false;
                FramesParsed++;
                return true;
            }

            // 길이를 모르면 FFD8 … FFD9 를 찾는다
            var start = IndexOf(Soi, _pos);
            if (start < 0) return false;
            var end = IndexOf(Eoi, start + 2);
            if (end < 0) return false;
            jpeg = _buf.GetRange(start, end + 2 - start).ToArray();
            _pos = end + 2;
            _inBody = false;
            FramesParsed++;
            return true;
        }
    }

    private static readonly byte[] CrLfCrLf = { 13, 10, 13, 10 };
    private static readonly byte[] Soi = { 0xFF, 0xD8 };
    private static readonly byte[] Eoi = { 0xFF, 0xD9 };

    private int IndexOf(byte[] pattern, int from)
    {
        var n = _buf.Count - pattern.Length;
        for (var i = from; i <= n; i++)
        {
            if (_buf[i] != pattern[0]) continue;
            var ok = true;
            for (var j = 1; j < pattern.Length; j++)
                if (_buf[i + j] != pattern[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static int ParseContentLength(string header)
    {
        foreach (var line in header.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(t["Content-Length:".Length..].Trim(), out var n) && n > 0)
                return n;
        }
        return -1;
    }
}
