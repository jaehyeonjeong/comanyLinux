#include "mainwindow.h"
#include <QtUiTools/QUiLoader> // UI 파일을 로드하기 위한 헤더
#include <QFile>
#include <QPushButton>
#include <QMessageBox>
#include <QVBoxLayout>

MainWindow::MainWindow(QWidget *parent) : QWidget(parent) {
    // 키오스크 스타일 (프레임리스) 설정
    setWindowFlags(Qt::Window | Qt::FramelessWindowHint);

    // 1. UI 파일 열기
    QFile file("mainwindow.ui");
    if (!file.open(QFile::ReadOnly)) {
        QMessageBox::critical(this, "에러", "UI 파일을 열 수 없습니다.");
        return;
    }

    // 2. QUiLoader를 이용해 UI 동적 로드
    QUiLoader loader;
    m_ui = loader.load(&file, this);
    file.close();

    if (m_ui) {
        // 3. [핵심] UI 파일 내부에 선언된 "myButton" 위젯 찾아오기
        QPushButton *button = m_ui->findChild<QPushButton*>("myButton");
        
        if (button) {
            // 큼직하게 스타일 변경
            button->setFixedSize(1920, 1080);
            
            // 4. 찾은 위젯과 슬롯 함수 connect 연동
            connect(button, &QPushButton::clicked, this, &MainWindow::onButtonClicked);
        }

        // 로드된 UI를 현재 창의 레이아웃으로 설정
        QVBoxLayout *layout = new QVBoxLayout(this);
        layout->addWidget(m_ui);
        setLayout(layout);
    }
}

MainWindow::~MainWindow() {}

void MainWindow::onButtonClicked() {
    QMessageBox::information(this, "성공", "UI 파일과 위젯이 완벽하게 연동되었습니다!");
}
