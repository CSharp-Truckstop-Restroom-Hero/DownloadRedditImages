using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DownloadRedditImages
{
    public class KeyedLock<TKey> where TKey : notnull
    {
        private readonly Dictionary<TKey, (SemaphoreSlim, int)> _perKey;
        private readonly Stack<SemaphoreSlim> _pool;
        private readonly int _poolCapacity;

        public KeyedLock(int poolCapacity = 10)
        {
            _perKey = new Dictionary<TKey, (SemaphoreSlim, int)>();
            _pool = new Stack<SemaphoreSlim>(poolCapacity);
            _poolCapacity = poolCapacity;
        }

        public async Task<IDisposable> WaitAsync(TKey key)
        {
            await GetSemaphore(key).WaitAsync();
            return new Releaser(key, this);
        }

        private SemaphoreSlim GetSemaphore(TKey key)
        {
            lock (_perKey)
            {
                if (_perKey.TryGetValue(key, out var entry))
                {
                    entry.Item2++;
                    _perKey[key] = entry;
                    return entry.Item1;
                }

                SemaphoreSlim? semaphore;
                lock (_pool)
                {
                    _pool.TryPop(out semaphore);
                }

                semaphore ??= new SemaphoreSlim(1, 1);

                _perKey[key] = (semaphore, 1);
                return semaphore;
            }
        }

        private struct Releaser : IDisposable
        {
            private readonly TKey _key;
            private readonly KeyedLock<TKey> _keyedLock;

            public Releaser(TKey key, KeyedLock<TKey> keyedLock)
            {
                _key = key;
                _keyedLock = keyedLock;
            }

            public void Dispose() =>
                _keyedLock.Release(_key);
        }

        private void Release(TKey key)
        {
            SemaphoreSlim semaphore;
            int counter;
            lock (_perKey)
            {
                if (_perKey.TryGetValue(key, out var entry))
                {
                    (semaphore, counter) = entry;
                    counter--;
                    if (counter == 0)
                    {
                        _perKey.Remove(key);
                    }
                    else
                    {
                        _perKey[key] = (semaphore, counter);
                    }
                }
                else
                {
                    throw new InvalidOperationException("Key not found.");
                }
            }

            semaphore.Release();

            if (counter == 0)
            {
                lock (_pool)
                {
                    if (_pool.Count < _poolCapacity)
                    {
                        _pool.Push(semaphore);
                    }
                }
            }
        }
    }
}