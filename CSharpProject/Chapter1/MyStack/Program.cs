using System;

namespace StackAlgorithm
{
    class Program
    {
        static void Main(string[] args)
        {
            // 같은 네임스페이스(StackAlgorithm)안에 있으므로 MyStack을 바로 인식합니다.
            MyStack stack = new MyStack(3);

            stack.Push(10);
            stack.Push(20);
            stack.Push(30);

            Console.WriteLine("-------------------------");

            Console.WriteLine($"Pop 처리가 된 값: {stack.Pop()}");
            Console.WriteLine($"Pop 처리가 된 값: {stack.Pop()}");
            Console.WriteLine($"Pop 처리가 된 값: {stack.Pop()}");
        }
    }
}
