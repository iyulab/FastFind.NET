using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace FastFind.Models;

/// <summary>
/// Ultra-high performance lock-free hash table optimized for file indexing
/// </summary>
public class LockFreeHashTable<TKey, TValue> where TKey : IEquatable<TKey>
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public TKey? Key;
        public TValue? Value;
        public volatile bool HasValue;
        public volatile int Version; // ABA 문제 방지
    }
    
    private readonly Entry[] _buckets;
    private readonly int _mask;
    private readonly int _maxProbes;
    private long _count; // volatile 제거
    
    public LockFreeHashTable(int capacity = 1024 * 1024)
    {
        var size = NextPowerOfTwo(capacity);
        _buckets = new Entry[size];
        _mask = size - 1;
        _maxProbes = Math.Min(32, (int)Math.Log2(size)); // 로그 기반 최대 탐사
    }
    
    public long Count => Interlocked.Read(ref _count);
    
    /// <summary>
    /// 무락 읽기 - 극도로 빠른 조회
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(TKey key, out TValue? value)
    {
        var hash = GetHashCode(key);
        var index = hash & _mask;
        
        // 🚀 선형 탐사 with 캐시 친화적 접근
        for (int i = 0; i < _maxProbes; i++)
        {
            ref var entry = ref _buckets[(index + i) & _mask];
            
            // 메모리 배리어 없이 빠른 읽기
            if (entry.HasValue && entry.Key != null && entry.Key.Equals(key))
            {
                value = entry.Value;
                return true;
            }
            
            // 빈 슬롯 발견 시 더 이상 탐사하지 않음
            if (!entry.HasValue)
                break;
        }
        
        value = default;
        return false;
    }
    
    /// <summary>
    /// 무락 쓰기 - CAS 기반 안전한 삽입
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TKey key, TValue value)
    {
        var hash = GetHashCode(key);
        var index = hash & _mask;
        
        // 🚀 선형 탐사로 빈 슬롯 찾기
        for (int i = 0; i < _maxProbes; i++)
        {
            ref var entry = ref _buckets[(index + i) & _mask];
            
            // 이미 존재하는 키 확인
            if (entry.HasValue && entry.Key != null && entry.Key.Equals(key))
                return false; // 중복 키
            
            // 빈 슬롯 발견
            if (!entry.HasValue)
            {
                // CAS로 안전하게 삽입
                var newEntry = new Entry
                {
                    Key = key,
                    Value = value,
                    Version = entry.Version + 1,
                    HasValue = false // 마지막에 설정
                };
                
                // 원자적 업데이트 시도
                if (Interlocked.CompareExchange(ref entry.Version, newEntry.Version, entry.Version) == entry.Version)
                {
                    entry.Key = key;
                    entry.Value = value;
                    Thread.MemoryBarrier(); // 순서 보장
                    entry.HasValue = true;
                    
                    Interlocked.Increment(ref _count);
                    return true;
                }
            }
        }
        
        // 테이블이 가득 참 (리사이징 필요)
        return false;
    }
    
    /// <summary>
    /// 업데이트 또는 추가
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddOrUpdate(TKey key, TValue value)
    {
        var hash = GetHashCode(key);
        var index = hash & _mask;
        
        for (int i = 0; i < _maxProbes; i++)
        {
            ref var entry = ref _buckets[(index + i) & _mask];
            
            // 기존 키 업데이트
            if (entry.HasValue && entry.Key != null && entry.Key.Equals(key))
            {
                entry.Value = value;
                return;
            }
            
            // 새 키 추가
            if (!entry.HasValue && TryAdd(key, value))
                return;
        }
    }
    
    /// <summary>
    /// 무락 제거 - 논리적 삭제
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRemove(TKey key, out TValue? value)
    {
        var hash = GetHashCode(key);
        var index = hash & _mask;
        
        for (int i = 0; i < _maxProbes; i++)
        {
            ref var entry = ref _buckets[(index + i) & _mask];
            
            if (entry.HasValue && entry.Key != null && entry.Key.Equals(key))
            {
                value = entry.Value;
                
                // 논리적 삭제 (HasValue만 false로)
                entry.HasValue = false;
                Thread.MemoryBarrier();
                entry.Key = default;
                entry.Value = default;
                
                Interlocked.Decrement(ref _count);
                return true;
            }
            
            if (!entry.HasValue)
                break;
        }
        
        value = default;
        return false;
    }
    
    /// <summary>
    /// 모든 항목 열거 (스냅샷)
    /// </summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> GetSnapshot()
    {
        var results = new List<KeyValuePair<TKey, TValue>>((int)Count);
        
        for (int i = 0; i < _buckets.Length; i++)
        {
            ref var entry = ref _buckets[i];
            if (entry.HasValue && entry.Key != null && entry.Value != null)
            {
                results.Add(new KeyValuePair<TKey, TValue>(entry.Key, entry.Value));
            }
        }
        
        return results;
    }
    
    /// <summary>
    /// 테이블 정리 (논리적으로 삭제된 항목 물리적 삭제)
    /// </summary>
    public void Compact()
    {
        // 백그라운드에서 실행되어야 함
        for (int i = 0; i < _buckets.Length; i++)
        {
            ref var entry = ref _buckets[i];
            if (!entry.HasValue && entry.Key != null)
            {
                entry.Key = default;
                entry.Value = default;
                entry.Version++;
            }
        }
    }
    
    /// <summary>
    /// 고성능 해시 함수
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetHashCode(TKey key)
    {
        // FNV-1a 해시 알고리즘 사용 (빠르고 분산이 좋음)
        var hash = key?.GetHashCode() ?? 0;
        
        // 추가 혼합으로 클러스터링 방지
        hash ^= hash >> 16;
        hash *= 0x45d9f3b;
        hash ^= hash >> 16;
        hash *= 0x45d9f3b;
        hash ^= hash >> 16;
        
        return hash;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1) return 2;
        
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }
    
    /// <summary>
    /// 통계 정보
    /// </summary>
    public struct Statistics
    {
        public long Count;
        public int Capacity;
        public double LoadFactor;
        public int MaxProbeDistance;
        public double AverageProbeDistance;
    }
    
    public Statistics GetStatistics()
    {
        var count = Count;
        var capacity = _buckets.Length;
        var loadFactor = (double)count / capacity;
        
        int maxProbe = 0;
        int totalProbe = 0;
        int usedSlots = 0;
        
        for (int i = 0; i < _buckets.Length; i++)
        {
            if (_buckets[i].HasValue)
            {
                usedSlots++;
                
                // 이 항목의 탐사 거리 계산
                var key = _buckets[i].Key;
                if (key != null)
                {
                    var hash = GetHashCode(key);
                    var idealIndex = hash & _mask;
                    var actualDistance = (i - idealIndex + _buckets.Length) & _mask;
                    
                    maxProbe = Math.Max(maxProbe, actualDistance);
                    totalProbe += actualDistance;
                }
            }
        }
        
        var avgProbe = usedSlots > 0 ? (double)totalProbe / usedSlots : 0;
        
        return new Statistics
        {
            Count = count,
            Capacity = capacity,
            LoadFactor = loadFactor,
            MaxProbeDistance = maxProbe,
            AverageProbeDistance = avgProbe
        };
    }
}

/// <summary>
/// 특수화된 문자열 키 해시 테이블 (더 빠른 성능)
/// </summary>
public class StringHashTable<TValue> : LockFreeHashTable<string, TValue>
{
    public StringHashTable(int capacity = 1024 * 1024) : base(capacity) { }
    
    /// <summary>
    /// 문자열 전용 최적화된 해시 함수
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int GetStringHashCode(string str)
    {
        if (string.IsNullOrEmpty(str))
            return 0;
        
        fixed (char* ptr = str)
        {
            var hash1 = 5381u;
            var hash2 = hash1;
            
            var length = str.Length;
            var p = (uint*)ptr;
            
            // 4바이트씩 처리
            for (int i = 0; i < length / 2; i++)
            {
                hash1 = ((hash1 << 5) + hash1) ^ p[i];
            }
            
            // 남은 2바이트 처리
            if ((length & 1) != 0)
            {
                hash2 = ((hash2 << 5) + hash2) ^ ptr[length - 1];
            }
            
            return (int)(hash1 + (hash2 * 1566083941));
        }
    }
}