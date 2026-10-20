cmake_minimum_required(VERSION 3.16)
project(wsl_qt_demo VERSION 1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# 메타 객체 컴파일러(MOC) 활성화 (Q_OBJECT 해석을 위해 필수)
set(CMAKE_AUTOMOC ON)

# Qt6 라이브러리 로드
find_package(Qt6 REQUIRED COMPONENTS Widgets)

# 빌드할 소스 파일 리스트 명시 (새로 만든 파일 추가)
add_executable(wsl_qt_demo 
    main.cpp 
    mainwindow.h
    mainwindow.cpp
)

target_link_libraries(wsl_qt_demo PRIVATE Qt6::Widgets)
