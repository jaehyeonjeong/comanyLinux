using System;
using System.Windows; // WPF 핵심 네임스페이스
using System.Windows.Controls; // WPF 컨트롤(버전, 레이아웃 등)

namespace GappedWpfApp
{
    class Program
    {
        [STAThread] // WPF 환경 구동을 위한 전역 스레드 필수 속성
            static void Main()
            {
                // 1. Application 객체 생성 (WPF 프로그램의 생명주기 관리)
                Application app = new Application();

                // 2. 메인 윈도우 창 생성 및 세팅
                Window window = new Window
                {
                    Title = "C# WSL WPF 연습창",
                    Width = 400,
                    Height = 300,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                // 3. WPF용 레이아웃 그리드(Grid) 및 버튼 생성
                Grid grid = new Grid();
                Button msgButton = new Button
                {
                    Content = "메시지 박스 열기",
                    Width = 160,
                    Height = 50,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                // 4. 버튼 클릭 이벤트 연결
                msgButton.Click += (sender, e) =>
                {
                    // [핵심] 버튼을 누르면 WPF 전역 메시지 박스 팝업
                    MessageBox.Show("C# WPF 버튼이 성공적으로 클릭되었습니다!", "알림창", MessageBoxButton.OK, MessageBoxImage.Information);
                };

                // 레이아웃에 버튼을 넣고, 창에 레이아웃을 집어넣음
                grid.Children.Add(msgButton);
                window.Content = grid;

                // 5. 창을 띄우면서 WPF 어플리케이션 시작
                app.Run(window);
            }
    }
}

