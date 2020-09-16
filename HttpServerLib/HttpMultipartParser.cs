using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using WebSocketSharp;

/// <summary>
/// HttpMultipartParser
/// Reads a multipart http data stream and returns the file name, content type and file content.
/// Also, it returns any additional form parameters in a Dictionary.
/// </summary>
namespace HttpServerLib
{
    static class Misc
    {
        public static int IndexOf(byte[] searchWithin, byte[] serachFor, int startIndex)
        {
            int index = 0;
            int startPos = Array.IndexOf(searchWithin, serachFor[0], startIndex);

            if (startPos != -1)
            {
                while ((startPos + index) < searchWithin.Length)
                {
                    if (searchWithin[startPos + index] == serachFor[index])
                    {
                        index++;
                        if (index == serachFor.Length)
                        {
                            return startPos;
                        }
                    }
                    else
                    {
                        startPos = Array.IndexOf(searchWithin, serachFor[0], startPos + index);
                        if (startPos == -1)
                        {
                            return -1;
                        }
                        index = 0;
                    }
                }
            }

            return -1;
        }

        public static byte[] ToByteArray(Stream stream)
        {
            byte[] buffer = new byte[32768];
            using (MemoryStream ms = new MemoryStream())
            {
                while (true)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        return ms.ToArray();
                    ms.Write(buffer, 0, read);
                }
            }
        }
    }

    public class MultipartFile
    {
        public string Name { get; set; }
        public string FileName { get; set; }
        public byte[] Buffer { get; set; }
        public string ContentType { get; set; }
    }

    public class MultipartObject
    {
        public MultipartObject(string boundary, byte[] buff)
        {
            Encoding = Encoding.UTF8;
            Boundary = boundary;
            Buffer = buff;

            Parse();
        }

        public MultipartObject(string boundary, byte[] buff, Logger log)
        {
            Encoding = Encoding.UTF8;
            Boundary = boundary;
            Buffer = buff;
            _Log = log;

            Parse();
        }

        public MultipartObject(string boundary, byte[] buff, Encoding encoding)
        {
            Encoding = encoding;
            Boundary = boundary;
            Buffer = buff;

            Parse();
        }

        public MultipartObject(string boundary, byte[] buff, Encoding encoding, Logger log)
        {
            Encoding = encoding;
            Boundary = boundary;
            Buffer = buff;
            _Log = log;

            Parse();
        }

        private Logger _Log { get; set; }
        private string Boundary { get; set; }
        private byte[] Buffer { get; set; }
        private Encoding Encoding { get; set; }

        internal Dictionary<string, string> Fields { get; } = new Dictionary<string, string>();

        internal Dictionary<string, List<string>> FieldList { get; } = new Dictionary<string, List<string>>();

        internal List<MultipartFile> Files { get; } = new List<MultipartFile>();

        private void Parse()
        {
            string newLine = "\r\n";
            try
            {
                // Copy to a string for header parsing
                string content = Encoding.GetString(Buffer);

                // The first line should contain the delimiter
                int delimiterEndIndex = content.IndexOf(newLine);
                if (delimiterEndIndex > -1)
                {
                    string[] sections = content.Split(new string[] { Boundary + newLine }, StringSplitOptions.RemoveEmptyEntries);

                    var currentIndex = 0;

                    foreach (string s in sections)
                    {
                        if (s.StartsWith("Content-Disposition"))
                        {
                            var index = s.IndexOf(newLine, s.IndexOf(newLine) + 1);
                            if (index <= 0)
                            {
                                continue;
                            }
                            var head = s.Substring(0, index);
                            // If we find "Content-Disposition", this is a valid multi-part section
                            // Now, look for the "name" parameter
                            var nameReg = new Regex(@"(?<=name\=\"")(.*?)(?=\"")");
                            if (!nameReg.IsMatch(head))
                            {
                                continue;
                            }

                            Match nameMatch = nameReg.Match(head);
                            string name = nameMatch.Value.Trim().ToLower();

                            var fileReg = new Regex(@"(?<=filename\=\"")(.*?)(?=\"")");

                            if (fileReg.IsMatch(head))
                            {
                                // 文件
                                var fileName = fileReg.Match(head).Value.Trim();
                                Regex regContentType = new Regex(@"(?<=Content\-Type:)(.*?)[\w/]+");
                                if (!regContentType.IsMatch(head))
                                {
                                    continue;
                                }
                                var contentTypeMatch = regContentType.Match(head);
                                var contentType = contentTypeMatch.Value.Trim();

                                var headBuff = Encoding.GetBytes(head);

                                int startIndex = Misc.IndexOf(Buffer, headBuff, currentIndex);
                                startIndex = startIndex + headBuff.Length + 4;

                                byte[] delimiterBytes = Encoding.GetBytes("\r\n--" + Boundary);
                                int endIndex = Misc.IndexOf(Buffer, delimiterBytes, startIndex);
                                currentIndex = endIndex;

                                int contentLength = endIndex - startIndex;

                                // Extract the file contents from the byte array
                                byte[] fileData = new byte[contentLength];

                                System.Buffer.BlockCopy(Buffer, startIndex, fileData, 0, contentLength);

                                MultipartFile fileObject = new MultipartFile()
                                {
                                    Buffer = fileData,
                                    ContentType = contentType,
                                    FileName = fileName,
                                    Name = name
                                };

                                Files.Add(fileObject);
                            }
                            else
                            {
                                var next = s.Substring(index + 1);

                                if (next.IndexOf(Boundary) != -1)
                                {
                                    next = next.Substring(0, next.IndexOf(Boundary));
                                }
                                var value = next.Substring(0, next.LastIndexOf("--")).Trim();

                                // 普通字符
                                if (!Fields.ContainsKey(name))
                                {
                                    Fields.Add(name, value);
                                    FieldList.Add(name, new List<string>() { value });
                                }
                                else
                                {
                                    FieldList[name].Add(value);
                                }
                            }
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                _Log?.Error(ex.ToString());
            }
        }

        public List<string> GetNames()
        {
            List<string> results = new List<string>();
            foreach(var kv in Files)
            {
                if (!results.Contains(kv.Name))
                {
                    results.Add(kv.Name);
                }
            }

            foreach(var kv in Fields)
            {
                if (!results.Contains(kv.Key))
                {
                    results.Add(kv.Key);
                }
            }

            return results;
        }

        public string GetField(string name)
        {
            if (Fields.ContainsKey(name))
            {
                return Fields[name];
            }

            return string.Empty;
        }

        public List<string> GetFields(string name)
        {
            if (FieldList.ContainsKey(name))
            {
                return FieldList[name];
            }

            return new List<string>();
        }

        public MultipartFile GetFile(string name)
        {
            foreach(var file in Files)
            {
                if (file.Name == name)
                {
                    return file;
                }
            }

            return null;
        }

        public MultipartFile GetDefaultFile()
        {
            if (Files.Count >= 1)
            {
                return Files[0];
            }

            return null;
        }

        public List<MultipartFile> GetFiles(string name)
        {
            List<MultipartFile> results = new List<MultipartFile>();
            foreach(var file in Files)
            {
                if (file.Name == name)
                {
                    results.Add(file);
                }
            }

            return results;
        }
    }

}
