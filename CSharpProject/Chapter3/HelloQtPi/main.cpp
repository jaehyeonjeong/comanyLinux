#include <QApplication>
#include <QWidget>
#include <QLabel>
#include <QPushButton>
#include <QVBoxLayout>
#include <QSysInfo>

int main(int argc, char *argv[])
{
    QApplication app(argc, argv);

    QWidget window;
    window.setWindowTitle("Hello Qt on Pi");
    window.resize(400, 200);

    auto *info = new QLabel(QString("호스트: %1\nCPU: %2")
                            .arg(QSysInfo::machineHostName(),
                                 QSysInfo::currentCpuArchitecture()));
    auto *countLabel = new QLabel("클릭 횟수: 0");
    auto *button = new QPushButton("눌러보세요");

    int count = 0;
    QObject::connect(button, &QPushButton::clicked, [&]() {
        countLabel->setText(QString("클릭 횟수: %1").arg(++count));
    });

    auto *layout = new QVBoxLayout(&window);
    layout->addWidget(info);
    layout->addWidget(countLabel);
    layout->addWidget(button);

    window.show();
    return app.exec();
}
