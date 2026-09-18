using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ClassroomNetGuard.Shared
{
    /// <summary>
    /// 通信帧协议：4 字节大端长度前缀 + UTF-8 JSON 正文。
    /// 教师端与学生端之间的所有消息都走该协议。
    /// </summary>
    public static class FrameProtocol
    {
        public const int MaxFrameBytes = 4 * 1024 * 1024;

        public static async Task WriteAsync(Stream stream, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var len = new byte[4];
            len[0] = (byte)((bytes.Length >> 24) & 0xFF);
            len[1] = (byte)((bytes.Length >> 16) & 0xFF);
            len[2] = (byte)((bytes.Length >> 8) & 0xFF);
            len[3] = (byte)(bytes.Length & 0xFF);
            await stream.WriteAsync(len, 0, 4);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        /// <summary>读一帧；连接关闭或帧非法时返回 null。</summary>
        public static async Task<string> ReadAsync(Stream stream)
        {
            var lenBuf = new byte[4];
            var read = 0;
            while (read < 4)
            {
                var n = await stream.ReadAsync(lenBuf, read, 4 - read);
                if (n <= 0) return null;
                read += n;
            }
            var length = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
            if (length <= 0 || length > MaxFrameBytes) return null;

            var buf = new byte[length];
            read = 0;
            while (read < length)
            {
                var n = await stream.ReadAsync(buf, read, length - read);
                if (n <= 0) return null;
                read += n;
            }
            return Encoding.UTF8.GetString(buf);
        }
    }
}
