#include <QApplication>
#include "mainwindow.h"

int main(int argc, char *argv[]) {
    QApplication app(argc, argv);

    // 기능이 분리된 메인 윈도우 객체 생성 및 실행
    MainWindow window;
    window.show();

    return app.exec();
}
