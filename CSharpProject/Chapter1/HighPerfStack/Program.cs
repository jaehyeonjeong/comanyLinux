using System;

namespace HighPerfStack
{
    class Program
    {
        static void Main()
        {
            // 스택 메모리 영역에 공간 10개 확보하여 구동 테스트
            Span<int> memory = stackalloc int[10];
            FastStack<int> stack = new FastStack<int>(memory);

            stack.Push(77);
            stack.Push(88);

            Console.WriteLine($"Pop: {stack.Pop()}"); // 88
            Console.WriteLine($"Pop: {stack.Pop()}"); // 77
        }
    }
}

