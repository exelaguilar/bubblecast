using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Bubblecast
{
    public enum HttpRequestReadStatus
    {
        Success,
        Malformed,
        HeadersTooLarge,
        Timeout
    }

    public sealed class ParsedHttpRequest
    {
        public string RequestLine;
        public Dictionary<string, string> Headers;
    }

    public static class BoundedHttpRequestReader
    {
        public const int MaximumRequestLineBytes = 256;
        public const int MaximumHeaderCount = 32;
        public const int MaximumHeaderBlockBytes = 8192;
        public const int TotalReadDeadlineMilliseconds = 3000;

        private enum LineStatus { Success, Malformed, TooLarge, Timeout }

        public static HttpRequestReadStatus Read(Stream stream, Action<int> setReceiveTimeout, out ParsedHttpRequest request)
        {
            request = null;
            Stopwatch elapsed = Stopwatch.StartNew();
            string requestLine;
            int requestLineBytes;
            LineStatus lineStatus = ReadLine(stream, setReceiveTimeout, elapsed, MaximumRequestLineBytes, out requestLine, out requestLineBytes);
            if (lineStatus == LineStatus.Timeout) return HttpRequestReadStatus.Timeout;
            if (lineStatus == LineStatus.TooLarge) return HttpRequestReadStatus.HeadersTooLarge;
            if (lineStatus != LineStatus.Success || String.IsNullOrWhiteSpace(requestLine)) return HttpRequestReadStatus.Malformed;

            int headerBlockBytes = requestLineBytes;
            int headerCount = 0;
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string line;
                int lineBytes;
                lineStatus = ReadLine(stream, setReceiveTimeout, elapsed, MaximumHeaderBlockBytes, out line, out lineBytes);
                if (lineStatus == LineStatus.Timeout) return HttpRequestReadStatus.Timeout;
                if (lineStatus == LineStatus.TooLarge) return HttpRequestReadStatus.HeadersTooLarge;
                if (lineStatus != LineStatus.Success) return HttpRequestReadStatus.Malformed;
                headerBlockBytes += lineBytes;
                if (headerBlockBytes > MaximumHeaderBlockBytes) return HttpRequestReadStatus.HeadersTooLarge;
                if (line.Length == 0) break;
                if (++headerCount > MaximumHeaderCount) return HttpRequestReadStatus.HeadersTooLarge;
                int colon = line.IndexOf(':');
                if (colon <= 0) return HttpRequestReadStatus.Malformed;
                string name = line.Substring(0, colon).Trim();
                if (name.Length == 0) return HttpRequestReadStatus.Malformed;
                headers[name] = line.Substring(colon + 1).Trim();
            }

            request = new ParsedHttpRequest { RequestLine = requestLine, Headers = headers };
            return HttpRequestReadStatus.Success;
        }

        private static LineStatus ReadLine(Stream stream, Action<int> setReceiveTimeout, Stopwatch elapsed, int maximumLineBytes, out string line, out int wireBytes)
        {
            StringBuilder value = new StringBuilder();
            wireBytes = 0;
            line = null;
            while (true)
            {
                int remaining = TotalReadDeadlineMilliseconds - (int)elapsed.ElapsedMilliseconds;
                if (remaining <= 0) return LineStatus.Timeout;
                if (setReceiveTimeout != null) setReceiveTimeout(remaining);

                int next;
                try { next = stream.ReadByte(); }
                catch (IOException) { return LineStatus.Timeout; }
                if (next < 0) return LineStatus.Malformed;
                if (next == '\r')
                {
                    if (setReceiveTimeout != null)
                    {
                        remaining = TotalReadDeadlineMilliseconds - (int)elapsed.ElapsedMilliseconds;
                        if (remaining <= 0) return LineStatus.Timeout;
                        setReceiveTimeout(remaining);
                    }
                    int lf;
                    try { lf = stream.ReadByte(); }
                    catch (IOException) { return LineStatus.Timeout; }
                    if (lf != '\n') return LineStatus.Malformed;
                    wireBytes = value.Length + 2;
                    if (wireBytes > maximumLineBytes) return LineStatus.TooLarge;
                    line = value.ToString();
                    return LineStatus.Success;
                }
                if (next == '\n' || next == 0 || next > 0x7f) return LineStatus.Malformed;
                value.Append((char)next);
                if (value.Length + 2 > maximumLineBytes) return LineStatus.TooLarge;
            }
        }
    }
}
