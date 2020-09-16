# 一个简单的Http服务器
使用方法

1.编写一个监听注册类或函数，在其中设置监听端口，并且注册http回调

    class HttpService
    {
        public HttpService()
        {
            
        }

        public void Start()
        {
            var http = new HttpServerHandlerManager(this);
            http.CreateInstance(IPAddress.Parse("0.0.0.0"), 8080);
            // 注册程序集下所有http函数
            http.RegisterHandler(Assembly.GetEntryAssembly());

            http.RegisterDirectory("/static", "./");
            Console.WriteLine("Start listening at http://0.0.0.0/8080");
        }

        HttpServerHandlerManager http = null;

        public void Stop()
        {
            http?.Release();
        }
    }
    
2.编写一个继承于HttpServerHandlerBase的类，在类中设置path和回调响应函数，每个类需要添加HttpGet或HttpPost，否则回调将无法响应
  类成员的变量指定HttpField属性，与http请求的参数映射
  
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

3. 在Main中启动Http服务器

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
