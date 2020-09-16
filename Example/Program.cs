using HttpServerLib;
using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;

namespace Example
{
    class HttpService
    {
        public HttpService()
        {
            
        }

        public void Start()
        {
            var http = new HttpServerHandlerManager(this);
            http.CreateInstance(IPAddress.Parse("0.0.0.0"), 8080);
            http.RegisterHandler(Assembly.GetEntryAssembly());
            http.Log = new WebSocketSharp.Logger(WebSocketSharp.LogLevel.Debug, null, (data, file) => { Console.WriteLine(data); });

            http.RegisterDirectory("/static", "./");
            Console.WriteLine("Start listening at http://0.0.0.0/8080");
        }

        HttpServerHandlerManager http = null;

        public void Stop()
        {
            http?.Release();
        }
    }

    [HttpGet]
    class HelloWorld : HttpServerHandlerBase
    {
        public override string Raw => "/hello";

        [HttpField("name")]
        public string Name { get; set; }

        public override Task<string> Exec(object state)
        {
            return Task.FromResult("hello " + Name);
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            HttpService http = new HttpService();
            http.Start();
            while(true)
            {
                if (Console.ReadLine() == "q")
                {
                    break;
                }
            }
            http.Stop();
        }
    }
}
