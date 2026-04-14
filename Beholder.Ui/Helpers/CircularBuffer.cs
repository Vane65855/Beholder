using System;
using System.Collections.Generic;

namespace Beholder.Ui.Helpers;

/// <summary>
/// Fixed-capacity ring buffer that overwrites the oldest entry when full.
/// Designed for the 300-sample (5 min at 1/sec) traffic history window.
/// </summary>
internal sealed class CircularBuffer<T>(int capacity) {
    private readonly T[] _buffer = new T[capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.")];
    private int _head;
    private int _count;

    public int Capacity => _buffer.Length;
    public int Count => _count;

    public void Add(T item) {
        _buffer[_head] = item;
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    /// <summary>
    /// Gets the element at the specified index where 0 is the oldest entry.
    /// </summary>
    public T this[int index] {
        get {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            var start = (_head - _count + _buffer.Length) % _buffer.Length;
            return _buffer[(start + index) % _buffer.Length];
        }
    }

    public IReadOnlyList<T> ToList() {
        var result = new T[_count];
        var start = (_head - _count + _buffer.Length) % _buffer.Length;
        for (var i = 0; i < _count; i++)
            result[i] = _buffer[(start + i) % _buffer.Length];
        return result;
    }

    public void Clear() {
        _head = 0;
        _count = 0;
        Array.Clear(_buffer);
    }
}
