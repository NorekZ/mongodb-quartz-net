﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;
using MongoDB.Driver;
using Quartz.Spi.MongoDbJobStore.Models;
using Quartz.Spi.MongoDbJobStore.Repositories;

namespace Quartz.Spi.MongoDbJobStore
{
    /// <summary>
    /// Implements a simple distributed lock on top of MongoDB. It is not a reentrant lock so you can't 
    /// acquire the lock more than once in the same thread of execution.
    /// </summary>
    internal class LockManager : IDisposable
    {
        private static readonly TimeSpan SleepThreshold = TimeSpan.FromMilliseconds(1000);

        private static readonly ILog Log = LogManager.GetLogger<LockManager>();

        private readonly LockRepository _lockRepository;

        private readonly ConcurrentDictionary<LockType, LockInstance> _pendingLocks =
            new ConcurrentDictionary<LockType, LockInstance>();

        private readonly SemaphoreSlim _pendingLocksSemaphore = new SemaphoreSlim(1);

        private bool _disposed;

        public LockManager(IMongoDatabase database, string instanceName, string collectionPrefix)
        {
            _lockRepository = new LockRepository(database, instanceName, collectionPrefix);
        }

        public void Dispose()
        {
            EnsureObjectNotDisposed();

            _disposed = true;
            var locks = _pendingLocks.ToArray();
            foreach (var keyValuePair in locks)
            {
                keyValuePair.Value.Dispose();
            }
        }

        public async Task<IDisposable> AcquireLock(LockType lockType, string instanceId)
        {
            var lockKey = Guid.NewGuid();

            while (true)
            {
                EnsureObjectNotDisposed();

                Log.Info("N: Trying to acquire lock " + lockKey);

                await _pendingLocksSemaphore.WaitAsync();
                try
                {
                    if (await _lockRepository.TryAcquireLock(lockType, instanceId, lockKey))
                    {
                        Log.Info("N: Acquired lock " + lockKey);

                        var lockInstance = new LockInstance(this, lockType, instanceId, lockKey);
                        AddLock(lockInstance);

                        Log.Info("N: Lock ready " + lockKey);

                        return lockInstance;
                    }
                }
                finally
                {
                    _pendingLocksSemaphore.Release();
                }

                Log.Info("N: Failed to acquired lock " + lockKey);

                Thread.Sleep(SleepThreshold);
            }
        }
        
        private async Task ReleaseLock(LockInstance lockInstance)
        {
            await _pendingLocksSemaphore.WaitAsync();
            try
            {

                _lockRepository.ReleaseLock(lockInstance.LockType, lockInstance.InstanceId, lockInstance.LockKey).GetAwaiter().GetResult();

                Log.Info("N: Lock released " + lockInstance.LockKey);

                LockReleased(lockInstance);
            }
            finally
            {
                _pendingLocksSemaphore.Release();
            }
        }

        private void EnsureObjectNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LockManager));
            }
        }

        private void AddLock(LockInstance lockInstance)
        {
            if (!_pendingLocks.TryAdd(lockInstance.LockType, lockInstance))
            {
                throw new Exception($"Unable to add lock instance for lock {lockInstance.LockType} on {lockInstance.InstanceId}");
            }
        }

        private void LockReleased(LockInstance lockInstance)
        {
            if (!_pendingLocks.TryRemove(lockInstance.LockType, out _))
            {
                Log.Warn($"Unable to remove pending lock {lockInstance.LockType} on {lockInstance.InstanceId}");
            }
        }

        private class LockInstance : IDisposable
        {
            private readonly LockManager _lockManager;
            public Guid LockKey { get; }

            private bool _disposed;

            public LockInstance(LockManager lockManager, LockType lockType, string instanceId, Guid lockKey)
            {
                _lockManager = lockManager;
                LockKey = lockKey;
                LockType = lockType;
                InstanceId = instanceId;
            }

            public string InstanceId { get; }

            public LockType LockType { get; }

            public void Dispose()
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(LockInstance),
                        $"This lock {LockType} for {InstanceId} has already been disposed");
                }

                _lockManager.ReleaseLock(this).GetAwaiter().GetResult();

                _disposed = true;
            }
        }
    }
}