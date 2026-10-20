using System;
using System.Drawing; 
using System.Windows.Forms; 

namespace GuiMessageBoxApp
{
    public class MainForm : Form
    {
        private Button _msgButton;

        public MainForm()
        {
            this.Text = "C# WSL GUI 연습창";
            this.Size = new Size(400, 300);
            this.StartPosition = FormStartPosition.CenterScreen;

            _msgButton = new Button();
            _msgButton.Text = "메시지 박스 열기";
            _msgButton.Size = new Size(160, 50);

            _msgButton.Location = new Point(
                    (this.ClientSize.Width - _msgButton.Width) / 2,
                    (this.ClientSize.Height - _msgButton.Height) / 2
                    );

            _msgButton.Click += (sender, e) => 
            {
                MessageBox.Show("C# GUI 버튼이 정상적으로 클릭되었습니다!", "알림창", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            this.Controls.Add(_msgButton);
        }
    }

    class Program
    {
        [STAThread] 
            static void Main()
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new MainForm());
            }
    }
}

