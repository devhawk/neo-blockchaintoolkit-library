using System;
using System.Collections.Generic;
using System.Threading;
using Neo.Network.RPC;
using Neo.Network.RPC.Models;

namespace Neo.BlockchainToolkit.Persistence
{
    public partial class StateServiceStore
    {
        internal class MemoryCacheClient : ICachingClient
        {
            readonly RpcClient rpcClient;

            LockingDictionary<uint, RpcStateRoot> stateRoots = new();
            LockingDictionary<uint, UInt256> blockHashes = new();
            LockingDictionary<int, RpcFoundStates> foundStates = new();
            LockingDictionary<int, byte[]?> retrievedStates = new();
            LockingDictionary<int, byte[]> storages = new();

            public MemoryCacheClient(RpcClient rpcClient)
            {
                this.rpcClient = rpcClient;
            }

            public void Dispose()
            {
                rpcClient.Dispose();
            }

            public RpcFoundStates FindStates(UInt256 rootHash, UInt160 scriptHash, ReadOnlyMemory<byte> prefix, ReadOnlyMemory<byte> from = default, int? count = null)
            {
                var hash = HashCode.Combine(
                    rootHash,
                    scriptHash,
                    ReadOnlyMemoryComparer.GetHashCode(prefix.Span),
                    ReadOnlyMemoryComparer.GetHashCode(from.Span),
                    count);
                return foundStates.GetOrAdd(hash,
                    _ => rpcClient.FindStates(rootHash, scriptHash, prefix.Span, from.Span, count));
            }

            public UInt256 GetBlockHash(uint index)
            {
                return blockHashes.GetOrAdd(index, i => rpcClient.GetBlockHash(i));
            }

            public byte[]? GetState(UInt256 rootHash, UInt160 scriptHash, ReadOnlyMemory<byte> key)
            {
                var doo = new Dictionary<int, RpcFoundStates>();

                var hash = HashCode.Combine(rootHash, scriptHash, ReadOnlyMemoryComparer.GetHashCode(key.Span));
                return retrievedStates.GetOrAdd(hash, _ => rpcClient.GetProvenState(rootHash, scriptHash, key.Span));
            }

            public RpcStateRoot GetStateRoot(uint index)
            {
                return stateRoots.GetOrAdd(index, i => rpcClient.GetStateRoot(i));
            }

            public byte[] GetLedgerStorage(ReadOnlyMemory<byte> key)
            {
                var contractHash = Neo.SmartContract.Native.NativeContract.Ledger.Hash;
                var hash = ReadOnlyMemoryComparer.GetHashCode(key.Span);
                return storages.GetOrAdd(hash, _ => rpcClient.GetStorage(contractHash, key.Span));
            }

            class LockingDictionary<TKey, TValue> where TKey : notnull
            {
                readonly Dictionary<TKey, TValue> cache = new();
                readonly ReaderWriterLockSlim cacheLock = new();

                public TValue GetOrAdd(in TKey key, Func<TKey, TValue> factory)
                {
                    cacheLock.EnterUpgradeableReadLock();
                    try
                    {
                        if (cache.TryGetValue(key, out var value)) return value;

                        value = factory(key);
                        cacheLock.EnterWriteLock();
                        try
                        {
                            if (cache.TryGetValue(key, out var _value))
                            {
                                value = _value;
                            }
                            else
                            {
                                cache.Add(key, value);
                            }
                            return value;
                        }
                        finally
                        {
                            cacheLock.ExitWriteLock();
                        }
                    }
                    finally
                    {
                        cacheLock.ExitUpgradeableReadLock();
                    }
                }
            }
        }
    }
}
