#ifndef MAINWINDOW_H
#define MAINWINDOW_H

#include <QWidget>
#include <QPushButton>
#include <QVBoxLayout>

// Qt의 핵심인 객체 지향 위젯 클래스 정의
class MainWindow : public QWidget {
    Q_OBJECT // Qt의 시그널-슬롯 매커니즘을 사용하기 위한 필수 매크로

public:
    explicit MainWindow(QWidget *parent = nullptr);
    ~MainWindow();

private slots:
    // 버튼 클릭 이벤트를 처리할 컨트롤러(컨트롤 로직) 함수
    void onButtonClicked();

private:
    // View(UI 컴포넌트) 분리 변수
    QPushButton *m_button;
    QVBoxLayout *m_layout;
};

#endif // MAINWINDOW_H
