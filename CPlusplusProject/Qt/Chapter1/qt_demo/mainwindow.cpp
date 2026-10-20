#include "mainwindow.h"
#include <QMessageBox>

MainWindow::MainWindow(QWidget *parent) : QWidget(parent) {
    // [수정] showFullScreen() 대신 테두리를 없애고 크기를 강제로 키우는 방식을 사용합니다.
    setWindowFlags(Qt::Window | Qt::FramelessWindowHint);
    
    // 일반적인 FHD 모니터 크기로 설정 (모니터 해상도에 맞게 조절 가능)
    // 우선 테스트를 위해 1024x768 또는 1920x1080 등으로 지정해 보세요.
    resize(1920, 1080); 

    m_button = new QPushButton("키오스크 버튼 (클릭하세요)", this);
    m_button->setFixedSize(300, 100); 

    m_layout = new QVBoxLayout(this);
    m_layout->addWidget(m_button, 0, Qt::AlignCenter); 
    setLayout(m_layout);

    connect(m_button, &QPushButton::clicked, this, &MainWindow::onButtonClicked);
}

MainWindow::~MainWindow() {}

void MainWindow::onButtonClicked() {
    // 팝업창이 전체화면 뒤로 숨는 것을 방지하기 위해 부모(this) 지정을 확실히 합니다.
    QMessageBox::information(this, "알림", "버튼이 정상적으로 클릭되었습니다!");
}
