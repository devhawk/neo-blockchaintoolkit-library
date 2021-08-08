using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Neo.Cryptography;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.SmartContract.Native;

namespace Neo.BlockchainToolkit.SmartContract
{
    using WitnessChecker = Func<byte[], bool>;

    public partial class TestApplicationEngine : ApplicationEngine
    {
        private readonly static IReadOnlyDictionary<uint, InteropDescriptor> overriddenServices;

        static TestApplicationEngine()
        {
            var builder = ImmutableDictionary.CreateBuilder<uint, InteropDescriptor>();

            AddOverload(builder, "System.Runtime.CheckWitness", nameof(CheckWitnessOverride));

            overriddenServices = builder.ToImmutable();

            static void AddOverload(ImmutableDictionary<uint, InteropDescriptor>.Builder builder, string sysCallName, string overloadedMethodName)
            {
                var sysCallHash = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(sysCallName).Sha256());
                var handler = typeof(TestApplicationEngine).GetMethod(overloadedMethodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"AddOverload failed to locate {overloadedMethodName} method");
                var descriptor = ApplicationEngine.Services[sysCallHash] with { Handler = handler };

                builder.Add(sysCallHash, descriptor);
            }
        }

        public static Block CreateDummyBlock(DataCache snapshot, ProtocolSettings settings)
        {
            var hash = NativeContract.Ledger.CurrentHash(snapshot);
            var currentBlock = NativeContract.Ledger.GetBlock(snapshot, hash);

            return new Block
            {
                Header = new Header
                {
                    Version = 0,
                    PrevHash = hash,
                    MerkleRoot = new UInt256(),
                    Timestamp = currentBlock.Timestamp + settings.MillisecondsPerBlock,
                    Index = currentBlock.Index + 1,
                    NextConsensus = currentBlock.NextConsensus,
                    Witness = new Witness
                    {
                        InvocationScript = Array.Empty<byte>(),
                        VerificationScript = Array.Empty<byte>()
                    },
                },
                Transactions = Array.Empty<Transaction>()
            };
        }

        private readonly WitnessChecker witnessChecker;

        public static Transaction CreateTestTransaction(UInt160 signerAccount, WitnessScope witnessScope = WitnessScope.CalledByEntry)
            => CreateTestTransaction(new Signer
            {
                Account = signerAccount,
                Scopes = witnessScope,
                AllowedContracts = Array.Empty<UInt160>(),
                AllowedGroups = Array.Empty<Neo.Cryptography.ECC.ECPoint>()
            });

        public static Transaction CreateTestTransaction(Signer? signer = null) => new Transaction
        {
            Nonce = (uint)new Random().Next(),
            Script = Array.Empty<byte>(),
            Signers = signer == null ? Array.Empty<Signer>() : new[] { signer },
            Attributes = Array.Empty<TransactionAttribute>(),
            Witnesses = Array.Empty<Witness>(),
        };

        public TestApplicationEngine(DataCache snapshot, ProtocolSettings settings) 
            : this(TriggerType.Application, null, snapshot, null, settings, ApplicationEngine.TestModeGas, null)
        {
        }

        public TestApplicationEngine(DataCache snapshot, ProtocolSettings settings, UInt160 signer, WitnessScope witnessScope = WitnessScope.CalledByEntry) 
            : this(TriggerType.Application, CreateTestTransaction(signer, witnessScope), snapshot, null, settings, ApplicationEngine.TestModeGas, null)
        {
        }

        public TestApplicationEngine(DataCache snapshot, ProtocolSettings settings, Transaction transaction) 
            : this(TriggerType.Application, transaction, snapshot, null, settings, ApplicationEngine.TestModeGas, null)
        {
        }

        public TestApplicationEngine(TriggerType trigger, IVerifiable? container, DataCache snapshot, Block? persistingBlock, ProtocolSettings settings, long gas, WitnessChecker? witnessChecker)
            : base(trigger, container ?? CreateTestTransaction(), snapshot, persistingBlock, settings, gas)
        {
            this.witnessChecker = witnessChecker ?? CheckWitness;
            ApplicationEngine.Log += OnLog;
            ApplicationEngine.Notify += OnNotify;
        }

        public override void Dispose()
        {
            ApplicationEngine.Log -= OnLog;
            ApplicationEngine.Notify -= OnNotify;
            base.Dispose();
        }

        public new event EventHandler<LogEventArgs>? Log;
        public new event EventHandler<NotifyEventArgs>? Notify;

        private void OnLog(object? sender, LogEventArgs args)
        {
            if (ReferenceEquals(this, sender))
            {
                this.Log?.Invoke(sender, args);
            }
        }

        private void OnNotify(object? sender, NotifyEventArgs args)
        {
            if (ReferenceEquals(this, sender))
            {
                this.Notify?.Invoke(sender, args);
            }
        }

        protected internal bool CheckWitnessOverride(byte[] hashOrPubkey) => witnessChecker.Invoke(hashOrPubkey);

        protected override void OnSysCall(uint methodHash)
        {
            if (overriddenServices.TryGetValue(methodHash, out var descriptor))
            {
                base.OnSysCall(descriptor);
            }
            else
            {
                base.OnSysCall(methodHash);
            }
        }
    }
}
