cmake_minimum_required(VERSION 3.16)
project(wsl_qt_console VERSION 1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# GUI 관련 메타 객체 컴파일러(MOC)가 필요 없으므로 단순화
# (콘솔에서도 시그널/슬롯을 쓴다면 ON으로 두어야 하지만, 여기선 생략 가능)
set(CMAKE_AUTOMOC OFF)

# 순수 콘솔 환경을 위해 Qt6 Core 라이브러리만 로드
find_package(Qt6 REQUIRED COMPONENTS Core)

# 빌드할 소스 파일 지정 (main.cpp 단 하나)
add_executable(wsl_qt_console main.cpp)

# Qt6 Core 라이브러리 링크
target_link_libraries(wsl_qt_console PRIVATE Qt6::Core)
