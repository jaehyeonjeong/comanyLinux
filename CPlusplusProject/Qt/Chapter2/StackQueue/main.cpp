#include <QCoreApplication>		// GUI가 없는 콘솔앱을 위한 필수 해더
#include <QStack>
#include <QQueue>
#include <QDebug>				// 터미널 출력(디버깅)을 위한 헤더

void testStack() {
	qDebug() << "=======================================";
	qDebug() << "[1] Qt QStack 테스트 LIFO - 후입선출";
	qDebug() << "=======================================";

	QStack<int> stack;

	// Data 삽입(push)
	stack.push(10);
	stack.push(20);
	stack.push(30);
	qDebug() << "Stack에 10, 20, 30을 순서대로 넣었습니다.";
	qDebug() << "현재 Stack 크기:" << stack.size();
	qDebug() << "맨 위(Top) 데이터 확인:" << stack.top() << "\n";

	// Data 인쇄 및 추출(pop)
	while(!stack.isEmpty()) {
		// pop()은 가장 마지막에 들어간 데이터 부터 꺼낸다.
		int value = stack.pop();
		qDebug() << "Pop 실행 -> 꺼낸 값:" << value;
	}
	qDebug() << "모든 데이터를 꺼낸 후 Stack의 공백 여부:" << stack.isEmpty() << "\n";
}

void testQueue() {
	qDebug() << "=======================================";
	qDebug() << "[2] Qt QQueue 테스트 (FIFO - 선입선출)";
	qDebug() << "=======================================";

	QQueue<QString> queue;

	// Data 삽입 (enqueue)
	queue.enqueue("첫 번째 손님");
	queue.enqueue("두 번째 손님");
	queue.enqueue("세 번째 손님");

	qDebug() << "Queue에 대기열을 추가했습니다.";
	qDebug() << "현재 대기자 수:" << queue.size();
	qDebug() << "가장 먼저 나갈(Head) 데이터 확인:" << queue.head() << "\n";

	// Data 인쇄 및 추출(dequeue)
	while(!queue.isEmpty()){
		// dequeue()는 가장 먼저 들어간 데이터 부터 꺼냅니다.
		QString customer = queue.dequeue();
		qDebug() << "Dequeue 실행->처리 중인 항목:" << customer;
	}
	qDebug() << "모든 처리 후 Queue 공백 여부:" << queue.isEmpty();
}

int main(int argc, char* argv[]) {
	// GUI 요소가 없는 순수 콘솔 이벤트 루프 애플리케이션 생성
	QCoreApplication app(argc, argv);

	// 스택 및 큐 테스트 함수 실행
    testStack();
    testQueue();

    // 콘솔에서 즉시 결과를 확인하고 종료하기 위해 정적 종료 처리
    // (이벤트 루프를 계속 돌리려면 return app.exec(); 를 사용)
    return 0;
}
