using System;
using System.Net.Sockets;
using System.Text;

namespace BsdSocketApp
{
    public class ClientHandler
    {
        private Socket _clientSocket;

        public ClientHandler(Socket clientSocket)
        {
            _clientSocket = clientSocket;
        }

        // 스레드가 실행할 진입 메소드
        public void HandleClient()
        {
            // 데이터 수신을 위한 바이트 버퍼 메모리 확보 (1KB)
            byte[] buffer = new byte[1024];

            try
            {
                // 클라이언트가 연결을 끊기 전까지 통신 유지
                while (true)
                {
                    // 수신 (Receive) - 데이터가 올 때까지 이 스레드만 멈춰서(블로킹) 대기
                    int receivedBytes = _clientSocket.Receive(buffer);

                    // 받은 데이터가 0바이트라면 클라이언트가 연결을 정상 종료한 것임
                    if (receivedBytes == 0)
                    {
                        Console.WriteLine($"[종료] 클라이언트가 연결을 끊었습니다: {_clientSocket.RemoteEndPoint}");
                        break;
                    }

                    // 바이트 데이터를 한글/영문 문자열로 변환
                    string receivedMessage = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
                    Console.WriteLine($"[수신] [{_clientSocket.RemoteEndPoint}]: {receivedMessage.Trim()}");

                    // 에코(Echo) 반응 - 받은 메시지를 그대로 다시 클라이언트에게 전송 (Send)
                    string echoMessage = $"[Server Echo]: {receivedMessage}";
                    byte[] sendBuffer = Encoding.UTF8.GetBytes(echoMessage);
                    _clientSocket.Send(sendBuffer);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[예외] 클라이언트 통신 중 예외 발생: {ex.Message}");
            }
            finally
            {
                // 소켓 리소스 정리 및 닫기
                _clientSocket.Shutdown(SocketShutdown.Both);
                _clientSocket.Close();
            }
        }
    }
}

