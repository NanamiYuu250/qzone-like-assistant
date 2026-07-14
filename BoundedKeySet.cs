using System.Collections;

namespace QzoneLikeAssistant;

internal sealed class BoundedKeySet(int capacity) : IEnumerable<string>
{
    private readonly HashSet<string> keys = [];
    private readonly Queue<string> order = [];

    public int Count => keys.Count;

    public bool Add(string key)
    {
        if (!keys.Add(key)) return false;
        order.Enqueue(key);
        while (order.Count > capacity)
        {
            keys.Remove(order.Dequeue());
        }
        return true;
    }

    public bool Contains(string key) => keys.Contains(key);

    public void Clear()
    {
        keys.Clear();
        order.Clear();
    }

    public IEnumerator<string> GetEnumerator() => order.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
