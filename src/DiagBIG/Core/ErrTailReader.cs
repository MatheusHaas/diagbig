using System;
using System.IO;
using System.Text;

namespace DiagBIG.Core
{
    public class ErrReadResult
    {
        public string Text { get; set; }
        public bool Truncated { get; set; }
        public long FileSize { get; set; }
        public long BytesRead { get; set; }
        public string EncodingName { get; set; }
    }

    /// <summary>Lê só o final do .err, sem travar o banco (compartilhado com leitura, escrita e exclusão).</summary>
    public static class ErrTailReader
    {
        public static ErrReadResult ReadTail(string path, long maxBytes)
        {
            byte[] data;
            long size;
            bool truncated;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                size = fs.Length;
                truncated = size > maxBytes;
                long start = truncated ? size - maxBytes : 0;
                fs.Seek(start, SeekOrigin.Begin);
                data = new byte[size - start];
                int off = 0;
                while (off < data.Length)
                {
                    int n = fs.Read(data, off, data.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
                if (off < data.Length) Array.Resize(ref data, off);
            }

            int skip = 0;
            if (truncated)
            {
                // Descarta a primeira linha (cortada no meio).
                int nl = Array.IndexOf(data, (byte)'\n');
                skip = nl >= 0 ? nl + 1 : 0;
            }
            else if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                skip = 3;
            }

            string encName;
            string text = Decode(data, skip, data.Length - skip, out encName);
            return new ErrReadResult { Text = text, Truncated = truncated, FileSize = size, BytesRead = data.Length, EncodingName = encName };
        }

        /// <summary>Tenta UTF-8 estrito; se tiver byte inválido, usa Windows-1252.</summary>
        public static string Decode(byte[] data, int index, int count, out string encodingName)
        {
            try
            {
                var utf8 = new UTF8Encoding(false, true);
                encodingName = "UTF-8";
                return utf8.GetString(data, index, count);
            }
            catch (DecoderFallbackException)
            {
                encodingName = "Windows-1252";
                return Windows1252().GetString(data, index, count);
            }
        }

        static Encoding Windows1252()
        {
            try { return Encoding.GetEncoding(1252); }
            catch (Exception) { return Encoding.GetEncoding("iso-8859-1"); }
        }
    }
}
