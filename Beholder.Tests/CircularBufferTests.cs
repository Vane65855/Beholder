using Beholder.Ui.Helpers;

namespace Beholder.Tests;

public class CircularBufferTests {
    [Fact]
    public void Ctor_ZeroCapacity_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircularBuffer<int>(0));

    [Fact]
    public void Ctor_NegativeCapacity_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircularBuffer<int>(-1));

    [Fact]
    public void Add_SingleItem_CountIsOne() {
        var buf = new CircularBuffer<int>(5);
        buf.Add(42);
        Assert.Equal(1, buf.Count);
    }

    [Fact]
    public void Indexer_ReturnsOldestFirst() {
        var buf = new CircularBuffer<int>(5);
        buf.Add(10);
        buf.Add(20);
        buf.Add(30);

        Assert.Equal(10, buf[0]);
        Assert.Equal(20, buf[1]);
        Assert.Equal(30, buf[2]);
    }

    [Fact]
    public void Add_OverflowWraps_OldestEvicted() {
        var buf = new CircularBuffer<int>(3);
        buf.Add(1);
        buf.Add(2);
        buf.Add(3);
        buf.Add(4);

        Assert.Equal(3, buf.Count);
        Assert.Equal(2, buf[0]);
        Assert.Equal(3, buf[1]);
        Assert.Equal(4, buf[2]);
    }

    [Fact]
    public void Capacity_ReflectsConstructorArg() {
        var buf = new CircularBuffer<int>(42);
        Assert.Equal(42, buf.Capacity);
    }

    [Fact]
    public void Count_NeverExceedsCapacity() {
        var buf = new CircularBuffer<int>(3);
        for (var i = 0; i < 100; i++) buf.Add(i);
        Assert.Equal(3, buf.Count);
    }

    [Fact]
    public void Indexer_OutOfRange_Throws() {
        var buf = new CircularBuffer<int>(5);
        buf.Add(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => buf[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buf[-1]);
    }

    [Fact]
    public void Indexer_EmptyBuffer_Throws() {
        var buf = new CircularBuffer<int>(5);
        Assert.Throws<ArgumentOutOfRangeException>(() => buf[0]);
    }

    [Fact]
    public void ToList_ReturnsOldestToNewest() {
        var buf = new CircularBuffer<int>(3);
        buf.Add(10);
        buf.Add(20);
        buf.Add(30);
        buf.Add(40);

        var list = buf.ToList();
        Assert.Equal([20, 30, 40], list);
    }

    [Fact]
    public void ToList_EmptyBuffer_ReturnsEmpty() {
        var buf = new CircularBuffer<int>(5);
        Assert.Empty(buf.ToList());
    }

    [Fact]
    public void Clear_ResetsCountToZero() {
        var buf = new CircularBuffer<int>(5);
        buf.Add(1);
        buf.Add(2);
        buf.Clear();
        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void Clear_ThenAdd_WorksCorrectly() {
        var buf = new CircularBuffer<int>(3);
        buf.Add(1);
        buf.Add(2);
        buf.Add(3);
        buf.Clear();
        buf.Add(99);

        Assert.Equal(1, buf.Count);
        Assert.Equal(99, buf[0]);
    }

    [Fact]
    public void Add_CapacityOne_AlwaysHoldsLastItem() {
        var buf = new CircularBuffer<int>(1);
        buf.Add(1);
        buf.Add(2);
        buf.Add(3);

        Assert.Equal(1, buf.Count);
        Assert.Equal(3, buf[0]);
    }

    [Fact]
    public void MultipleWraps_CorrectOrder() {
        var buf = new CircularBuffer<int>(4);
        for (var i = 0; i < 20; i++) buf.Add(i);

        Assert.Equal(4, buf.Count);
        Assert.Equal(16, buf[0]);
        Assert.Equal(17, buf[1]);
        Assert.Equal(18, buf[2]);
        Assert.Equal(19, buf[3]);
    }
}
