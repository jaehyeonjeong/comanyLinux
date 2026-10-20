using System;

namespace HighPerfStack
{
    public ref struct FastStack<T>
    {
        private Span<T> _storage;
        private int _top;

        public FastStack(Span<T> storage)
        {
            _storage = storage;
            _top = -1;
        }

        public void Push(T item)
        {
            if (_top >= _storage.Length - 1)
                throw new StackOverflowException("스택 용량 초과");

            _storage[++_top] = item;
        }

        public T Pop()
        {
            if (_top < 0)
                throw new InvalidOperationException("스택이 비어있음");

            return _storage[_top--];
        }
    }
}


