using System;

namespace BsdSocketApp
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=====================================");
            Console.WriteLine("  Thread 기반 BSD 소켓 서버 컨트롤러");
            Console.WriteLine("=====================================");

            // 포트 5000번으로 서버 객체 생성
            SocketServer server = new SocketServer(5000);

            // server 실행
            server.Start();
        }
    }
}

