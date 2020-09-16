using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace HttpServerLib
{
    public class HttpServerHandlerManager
    {
        private HttpServer server = null;

        readonly object _state;

        public HttpServerHandlerManager(object state)
        {
            _state = state;
        }

        private Logger _Log = null;
        public Logger Log
        {
            get
            {
                return _Log;
            }
            set
            {
                _Log = value;
            }
        }

        public bool CacheFile { get; set; } = false;

        private int _MaxPostLength = int.MaxValue;
        public int MaxPostFileLength
        {
            get { return _MaxPostLength; }
            set { _MaxPostLength = value; }
        }

        public void CreateInstance(string ip, int port)
        {
            CreateInstance(IPAddress.Parse(ip), port);
        }

        public void CreateInstance(IPAddress ipadd, int port)
        {
            server = new HttpServer(ipadd, port);

            server.AllowForwardedRequest = true;

            server.OnGet += Server_OnGet;
            server.OnPost += Server_OnPost;

            server.Start();
        }

        public void Release()
        {
            server?.Stop();
            server = null;
            handlerDic_Get.Clear();
            handlerSerializeParamer_Get.Clear();
            handlerDic_Post.Clear();
            handlerSerializeParamer_Post.Clear();
        }

        private async Task Server_OnPost(object sender, HttpRequestEventArgs e)
        {
            try
            {
                byte[] body = null;

                byte[] buffer = new byte[32768];
                var readLength = 0;
                using (MemoryStream ms = new MemoryStream())
                {
                    while (true)
                    {
                        int read = await e.Request.InputStream.ReadAsync(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            body = ms.ToArray();
                            break;
                        }
                        readLength += read;
                        if (readLength >= MaxPostFileLength)
                        {
                            throw new Exception("upload file too big!");
                        }
                        await ms.WriteAsync(buffer, 0, read);
                    }
                }

                await ProcessHandler(e, true, body);
            }
            catch(Exception ex)
            {
                Log?.Error(ex.ToString());
            }
        }

        private async Task Server_OnGet(object sender, HttpRequestEventArgs e)
        {
            try
            {
                await ProcessHandler(e, false, null);
            }
            catch(Exception ex)
            {
                Log?.Error(ex.ToString());
            }
        }

        private Dictionary<string, Type> handlerDic_Get = new Dictionary<string, Type>();
        private Dictionary<string, List<HttpFieldAttribute>> handlerSerializeParamer_Get = new Dictionary<string, List<HttpFieldAttribute>>();
        private Dictionary<string, Type> handlerDic_Post = new Dictionary<string, Type>();
        private Dictionary<string, List<HttpFieldAttribute>> handlerSerializeParamer_Post = new Dictionary<string, List<HttpFieldAttribute>>();
        public Encoding DefaultEncoding
        {
            get
            {
                return Encoding.UTF8;
            }
        }

        public void RegisterHandler(Assembly asm)
        {
            var types = asm.GetTypes();
            foreach (var type in types)
            {
                if (type.IsSubclassOf(typeof(HttpServerHandlerBase)))
                {
                    var obj = (HttpServerHandlerBase)Activator.CreateInstance(type);
                    var bGet = false;
                    var bPost = false;
                    foreach (var attr in type.GetCustomAttributes(true))
                    {
                        if (attr.GetType() == typeof(HttpGetAttribute))
                        {
                            bGet = true;
                        }
                        if (attr.GetType() == typeof(HttpPostAttribute))
                        {
                            bPost = true;
                        }
                    }
                    if (bPost)
                    {
                        RegisterHandler(obj.Raw, type, true);
                    }
                    if (bGet)
                    {
                        RegisterHandler(obj.Raw, type, false);
                    }
                }
            }
        }

        //public void RegisterHandler(Type t)
        //{
        //    var types = Assembly.GetAssembly(t).GetTypes();
    
        //}

        private void RegisterHandler(string raw, Type handler, bool bPost)
        {
            if (!handler.IsSubclassOf(typeof(HttpServerHandlerBase)))
            {
                throw new ArgumentException("Invalid handler : " + handler);
            }
            Dictionary<string, Type> handlerDic = null;
            Dictionary<string, List<HttpFieldAttribute>> paramerDic = null;
            if (bPost)
            {
                handlerDic = handlerDic_Post;
                paramerDic = handlerSerializeParamer_Post;
            }
            else
            {
                handlerDic = handlerDic_Get;
                paramerDic = handlerSerializeParamer_Get;
            }

            if (handlerDic.ContainsKey(raw))
            {
                throw new ArgumentException("raw exist : " + raw);
            }
            handlerDic.Add(raw, handler);

            var propList = new List<HttpFieldAttribute>();
            foreach (var prop in handler.GetProperties())
            {
                foreach (var attr in prop.GetCustomAttributes(true))
                {
                    if (attr.GetType() == typeof(HttpFieldAttribute))
                    {
                        HttpFieldAttribute fieldAttr = (HttpFieldAttribute)attr;
                        fieldAttr.Name = prop.Name;
                        propList.Add(fieldAttr);
                    }
                }
            }

            paramerDic.Add(raw, propList);
        }

        /// <summary>
        /// 注册目录
        /// </summary>
        /// <param name="raw"></param>
        /// <param name="dir"></param>
        public void RegisterDirectory(string raw, string dir)
        {
            if(!processFileDirectory.ContainsKey(raw))
            {
                dir = dir.TrimEnd('/') + "/";
                if (System.IO.Directory.Exists(dir))
                {
                    processFileDirectory.Add(raw, dir);
                }
                else if(System.IO.Directory.Exists(Environment.CurrentDirectory + "/" + dir))
                {
                    processFileDirectory.Add(raw, Environment.CurrentDirectory + "/" + dir);
                }
                else
                {
                    throw new System.IO.DirectoryNotFoundException();
                }
            }
        }

        private Dictionary<string, string> processFileDirectory = new Dictionary<string, string>();

        private static string GetFileExt(string fileName)
        {
            var index = fileName.LastIndexOf(".");
            if (index == -1)
            {
                return fileName;
            }

            return fileName.Substring(index);
        }

        class CacheFileObject
        {
            public string FileName { get; set; }
            public string ContentType { get; set; }
            public byte[] FileBuffer { get; set; }
        }

        Dictionary<string, CacheFileObject> _FileCache = new Dictionary<string, CacheFileObject>();

        private async Task<bool> ProcessFile(string localPath, WebSocketSharp.Net.HttpListenerResponse response, KeyValuePair<string, string> kv)
        {
            var filePath = kv.Value + localPath.Substring(kv.Key.Length);
            if (filePath.Contains(".."))
            {
                return false;
            }

            if (CacheFile)
            {
                if (_FileCache.ContainsKey(filePath))
                {
                    var fo = _FileCache[filePath];
                    response.ContentType = fo.ContentType;

                    response.StatusCode = 200;
                    response.ContentLength64 = fo.FileBuffer.Length;
                    await response.OutputStream.WriteAsync(fo.FileBuffer, 0, fo.FileBuffer.Length);

                    return true;
                }
            }
            
            if(!System.IO.File.Exists(filePath))
            {
                return false;
            }

            var fileExt = GetFileExt(filePath);
            var contentType = FileContentType.GetFileContentType(fileExt);
            response.ContentType = contentType;

            response.StatusCode = 200;
            var fileBuffer = System.IO.File.ReadAllBytes(filePath);
            await response.OutputStream.WriteAsync(fileBuffer, 0, fileBuffer.Length);
            response.ContentLength64 = fileBuffer.Length;

            if (CacheFile)
            {
                CacheFileObject fo = new CacheFileObject()
                {
                    ContentType = contentType,
                    FileBuffer = fileBuffer,
                    FileName = filePath
                };
                if (!_FileCache.ContainsKey(filePath))
                {
                    _FileCache.Add(filePath, fo);
                }
            }

            return true;
        }

        private async Task HandlerFile(string localPath, WebSocketSharp.Net.HttpListenerResponse response)
        {
            foreach(var dirkv in processFileDirectory)
            {
                if(localPath.StartsWith(dirkv.Key))
                {
                    var bProcessed = await ProcessFile(localPath, response, dirkv);
                    if(bProcessed)
                    {
                        return;
                    }
                }
            }
            response.StatusCode = 404;
            var buffer = DefaultEncoding.GetBytes("page not found");
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        private bool GetRequestValue(NameValueCollection queryString, string parmer, out string value)
        {
            value = null;

            if (!queryString.Contains(parmer))
            {
                return false;
            }
            value = queryString[parmer];
            return true;
        }
        private void SetValue(System.Reflection.PropertyInfo prop, ref object objData, string value)
        {
            if (prop.PropertyType == typeof(string))
            {
                prop.SetValue(objData, value, null);
                return;
            }
            if (prop.PropertyType == typeof(int))
            {
                prop.SetValue(objData, int.Parse(value), null);
                return;
            }
            if (prop.PropertyType == typeof(uint))
            {
                prop.SetValue(objData, uint.Parse(value), null);
                return;
            }
            if (prop.PropertyType == typeof(long))
            {
                prop.SetValue(objData, long.Parse(value), null);
                return;
            }
            if (prop.PropertyType == typeof(ulong))
            {
                prop.SetValue(objData, ulong.Parse(value), null);
                return;
            }
            if (prop.PropertyType == typeof(float))
            {
                prop.SetValue(objData, float.Parse(value), null);
                return;
            }
            if (prop.PropertyType == typeof(bool))
            {
                prop.SetValue(objData, bool.Parse(value), null);
                return;
            }
            if (prop.PropertyType == typeof(double))
            {
                prop.SetValue(objData, double.Parse(value), null);
                return;
            }
            if (prop.PropertyType == typeof(byte))
            {
                prop.SetValue(objData, byte.Parse(value), null);
                return;
            }
            if (prop.PropertyType == typeof(short))
            {
                prop.SetValue(objData, short.Parse(value), null);
                return;
            }
            if (prop.PropertyType == typeof(ushort))
            {
                prop.SetValue(objData, ushort.Parse(value), null);
                return;
            }
            if (prop.PropertyType == typeof(DateTime))
            {
                prop.SetValue(objData, DateTime.Parse(value));
            }
        }
        public async Task<WebSocketSharp.Net.HttpListenerResponse> ProcessHandler(HttpRequestEventArgs e, bool bPost, byte[] postData)
        {
            var response = e.Response;
            try
            {
                Log?.Debug($"【{(bPost ? "POST" : "GET")}】【{e.Request.RawUrl}】");
                Dictionary<string, Type> handlerDic = null;
                Dictionary<string, List<HttpFieldAttribute>> paramerDic = null;
                if (bPost)
                {
                    handlerDic = handlerDic_Post;
                    paramerDic = handlerSerializeParamer_Post;
                }
                else
                {
                    handlerDic = handlerDic_Get;
                    paramerDic = handlerSerializeParamer_Get;
                }

                if (handlerDic.ContainsKey(e.Request.Url.LocalPath))
                {
                    var handler = handlerDic[e.Request.Url.LocalPath];
                    var objHandler = Activator.CreateInstance(handler);

                    var paramerList = paramerDic[e.Request.Url.LocalPath];
                    foreach (var paramer in paramerList)
                    {
                        var prop = handler.GetProperty(paramer.Name);
                        bool bGet = GetRequestValue(e.Request.QueryString, paramer.Field.ToLower(), out string propValue);
                        if (bGet)
                        {
                            SetValue(prop, ref objHandler, propValue);
                        }
                        else
                        {
                            if (!paramer.CanNull)
                            {
                                e.Response.StatusCode = 401;
                                var info = $"paramer is invalid: {paramer.Field} is null";
                                Log?.Error(info);
                                var buffer = DefaultEncoding.GetBytes(info);
                                e.Response.ContentLength64 = buffer.Length;
                                await e.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                                return e.Response;
                            }
                            else
                            {
                                if (!string.IsNullOrEmpty(paramer.Default))
                                {
                                    SetValue(prop, ref objHandler, paramer.Default);
                                }
                            }
                        }
                    }
                    var handlerBase = (HttpServerHandlerBase)objHandler;
                    handlerBase.PostData = postData;
                    handlerBase.Method = e.Request.HttpMethod;
                    handlerBase.RemoteIP = e.Request.RemoteEndPoint.Address.ToString();
                    handlerBase.Boundary = GetRequestBoundary(e.Request);
                    handlerBase.QueryString = e.Request.RawUrl;
                    handlerBase.Log = Log;

                    var retValue = await handlerBase.ExecAsync(response, _state);

                    return retValue;
                }
                else
                {
                    await HandlerFile(e.Request.Url.LocalPath, response);
                    return response;
                }
            }
            catch (Exception ex)
            {
                response.Close();
                Log?.Error(e.Request.RawUrl);
                Log?.Error(ex.ToString());
                return response;
            }
        }

        private static string GetRequestBoundary(WebSocketSharp.Net.HttpListenerRequest request)
        {
            var contentType = request.ContentType;
            if (string.IsNullOrEmpty(contentType))
            {
                return string.Empty;
            }
            var reg = new System.Text.RegularExpressions.Regex("boundary=(?<data>[^=;]+);?");
            if (!reg.IsMatch(contentType))
            {
                return string.Empty;
            }
            var m = reg.Match(contentType);
            return m.Groups["data"].Value;
        }
    }
    public class HttpFieldAttribute : Attribute
    {
        public HttpFieldAttribute()
        {

        }

        public HttpFieldAttribute(string fieldName)
        {
            Field = fieldName;
        }

        public string Field { get; set; }
        public string Name { get; set; }
        public bool CanNull { get; set; }
        public string Default { get; set; }
    }

    public class HttpGetAttribute : Attribute
    { }
    public class HttpPostAttribute : Attribute
    { }
    public abstract class HttpServerHandlerBase
    {
        /// <summary>
        /// Post请求的数据
        /// Get请求改数据为空字符串
        /// </summary>
        public byte[] PostData { get; set; }
        /// <summary>
        /// Handler监听的Path
        /// </summary>
        public abstract string Raw { get; }

        /// <summary>
        /// 取得当前调用为Post或Get请求
        /// </summary>
        public string Method { get; set; }

        /// <summary>
        /// 发送请求者IP
        /// </summary>
        public string RemoteIP { get; set; }

        public string Boundary { get; set; }

        /// <summary>
        /// 查询URL
        /// </summary>
        public string QueryString { get; set; }

        private Logger _Log = null;

        internal Logger Log
        {
            get
            {
                return _Log;
            }
            set
            {
                _Log = value;
            }
        }

        /// <summary>
        /// 同步调用实现方法
        /// </summary>
        /// <returns></returns>
        public abstract Task<string> Exec(object state);

        private int m_Status = 200;
        public int StatusCode
        {
            get
            {
                return m_Status;
            }
            set
            {
                m_Status = value;
            }
        }

        public static Dictionary<string, string> ParseUrlQueryData(string requestData)
        {
            var subkeyData = requestData.Split('&');
            Dictionary<string, string> postkv = new Dictionary<string, string>();
            foreach (var data in subkeyData)
            {
                var kv = data.Split('=');
                if (kv.Length == 2 && !string.IsNullOrWhiteSpace(kv[0]))
                {
                    postkv[kv[0].ToLower()] = Uri.UnescapeDataString(kv[1]);
                }
            }
            return postkv;
        }

        /// <summary>
        /// 异步调用实现方法,需要在该方法中写入Response返回的内容以及StatusCode
        /// 如果不重写该虚函数,那么Exec将会被调用,否则需要自行调用Exec并返回Response
        /// </summary>
        /// <param name="resp"></param>
        /// <returns></returns>
        public virtual async Task<WebSocketSharp.Net.HttpListenerResponse> ExecAsync(WebSocketSharp.Net.HttpListenerResponse resp, object state)
        {
            var task = Task.Run(async () =>
            {
                var content = await Exec(state);
                if(content == null)
                {
                    content = string.Empty;
                }

                Log?.Info(content);

                if (resp.CheckDisposedOrHeadersSent())
                {
                    return resp;
                }
                else
                {
                    resp.StatusCode = StatusCode;
                    resp.ContentEncoding = Encoding.UTF8;
                    resp.ContentType = "application/json";
                    var buffer = Encoding.UTF8.GetBytes(content);
                    resp.ContentLength64 = buffer.Length;
                    await resp.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    return resp;
                }
            });

            return await task;
        }
    }
}
