#include <QCoreApplication>
#include <QHash>
#include <QMap>
#include <QDebug>

void testHash() {
    qDebug() << "========================================";
    qDebug() << "⚡ Qt QHash 테스트 (해시 테이블 - 정렬 없음/검색 매우 빠름)";
    qDebug() << "========================================";

    // QHash<KeyType, ValueType> 형태로 선언합니다. (키: 상품 ID, 값: 상품명)
    QHash<int, QString> productHash;

    // 1. 데이터 삽입 (insert 또는 [] 연산자)
    productHash.insert(301, "IoT 라즈베리 키오스크");
    productHash.insert(102, "스마트 디스플레이 스크린");
    productHash.insert(205, "임베디드 제어 보드");
    
    qDebug() << "해시 테이블에 데이터를 삽입했습니다. (총" << productHash.size() << "개)";

    // 2. 데이터 검색 (contains 및 값 접근)
    int searchId = 102;
    if (productHash.contains(searchId)) {
        qDebug() << "검색 성공! ID" << searchId << "번의 상품명:" << productHash.value(searchId);
    }

    // 3. 데이터 순회 (출력 시 저장한 순서나 크기 순서와 상관없이 무작위로 출력됨)
    qDebug() << "\n[QHash 전체 데이터 출력]";
    for (auto it = productHash.constBegin(); it != productHash.constEnd(); ++it) {
        qDebug() << "Key (ID):" << it.key() << "-> Value (Name):" << it.value();
    }
    qDebug() << "";
}

void testMap() {
    qDebug() << "========================================";
    qDebug() << "🌳 Qt QMap 테스트 (레드-블랙 트리 - 키 기준 자동 정렬)";
    qDebug() << "========================================";

    // QMap<KeyType, ValueType> 형태로 선언합니다. (키: 사원번호, 값: 이름)
    QMap<int, QString> employeeMap;

    // 일부러 정렬되지 않은 무작위 순서로 데이터를 삽입합니다.
    employeeMap.insert(905, "김철수");
    employeeMap.insert(101, "홍길동");
    employeeMap.insert(503, "이영희");

    qDebug() << "맵에 데이터를 삽입했습니다. (총" << employeeMap.size() << "개)";

    // 2. 데이터 수정 ([] 연산자 활용)
    employeeMap[101] = "홍길동 (진급)"; 

    // 3. 데이터 순회 (★ 중요: 삽입 순서와 상관없이 Key(사원번호) 기준으로 자동 오름차순 정렬되어 출력됨)
    qDebug() << "\n[QMap 전체 데이터 출력 - Key 기준 정렬됨]";
    for (auto it = employeeMap.constBegin(); it != employeeMap.constEnd(); ++it) {
        qDebug() << "사원번호:" << it.key() << "-> 이름:" << it.value();
    }
}

int main(int argc, char *argv[]) {
    QCoreApplication app(argc, argv);

    testHash();
    testMap();

    return 0;
}
