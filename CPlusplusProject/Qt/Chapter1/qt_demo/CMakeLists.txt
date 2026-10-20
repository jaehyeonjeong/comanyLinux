cmake_minimum_required(VERSION 3.16)
project(wsl_qt_demo VERSION 1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)

# [수정] UiTools 컴포넌트 추가 설치 및 불러오기
find_package(Qt6 REQUIRED COMPONENTS Widgets UiTools)

add_executable(wsl_qt_demo 
    main.cpp 
    mainwindow.h
    mainwindow.cpp
)

# [수정] Qt6::UiTools 링크 추가
target_link_libraries(wsl_qt_demo PRIVATE Qt6::Widgets Qt6::UiTools)
