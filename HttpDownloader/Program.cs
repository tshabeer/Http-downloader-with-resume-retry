using System;

namespace HttpDownloader
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            // HttpUtil.SetProxy("127.0.0.1", 8888);

            var httpDownloader = new HttpDownloader();

            const string url = "http://174.138.175.114/500mb-file.zip"; //demo link for 500MB download
            const string filePath = @"c:\500mb-file.zip"; //writing the downloaded stream to a physical file

            bool result = httpDownloader.GetFileWithProgress(url, filePath,
                (readSize, totalSize) =>
                {
                    // print progress to console
                    var progress = (readSize * 100) / totalSize;
                    Console.Write("\r" + readSize + " / " + totalSize + ", " + progress + " % ");
                    return true;
                });
            Console.WriteLine("\r\nDownload " + (result ? "Ok" : "Failed"));
        }
    }
}