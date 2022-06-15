using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Neo.BlockchainToolkit.Models;
using Neo.BlockchainToolkit.SmartContract;
using Neo.Cryptography.ECC;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.Wallets;

namespace Neo.BlockchainToolkit
{
    public static class ExpressChainExtensions
    {
        public static ProtocolSettings GetProtocolSettings(this ExpressChain? chain, uint secondsPerBlock = 0)
        {
            return chain == null
                ? ProtocolSettings.Default
                : ProtocolSettings.Default with
                {
                    Network = chain.Network,
                    AddressVersion = chain.AddressVersion,
                    MillisecondsPerBlock = secondsPerBlock == 0 ? 15000 : secondsPerBlock * 1000,
                    ValidatorsCount = chain.ConsensusNodes.Count,
                    StandbyCommittee = chain.ConsensusNodes.Select(GetPublicKey).ToArray(),
                    SeedList = chain.ConsensusNodes
                        .Select(n => $"{System.Net.IPAddress.Loopback}:{n.TcpPort}")
                        .ToArray(),
                };

            static ECPoint GetPublicKey(ExpressConsensusNode node)
                => new KeyPair(node.Wallet.Accounts.Select(a => a.PrivateKey).Distinct().Single().HexToBytes()).PublicKey;
        }

        public static ExpressWallet GetWallet(this ExpressChain chain, ReadOnlySpan<char> name)
            => TryGetWallet(chain, name, out var wallet)
                ? wallet
                : throw new Exception($"wallet {name} not found");

        public static bool TryGetWallet(this ExpressChain chain, ReadOnlySpan<char> name, [MaybeNullWhen(false)] out ExpressWallet wallet)
        {
            for (int i = 0; i < chain.Wallets.Count; i++)
            {
                if (name.Equals(chain.Wallets[i].Name, StringComparison.OrdinalIgnoreCase))
                {
                    wallet = chain.Wallets[i];
                    return true;
                }
            }

            wallet = null;
            return false;
        }

        public static Contract CreateGenesisContract(this ExpressChain chain) => chain.ConsensusNodes.CreateGenesisContract();

        public static Contract CreateGenesisContract(this IReadOnlyList<ExpressConsensusNode> nodes)
        {
            if (nodes.Count <= 0) throw new ArgumentException("Invalid consensus node list", nameof(nodes));

            var keys = new ECPoint[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                var account = nodes[i].Wallet.DefaultAccount ??
                    throw new ArgumentException($"{nodes[i].Wallet.Name} consensus node wallet is missing a default account", nameof(nodes));
                keys[i] = new KeyPair(account.PrivateKey.HexToBytes()).PublicKey;
            }

            return Contract.CreateMultiSigContract((keys.Length * 2 / 3) + 1, keys);
        }

        public static ExpressWalletAccount GetDefaultAccount(this ExpressChain chain, string name)
            => TryGetDefaultAccount(chain, name, out var account)
                ? account
                : throw new Exception($"default account for {name} wallet not found");

        public static UInt160 GetDefaultAccountScriptHash(this ExpressChain chain, string name)
            => TryGetDefaultAccount(chain, name, out var account)
                ? account.ToScriptHash(chain.AddressVersion)
                : throw new Exception($"default account for {name} wallet not found");

        public static bool TryGetDefaultAccount(this ExpressChain chain, ReadOnlySpan<char> name, [MaybeNullWhen(false)] out ExpressWalletAccount account)
        {
            if (chain.TryGetWallet(name, out var wallet) && wallet.DefaultAccount != null)
            {
                account = wallet.DefaultAccount;
                return true;
            }

            account = null;
            return false;
        }


        public static UInt160 CalculateScriptHash(this ExpressWalletAccount account)
        {
            KeyPair keyPair = new(Convert.FromHexString(account.PrivateKey));
            if (keyPair.PublicKey.IsInfinity) throw new ArgumentException(null, nameof(account.PrivateKey));
            var script = Contract.CreateSignatureRedeemScript(keyPair.PublicKey);
            return script.ToScriptHash();
        }

        public static UInt160 ToScriptHash(this ExpressWalletAccount account, byte addressVersion)
            => account.ScriptHash.ToScriptHash(addressVersion);

        public static TestApplicationEngine GetTestApplicationEngine(this ExpressChain chain, DataCache snapshot)
            => new TestApplicationEngine(snapshot, chain.GetProtocolSettings());

        public static TestApplicationEngine GetTestApplicationEngine(this ExpressChain chain, DataCache snapshot, UInt160 signer, WitnessScope witnessScope = WitnessScope.CalledByEntry)
            => new TestApplicationEngine(snapshot, chain.GetProtocolSettings(), signer, witnessScope);

        public static TestApplicationEngine GetTestApplicationEngine(this ExpressChain chain, DataCache snapshot, Transaction transaction)
            => new TestApplicationEngine(snapshot, chain.GetProtocolSettings(), transaction);

        public static TestApplicationEngine GetTestApplicationEngine(this ExpressChain chain, TriggerType trigger, IVerifiable? container, DataCache snapshot, Block? persistingBlock, long gas, Func<byte[], bool>? witnessChecker)
            => new TestApplicationEngine(trigger, container, snapshot, persistingBlock, chain.GetProtocolSettings(), gas, witnessChecker);
    }
}
