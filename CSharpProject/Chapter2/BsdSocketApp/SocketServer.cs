using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BsdSocketApp
{
    public class SocketServer
    {
        private Socket _listenerSocket;
        private int _port;
        private bool _isRunning;

        public SocketServer(int port)
        {
            _port = port;
        }

        public void Start()
        {
            // 1. 정통 BSD 규격 소켓 생성 (IPv4, 스트림, TCP 주입)
            _listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            // 2. 바인드 (Bind) - 어떤 IP와 포트에서 요청을 들을지 바인딩
            _listenerSocket.Bind(new IPEndPoint(IPAddress.Any, _port));

            // 3. 리슨 (Listen) - 연결 요청 대기 큐 크기 설정 (최대 10개 수용)
            _listenerSocket.Listen(10);
            _isRunning = true;

            Console.WriteLine($"[서버] 포트 {_port}에서 BSD 소켓 서버 구동 시작...");

            // 4. Accept 루프 돌리기
            while (_isRunning)
            {
                try
                {
                    // 클라이언트 접속 대기 (블로킹 모드)
                    Socket clientSocket = _listenerSocket.Accept();
                    Console.WriteLine($"[접속] 새로운 클라이언트 연결됨: {clientSocket.RemoteEndPoint}");

                    // [핵심] 전담 핸들러 클래스 인스턴스 생성
                    ClientHandler handler = new ClientHandler(clientSocket);

                    // [핵심] 멀티스레드(Thread) 생성 및 독립 실행 유도
                    Thread clientThread = new Thread(new ThreadStart(handler.HandleClient));
                    clientThread.IsBackground = true; // 메인 프로세스 종료 시 함께 종료되도록 설정
                    clientThread.Start();
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    Console.WriteLine($"[에러] 접속 처리 중 오류 발생: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _listenerSocket?.Close();
            Console.WriteLine("[서버] 소켓 서버가 안전하게 종료되었습니다.");
        }
    }
}

