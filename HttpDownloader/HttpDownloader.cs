/****************************** Module Header ******************************\
* HttpDownloader.cs
* The Class is used to download file over http;supports resume and retry.
*
* Copyright(c)  Shabeer Thazhathethil
*The MIT License (MIT)
*Permission is hereby granted, free of charge, to any person obtaining a copy
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
\***************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace HttpDownloader
{
    internal class HttpDownloader
    {
        public const int MaxRetryCount = 10; // for fixed retry logic,

        //TODO : Exponential retry logic

        /// <summary>
        /// HTTP GET file with out progress
        /// </summary>
        /// <param name="url"></param>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public bool GetFile(string url, string filePath)
        {
            return GetFileWithProgress(url, filePath, null);
        }

        /// <summary>
        /// HTTP GET file, and notify progress
        /// </summary>
        /// <param name="url"></param>
        /// <param name="filePath"></param>
        /// <param name="func">readSize, totalSize</param>
        /// <returns></returns>
        public bool GetFileWithProgress(string url, string filePath, Func<long, long, bool> func)
        {
            var fsData = new FileStream(filePath, FileMode.Create);

            long totalSize;
            var supportRange = HttpUtil.CheckSupportPartDownload(url, out totalSize);
            if (func != null)
                func(0, totalSize);

            bool result;
            var tmpFilePath = filePath + ".tmp";
            if (!supportRange || totalSize <= 50)
            {
                result = DirectDownload(url, tmpFilePath, func);
            }
            else
            {
                result = PartDownload(url, tmpFilePath, totalSize, func);
            }
            fsData.Flush();
            fsData.Close();
            if (result)
            {
                File.Delete(filePath);
                File.Move(tmpFilePath, filePath);
            }
            else
            {
                File.Delete(tmpFilePath);
            }
            return result;
        }

        /// <summary>
        /// download file directly, if HttpFile found url doesn't support resume-from-break
        /// </summary>
        /// <param name="url"></param>
        /// <param name="filePath"></param>
        /// <param name="func"></param>
        /// <returns></returns>
        private static bool DirectDownload(string url, string filePath, Func<long, long, bool> func)
        {
            for (var i = 0; i < MaxRetryCount; ++i)
            {
                if (HttpUtil.GetFileWithProgress(url, filePath, func))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// download file with Multiple-threaded, resume-from-break
        /// </summary>
        /// <param name="url"></param>
        /// <param name="filePath"></param>
        /// <param name="totalSize"></param>
        /// <param name="func"></param>
        /// <returns></returns>
        private bool PartDownload(string url, string filePath, long totalSize, Func<long, long, bool> func)
        {
            if (!EnsureFileSize(filePath, totalSize))
                return false;

            const int threadCount = 5;
            var length = totalSize / threadCount;
            var workers = new List<PartDownloadWorker>();
            long readSize = 0;
            for (var i = 0; i < threadCount; ++i)
            {
                var start = i * length;
                var end = (i + 1 == threadCount) ? (totalSize - 1) : (start + length);
                var worker = new PartDownloadWorker(url, filePath, start, end, count =>
                {
                    if (func == null)
                        return true;
                    lock (func)
                    {
                        readSize += count;
                        func(readSize, totalSize);
                    }
                    return true;
                });
                workers.Add(worker);
                if (!worker.Start())
                    break;
            }
            var result = true;
            foreach (var t in workers)
            {
                if (!result)
                {
                    t.Stop();
                }
                result = t.Join();
            }
            return true;
        }

        /// <summary>
        /// Ensure that file has the specified size
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="totalSize"></param>
        /// <returns></returns>
        public bool EnsureFileSize(string filePath, long totalSize)
        {
            FileStream fsData = null;
            try
            {
                fsData = new FileStream(filePath, FileMode.OpenOrCreate);
                fsData.SetLength(totalSize);
                return true;
            }
            catch (Exception e)
            {
                Logger.Error("FileUtil.EnsureFileSize Failed，Reason：" + e.Message + ", Path：" + filePath);
                return false;
            }
            finally
            {
                if (fsData != null)
                    fsData.Close();
            }
        }
    }

    /// <summary>
    /// helper class
    /// </summary>
    internal class PartDownloadWorker
    {
        private FileStream _fileStreamData;
        private readonly long _start;
        private readonly long _end;
        private Thread _thread;
        private bool _result;
        private readonly string _url;
        private bool _stopped;
        private readonly string _filePath;
        private readonly Func<long, bool> _func;

        public PartDownloadWorker(string url, string filePath, long start, long end, Func<long, bool> func)
        {
            _url = url;
            _start = start;
            _end = end;
            _thread = null;
            _result = _stopped = false;
            _filePath = filePath;
            _func = func;
        }

        /// <summary>
        /// Start part download
        /// </summary>
        /// <returns></returns>
        public bool Start()
        {
            _stopped = false;
            _result = false;

            _fileStreamData = new FileStream(_filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (_fileStreamData.Seek(_start, SeekOrigin.Begin) != _start)
            {
                _fileStreamData.Close();
                return false;
            }

            _thread = new Thread(() =>
            {
                var pos = _start;
                for (var i = 0; !_stopped && i < HttpDownloader.MaxRetryCount && pos < _end; ++i)
                {
                    if (!HttpUtil.GetFileRange(_url, pos, _end, (buffer, bufferLen) =>
                    {
                        if (_stopped)
                            return false;
                        _fileStreamData.Write(buffer, 0, bufferLen);
                        pos += bufferLen;
                        if (_func != null)
                        {
                            _func(bufferLen);
                        }
                        return true;
                    })) continue;
                    if (_fileStreamData != null)
                    {
                        _fileStreamData.Close();
                        _fileStreamData = null;
                    }
                    _result = true;
                    break;
                }
            });
            _thread.Start();
            return true;
        }

        /// <summary>
        /// Stop part file download
        /// </summary>
        public void Stop()
        {
            _stopped = true;
        }

        /// <summary>
        /// Join the thread if thread exists
        /// </summary>
        /// <returns></returns>
        public bool Join()
        {
            if (_thread != null)
            {
                _thread.Join();
                _thread = null;
            }
            if (_fileStreamData == null) return _result;
            _fileStreamData.Close();
            _fileStreamData = null;
            return _result;
        }
    }
}