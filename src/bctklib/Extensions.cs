
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Linq;
using System.Numerics;
using Neo.BlockchainToolkit.Models;
using Neo.BlockchainToolkit.Persistence;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.VM;

using Formatting = Newtonsoft.Json.Formatting;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;
using JsonTextReader = Newtonsoft.Json.JsonTextReader;
using JsonTextWriter = Newtonsoft.Json.JsonTextWriter;

namespace Neo.BlockchainToolkit
{
    public static class Extensions
    {
        public static ExpressChain LoadChain(this IFileSystem fileSystem, string path)
        {
            var serializer = new JsonSerializer();
            using var stream = fileSystem.File.OpenRead(path);
            using var streamReader = new System.IO.StreamReader(stream);
            using var reader = new JsonTextReader(streamReader);
            return serializer.Deserialize<ExpressChain>(reader)
                ?? throw new Exception($"Cannot load Neo-Express instance information from {path}");
        }

        public static void SaveChain(this IFileSystem fileSystem, ExpressChain chain, string path)
        {
            var serializer = new JsonSerializer();
            using var stream = fileSystem.File.Open(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
            using var streamWriter = new System.IO.StreamWriter(stream);
            using var writer = new JsonTextWriter(streamWriter) { Formatting = Formatting.Indented };
            serializer.Serialize(writer, chain);
        }

        public static ExpressChain FindChain(this IFileSystem fileSystem, string fileName = Constants.DEFAULT_EXPRESS_FILENAME, string? searchFolder = null)
        {
            if (fileSystem.TryFindChain(out var chain, fileName, searchFolder)) return chain;
            throw new Exception($"{fileName} Neo-Express file not found");
        }

        public static bool TryFindChain(this IFileSystem fileSystem, [MaybeNullWhen(false)] out ExpressChain chain, string fileName = Constants.DEFAULT_EXPRESS_FILENAME, string? searchFolder = null)
        {
            searchFolder ??= fileSystem.Directory.GetCurrentDirectory();
            while (searchFolder != null)
            {
                var filePath = fileSystem.Path.Combine(searchFolder, fileName);
                if (fileSystem.File.Exists(filePath))
                {
                    chain = fileSystem.LoadChain(filePath);
                    return true;
                }

                searchFolder = fileSystem.Path.GetDirectoryName(searchFolder);
            }

            chain = null;
            return false;
        }

        public static ScriptBuilder EmitInvocations(this ScriptBuilder builder, IEnumerable<ContractInvocation> invocations)
        {
            foreach (var invocation in invocations)
            {
                builder.EmitInvocation(invocation);
            }
            return builder;
        }

        public static ScriptBuilder EmitInvocation(this ScriptBuilder builder, ContractInvocation invocation)
        {
            if (invocation.Contract.TryPickT1(out var contract, out var hash))
            {
                throw new Exception($"Unbound Contract {contract}");
            }

            builder.EmitPush(invocation.Args);
            builder.EmitPush(invocation.CallFlags);
            builder.EmitPush(invocation.Operation);
            builder.EmitPush(hash);
            return builder.EmitSysCall(ApplicationEngine.System_Contract_Call);
        }

        public static ScriptBuilder EmitPush(this ScriptBuilder builder, ContractArg arg)
        {
            switch (arg)
            {
                case NullContractArg:
                    return builder.Emit(OpCode.PUSHNULL);
                case PrimitiveContractArg primitive:
                {
                    if (primitive.Value.TryPickT3(out var @string, out var remainder))
                    {
                        throw new Exception($"Unbound parameter {@string}");
                    }
                    else
                    {
                        return remainder.Match(
                            @bool => builder.EmitPush(@bool),
                            @int => builder.EmitPush(@int),
                            bytes => builder.EmitPush(bytes.Span));
                    }
                }
                case ArrayContractArg array:
                    return builder.EmitPush(array.Values);
                case MapContractArg map:
                    {
                        builder.Emit(OpCode.NEWMAP);
                        foreach (var (key, value) in map.Values)
                        {
                            builder.Emit(OpCode.DUP);
                            builder.EmitPush(key);
                            builder.EmitPush(value);
                            builder.Emit(OpCode.SETITEM);
                        }
                        return builder;
                    }
                default:
                    throw new NotSupportedException($"Unknown ContractArg type {arg.GetType().Name}");
            }
        }

        public static ScriptBuilder EmitPush(this ScriptBuilder builder, IReadOnlyList<ContractArg> args)
        {
            if (args.Count == 0) 
            { 
                return builder.Emit(OpCode.NEWARRAY0); 
            }

            for (int i = args.Count - 1; i >= 0; i --)
            {
                builder.EmitPush(args[i]);
            }
            builder.EmitPush(args.Count);
            return builder.Emit(OpCode.PACK);
        }

        internal static IReadOnlyList<T> Update<T>(this IReadOnlyList<T> @this, Func<T, T> update) where T : class
            => Update(@this, update, ReferenceEqualityComparer.Instance);

        // Calls update for every item in @this, but only returns a new list if one or more of the items has actually
        // been updated. If update returns an equal object for every item, Update returns the original list.
        internal static IReadOnlyList<T> Update<T>(this IReadOnlyList<T> @this, Func<T, T> update, IEqualityComparer<T> comparer)
        {
            // Lazily create updatedItems list when we first encounter an updated item
            List<T>? updatedList = null;
            for (int i = 0; i < @this.Count; i++)
            {
                // Potentially update the item
                var updatedItem = update(@this[i]);

                // if we haven't already got an updatedItems list
                // check to see if the object returned from update
                // is different from the one we passed in 
                if (updatedList is null && !comparer.Equals(updatedItem, @this[i]))
                {
                    // if the updated item is different, this is the
                    // first modified item in the list. 
                    // Create the updatedItems list and add all the
                    // previously processed but unmodified items 

                    updatedList = new List<T>(@this.Count);
                    for (int j = 0; j < i; j++)
                    {
                        updatedList.Add(@this[j]);
                    }
                }

                // if the updated items list exists, add the updatedItem to it
                // (modified or not) 
                if (updatedList is not null)
                {
                    updatedList.Add(updatedItem);
                }
            }

            // updateItems will be null if there were no modifications
            return updatedList ?? @this;
        }

        internal static string NormalizePath(this IFileSystem fileSystem, string path)
        {
            if (fileSystem.Path.DirectorySeparatorChar == '\\')
            {
                return fileSystem.Path.GetFullPath(path);
            }
            else
            {
                return path.Replace('\\', '/');
            }
        }

        public static ReadOnlySpan<byte> AsSpan(this Script script) => ((ReadOnlyMemory<byte>)script).Span;

        public static UInt160 CalculateScriptHash(this Script script) => Neo.SmartContract.Helper.ToScriptHash(script.AsSpan());

        public static string GetInstructionAddressPadding(this Script script)
        {
            var digitCount = EnumerateInstructions(script).Last().address switch
            {
                var x when x < 10 => 1,
                var x when x < 100 => 2,
                var x when x < 1000 => 3,
                var x when x < 10000 => 4,
                var x when x <= ushort.MaxValue => 5,
                _ => throw new Exception($"Max script length is {ushort.MaxValue} bytes"),
            };
            return new string('0', digitCount);
        }

        public static IEnumerable<(int address, Instruction instruction)> EnumerateInstructions(this Script script)
        {
            var address = 0;
            var opcode = OpCode.PUSH0;
            while (address < script.Length)
            {
                var instruction = script.GetInstruction(address);
                opcode = instruction.OpCode;
                yield return (address, instruction);
                address += instruction.Size;
            }

            if (opcode != OpCode.RET)
            {
                yield return (address, Instruction.RET);
            }
        }

        public static bool IsBranchInstruction(this Instruction instruction)
            => instruction.OpCode >= OpCode.JMPIF
                && instruction.OpCode <= OpCode.JMPLE_L;

        public static string GetOperandString(this Instruction instruction)
        {
            return string.Create<ReadOnlyMemory<byte>>(instruction.Operand.Length * 3 - 1,
                instruction.Operand, (span, memory) =>
                {
                    var first = memory.Span[0];
                    span[0] = GetHexValue(first / 16);
                    span[1] = GetHexValue(first % 16);

                    var index = 1;
                    for (var i = 2; i < span.Length; i += 3)
                    {
                        var b = memory.Span[index++];
                        span[i] = '-';
                        span[i + 1] = GetHexValue(b / 16);
                        span[i + 2] = GetHexValue(b % 16);
                    }
                });

            static char GetHexValue(int i) => (i < 10) ? (char)(i + '0') : (char)(i - 10 + 'A');
        }

        static readonly Lazy<IReadOnlyDictionary<uint, string>> sysCallNames = new Lazy<IReadOnlyDictionary<uint, string>>(
            () => ApplicationEngine.Services.ToDictionary(kvp => kvp.Value.Hash, kvp => kvp.Value.Name));

        public static string GetComment(this Instruction instruction, int ip, MethodToken[]? tokens = null)
        {
            tokens ??= Array.Empty<MethodToken>();

            switch (instruction.OpCode)
            {
                case OpCode.PUSHINT8:
                case OpCode.PUSHINT16:
                case OpCode.PUSHINT32:
                case OpCode.PUSHINT64:
                case OpCode.PUSHINT128:
                case OpCode.PUSHINT256:
                    return $"{new BigInteger(instruction.Operand.Span)}";
                case OpCode.PUSHA:
                    return $"{checked(ip + instruction.TokenI32)}";
                case OpCode.PUSHDATA1:
                case OpCode.PUSHDATA2:
                case OpCode.PUSHDATA4:
                    {
                        var text = System.Text.Encoding.UTF8.GetString(instruction.Operand.Span)
                            .Replace("\r", "\"\\r\"").Replace("\n", "\"\\n\"");
                        if (instruction.Operand.Length == 20)
                        {
                            return $"as script hash: {new UInt160(instruction.Operand.Span)}, as text: \"{text}\"";
                        }
                        return $"as text: \"{text}\"";
                    }
                case OpCode.JMP:
                case OpCode.JMPIF:
                case OpCode.JMPIFNOT:
                case OpCode.JMPEQ:
                case OpCode.JMPNE:
                case OpCode.JMPGT:
                case OpCode.JMPGE:
                case OpCode.JMPLT:
                case OpCode.JMPLE:
                case OpCode.CALL:
                    return OffsetComment(instruction.TokenI8);
                case OpCode.JMP_L:
                case OpCode.JMPIF_L:
                case OpCode.JMPIFNOT_L:
                case OpCode.JMPEQ_L:
                case OpCode.JMPNE_L:
                case OpCode.JMPGT_L:
                case OpCode.JMPGE_L:
                case OpCode.JMPLT_L:
                case OpCode.JMPLE_L:
                case OpCode.CALL_L:
                    return OffsetComment(instruction.TokenI32);
                case OpCode.CALLT:
                    {
                        int index = instruction.TokenU16;
                        if (index >= tokens.Length)
                            return $"Unknown token {instruction.TokenU16}";
                        var token = tokens[index];
                        var contract = NativeContract.Contracts.SingleOrDefault(c => c.Hash == token.Hash);
                        var tokenName = contract is null ? $"{token.Hash}" : contract.Name;
                        return $"{tokenName}.{token.Method} token call";
                    }
                case OpCode.TRY:
                    return TryComment(instruction.TokenI8, instruction.TokenI8_1);
                case OpCode.TRY_L:
                    return TryComment(instruction.TokenI32, instruction.TokenI32_1);
                case OpCode.ENDTRY:
                    return OffsetComment(instruction.TokenI8);
                case OpCode.ENDTRY_L:
                    return OffsetComment(instruction.TokenI32);
                case OpCode.SYSCALL:
                    return sysCallNames.Value.TryGetValue(instruction.TokenU32, out var name)
                        ? $"{name} SysCall"
                        : $"Unknown SysCall {instruction.TokenU32}";
                case OpCode.INITSSLOT:
                    return $"{instruction.TokenU8} static variables";
                case OpCode.INITSLOT:
                    return $"{instruction.TokenU8} local variables, {instruction.TokenU8_1} arguments";
                case OpCode.LDSFLD:
                case OpCode.STSFLD:
                case OpCode.LDLOC:
                case OpCode.STLOC:
                case OpCode.LDARG:
                case OpCode.STARG:
                    return $"Slot index {instruction.TokenU8}";
                case OpCode.NEWARRAY_T:
                case OpCode.ISTYPE:
                case OpCode.CONVERT:
                    return $"{(VM.Types.StackItemType)instruction.TokenU8} type";
                default:
                    return string.Empty;
            }

            string OffsetComment(int offset) => $"pos: {checked(ip + offset)} (offset: {offset})";
            string TryComment(int catchOffset, int finallyOffset)
            {
                var builder = new System.Text.StringBuilder();
                builder.Append(catchOffset == 0 ? "no catch block, " : $"catch {OffsetComment(catchOffset)}, ");
                builder.Append(finallyOffset == 0 ? "no finally block" : $"finally {OffsetComment(finallyOffset)}");
                return builder.ToString();
            }
        }

        // replicated logic from Blockchain.OnInitialized + Blockchain.Persist
        public static void EnsureLedgerInitialized(this IStore store, ProtocolSettings settings)
        {
            using var snapshot = new SnapshotCache(store.GetSnapshot());
            if (LedgerInitialized(snapshot)) return;

            var block = NeoSystem.CreateGenesisBlock(settings);
            if (block.Transactions.Length != 0) throw new Exception("Unexpected Transactions in genesis block");

            using (var engine = ApplicationEngine.Create(TriggerType.OnPersist, null, snapshot, block, settings, 0))
            {
                using var sb = new ScriptBuilder();
                sb.EmitSysCall(ApplicationEngine.System_Contract_NativeOnPersist);
                engine.LoadScript(sb.ToArray());
                if (engine.Execute() != VMState.HALT) throw new InvalidOperationException("NativeOnPersist operation failed", engine.FaultException);
            }

            using (var engine = ApplicationEngine.Create(TriggerType.PostPersist, null, snapshot, block, settings, 0))
            {
                using var sb = new ScriptBuilder();
                sb.EmitSysCall(ApplicationEngine.System_Contract_NativePostPersist);
                engine.LoadScript(sb.ToArray());
                if (engine.Execute() != VMState.HALT) throw new InvalidOperationException("NativePostPersist operation failed", engine.FaultException);
            }

            snapshot.Commit();

            // replicated logic from LedgerContract.Initialized
            static bool LedgerInitialized(DataCache snapshot)
            {
                const byte Prefix_Block = 5;
                var key = new KeyBuilder(NativeContract.Ledger.Id, Prefix_Block).ToArray();
                return snapshot.Find(key).Any();
            }
        }
    }
}
