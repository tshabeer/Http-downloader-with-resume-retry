/*
 The MIT License (MIT)

Copyright (c) 2014 hufuman
Contributed By : Shabeer Thazhathethil.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Cache;

namespace HttpDownloader
{
    public class HttpUtil
    {
        static private WebProxy _proxy;
        static readonly private CookieContainer CookieContainer = new CookieContainer();

        /// <summary>
        /// set proxy of HttpUtil
        /// </summary>
        /// <param name="address"></param>
        /// <param name="port"></param>
        static public void SetProxy(string address, int port)
        {
            _proxy = new WebProxy(address, port);
        }

        /// <summary>
        /// create HttpWebRequest, set some options, and replace host with ip, to accelerate HttpRequest
        /// </summary>
        /// <param name="url"></param>
        /// <returns></returns>
        static public HttpWebRequest GetHttpWebRequest(string url)
        {
            var uri = new Uri(url);
            HttpWebRequest request;
            string ip = DnsCache.GetCache().Resolve(uri.Host);
            if (ip == null)
            {
                request = (HttpWebRequest)WebRequest.Create(uri);
            }
            else
            {
                int start = url.IndexOf(uri.Host, StringComparison.Ordinal);
                string newUrl = url.Substring(0, start) + ip + url.Substring(start + uri.Host.Length);
                request = (HttpWebRequest)WebRequest.Create(newUrl);
            }
            request.Referer = uri.Scheme + "://" + uri.Host;
            request.Host = uri.Host;
            return request;
        }

        /// <summary>
        /// Set common options
        /// </summary>
        /// <param name="request"></param>
        static public void ConfigureClient(HttpWebRequest request)
        {
            request.Timeout = Math.Min(20 * 1000, request.Timeout);  // set to 20 sec
            request.ReadWriteTimeout = 20 * 1000;

            request.Proxy = _proxy ?? WebRequest.DefaultWebProxy;
            request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8";
            request.UserAgent =
                "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/35.0.1916.153 Safari/537.36";

            request.ServicePoint.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 50;

            request.CookieContainer = CookieContainer;

            request.KeepAlive = false;
            request.AllowWriteStreamBuffering = false;
            request.AllowAutoRedirect = true;
            request.AutomaticDecompression = DecompressionMethods.None;
            request.CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.NoCacheNoStore);

            GC.Collect();
        }

        /// <summary>
        /// download http file within the specified range
        /// </summary>
        /// <param name="url"></param>
        /// <param name="startPos"></param>
        /// <param name="endPos"></param>
        /// <param name="func"></param>
        /// <returns></returns>
        static public bool GetFileRange(string url, long startPos, long endPos, Func<byte[], int, bool> func)
        {
            HttpWebRequest request = null;
            HttpWebResponse response = null;
            try
            {
                request = GetHttpWebRequest(url);
                ConfigureClient(request);
                request.AddRange("bytes", startPos, endPos);

                using (response = (HttpWebResponse)request.GetResponse())
                {
                    // Response
                    string ranges = response.Headers["Content-Range"];
                    if (String.IsNullOrEmpty(ranges))
                        return false;
                    ranges = ranges.ToLower();
                    if (ranges.IndexOf("bytes", StringComparison.Ordinal) != 0)
                        return false;

                    ranges = ranges.Replace(" ", "");
                    ranges = ranges.Replace("\t", "");
                    ranges = ranges.Remove(0, 5);
                    int pos = ranges.IndexOf('-');
                    int pos2 = ranges.IndexOf('/');
                    if (pos <= 0 || pos2 < pos)
                        return false;

                    long tmpStart = Int64.Parse(ranges.Substring(0, pos));
                    long tmpEnd = Int64.Parse(ranges.Substring(pos + 1, pos2 - pos - 1));
                    if (tmpStart != startPos || tmpEnd != endPos)
                        return false;

                    var buffer = new byte[10240];
                    using (var stream = response.GetResponseStream())
                    {
                        if (stream == null)
                            return false;
                        long totalReadCount = 0;
                        int count;
                        while ((count = stream.Read(buffer, 0, 10240)) > 0)
                        {
                            if (!func(buffer, count))
                                return false;
                            totalReadCount += count;
                            if (totalReadCount >= endPos - startPos)
                                break;
                        }
                    }
                    return true;
                }
            }
            catch (Exception e)
            {
                Logger.Error("HttpUtil.GetFileRange，Reason：" + e.Message + "，Url：" + url);
                Logger.Error(e.StackTrace);
                return false;
            }
            finally
            {
                if (request != null)
                {
                    request.Abort();
                }
                if (response != null)
                {
                    response.Close();
                }
            }
        }

        static public bool GetFile(string url, string filePath)
        {
            return GetFileWithProgress(url, filePath, null);
        }

        /// <summary>
        /// download http file directly
        /// </summary>
        /// <param name="url"></param>
        /// <param name="filePath"></param>
        /// <param name="func"></param>
        /// <returns></returns>
        static public bool GetFileWithProgress(string url, string filePath, Func<long, long, bool> func)
        {
            FileStream fsData = null;
            long totalSize = 0;
            long readSize = 0;
            HttpWebResponse response = null;
            HttpWebRequest request = null;
            try
            {
                fsData = new FileStream(filePath, FileMode.Create);

                request = GetHttpWebRequest(url);
                ConfigureClient(request);

                var buffer = new Byte[10240];
                using (response = (HttpWebResponse)request.GetResponse())
                {
                    var readStream = response.GetResponseStream();
                    if (readStream == null)
                        throw new Exception("response.GetResponseStream Error");

                    totalSize = response.ContentLength;
                    readSize = 0;
                    if (response.ContentEncoding.ToLower().Contains("gzip"))
                        readStream = new GZipStream(readStream, CompressionMode.Decompress);
                    else if (response.ContentEncoding.ToLower().Contains("deflate"))
                        readStream = new DeflateStream(readStream, CompressionMode.Decompress);

                    using (var reader = new BinaryReader(readStream))
                    {
                        int count;
                        while ((count = reader.Read(buffer, 0, 10240)) > 0)
                        {
                            if (func != null)
                                func(readSize, totalSize);
                            fsData.Write(buffer, 0, count);
                            readSize += count;
                        }
                    }
                }
                return true;
            }
            catch (WebException e)
            {
                Logger.Error("HttpGetToFile ,download failed :" + readSize + "/" + totalSize + "，Reason：" + e.Message + "，URL：" + url + "，filePath：" + filePath);
                return false;
            }
            finally
            {
                if (fsData != null)
                {
                    fsData.Close();
                    fsData.Dispose();
                }
                if (response != null)
                {
                    response.Close();
                }
                if (request != null)
                {
                    request.Abort();
                }
            }
        }

        /// <summary>
        /// check if http file could be download resume-from-break
        /// </summary>
        /// <param name="url"></param>
        /// <param name="totalSize"></param>
        /// <returns></returns>
        static public bool CheckSupportPartDownload(string url, out long totalSize)
        {
            for (; ; )
            {
                HttpWebRequest request = null;
                HttpWebResponse response = null;
                try
                {
                    request = GetHttpWebRequest(url);
                    ConfigureClient(request);
                    // request.Method = "HEAD";
                    using (response = (HttpWebResponse)request.GetResponse())
                    {
                        totalSize = response.ContentLength;
                        string data = response.Headers.Get("accept-ranges");
                        return !String.IsNullOrEmpty(data) && data == "bytes";
                    }
                }
                catch (Exception e)
                {
                    Logger.Error("CheckSupportPartDownload，Url：" + url + "， Error：" + e.Message);
                }
                finally
                {
                    if (request != null)
                    {
                        request.Abort();
                    }
                    if (response != null)
                    {
                        response.Close();
                    }
                }
            }
        }
    }

    /// <summary>
    /// cache for dns, to accelerate http
    /// </summary>
    internal class DnsCache
    {
        static private readonly DnsCache Instance = new DnsCache();

        // used to balance several ips of one host
        private readonly Random _random = new Random();

        // lowered host => ips
        private readonly Dictionary<string, List<string>> _caches = new Dictionary<string, List<string>>();

        /// <summary>
        /// Singleton
        /// </summary>
        /// <returns></returns>
        static public DnsCache GetCache()
        {
            return Instance;
        }

        /// <summary>
        /// Resolve ip of host
        /// </summary>
        /// <param name="host"></param>
        /// <returns></returns>
        public string Resolve(string host)
        {
            List<string> ips;
            if (_caches.TryGetValue(host.ToLower(), out ips))
                return ips[_random.Next(0, ips.Count)];
            try
            {
                var entry = Dns.GetHostEntry(host);
                if (entry.AddressList.Length <= 0)
                    return null;
                ips = entry.AddressList.Select(address => address.ToString()).ToList();
                return ips[_random.Next(0, ips.Count)];
            }
            catch (Exception e)
            {
                Logger.Error("DnsCache.Resolve Failed，Reason：" + e.Message);
                return null;
            }
        }
    }

    internal class Logger
    {
        static Logger()
        {
            Trace.Listeners.Add(new TextWriterTraceListener("http_dowloader.log", "myListener"));
        }

        static public void Error(string msg)
        {
            Console.WriteLine("\r\n" + msg);
            Trace.WriteLine(msg, DateTime.Now.ToString(CultureInfo.InvariantCulture));
            Trace.Flush();
        }
    }
}