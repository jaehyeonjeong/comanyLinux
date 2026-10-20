using System;

namespace StackAlgorithm
{
    public class MyStack
    {
        private int[] space;
        private int top;
        private int max;

        public MyStack(int size)
        {
            max = size;
            space = new int[max];
            top = -1;
        }

        public void Push(int item)
        {
            if(top >= max - 1)
            {
                Console.WriteLine("Stack Overflow! 스택이 가득 찼습니다.");
                return;
            }
            space[++top] = item;
            Console.WriteLine($"Push: {item}");
        }

        public int Pop()
        {
            if(top < 0)
            {
                Console.WriteLine("Stack Underflow! 스택이 비어있습니다.");
                return -1;
            }
            return space[top--];
        }
    }
}
