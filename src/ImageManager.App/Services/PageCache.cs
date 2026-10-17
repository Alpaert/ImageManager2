using ImageManager.App.Controls;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Services;

internal sealed class PageCache
{
    private readonly Dictionary<int, List<ImageViewItem>> _cache = new();
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }

    public bool TryGet(int pageIndex, out List<ImageViewItem>? items)
    {
        lock (_lock)
        {
            return _cache.TryGetValue(pageIndex, out items);
        }
    }

    public void Set(int pageIndex, List<ImageViewItem> items)
    {
        lock (_lock)
        {
            _cache[pageIndex] = items;
        }
    }

    public bool Remove(int pageIndex, out List<ImageViewItem>? removed)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(pageIndex, out removed))
            {
                _cache.Remove(pageIndex);
                return true;
            }
            removed = null;
            return false;
        }
    }

    public void Remove(int pageIndex)
    {
        lock (_lock)
        {
            _cache.Remove(pageIndex);
        }
    }

    public void Clear(Action<ImageViewItem>? onItemEvicted = null)
    {
        lock (_lock)
        {
            if (onItemEvicted != null)
            {
                foreach (var page in _cache.Values)
                {
                    foreach (var item in page)
                        onItemEvicted(item);
                }
            }
            _cache.Clear();
        }
    }

    public List<ImageViewItem> GetAllItems()
    {
        lock (_lock)
        {
            var list = new List<ImageViewItem>();
            foreach (var page in _cache.Values)
                list.AddRange(page);
            return list;
        }
    }

    public List<KeyValuePair<int, List<ImageViewItem>>> GetAllPages()
    {
        lock (_lock)
        {
            return _cache.ToList();
        }
    }

    public int Trim(int currentPage, int totalPages, int maxCachedPages, Action<ImageViewItem> onItemEvicted)
    {
        lock (_lock)
        {
            if (_cache.Count <= maxCachedPages) return 0;

            var mustKeep = new HashSet<int> { currentPage };
            if (maxCachedPages >= 2 && currentPage - 1 >= 0) mustKeep.Add(currentPage - 1);
            if (maxCachedPages >= 3 && currentPage + 1 < totalPages) mustKeep.Add(currentPage + 1);

            int evictedPages = 0;
            foreach (var key in _cache.Keys.ToList())
            {
                if (mustKeep.Contains(key)) continue;
                if (_cache.TryGetValue(key, out var evicted))
                {
                    foreach (var item in evicted)
                        onItemEvicted(item);
                    _cache.Remove(key);
                    evictedPages++;
                }
            }
            return evictedPages;
        }
    }
}
