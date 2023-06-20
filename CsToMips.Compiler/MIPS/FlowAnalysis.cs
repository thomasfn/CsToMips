using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CsToMips.MIPS
{
    public readonly struct BlockState : IEquatable<BlockState>
    {
        public readonly int ProgramCounter;
        public readonly int? ReturnAddress;

        public BlockState(int programCounter, int? returnAddress)
        {
            ProgramCounter = programCounter;
            ReturnAddress = returnAddress;
        }

        public override bool Equals(object? obj) => obj is BlockState state && Equals(state);

        public bool Equals(BlockState other) => ProgramCounter == other.ProgramCounter;

        public override int GetHashCode() => HashCode.Combine(ProgramCounter);

        public static bool operator ==(BlockState left, BlockState right) => left.Equals(right);

        public static bool operator !=(BlockState left, BlockState right) => !(left == right);

        public BlockState SetProgramCounter(int newProgramCounter)
            => new BlockState(newProgramCounter, ReturnAddress);

        public BlockState SetReturnAddress(int? newReturnAddress)
            => new BlockState(ProgramCounter, newReturnAddress);

        public override string ToString()
            => $"(pc={ProgramCounter},ra={(ReturnAddress != null ? ReturnAddress.Value.ToString() : "?")})";

        public static BlockState GetLeastKnownState(in BlockState a, in BlockState b)
        {
            // If either has an unknown return address, the least known state has an unknown return address
            if (a.ReturnAddress == null) { return a; }
            if (b.ReturnAddress == null) { return b; }
            return a;
        }
    }

    public readonly struct BlockTransitionState
    {
        public readonly int BlockIndex;
        public readonly BlockState State;
        public readonly bool Natural;

        public BlockTransitionState(int blockIndex, BlockState state, bool natural)
        {
            BlockIndex = blockIndex;
            State = state;
            Natural = natural;
        }
    }

    public readonly struct Block
    {
        public readonly ImmutableArray<BlockTransitionState> EnterStates;
        public readonly BlockState BeginState;
        public readonly BlockState ExitState;
        public readonly ImmutableArray<BlockTransitionState> FollowStates;

        public int FirstInstructionIndex => BeginState.ProgramCounter;

        public int LastInstructionIndex => ExitState.ProgramCounter;

        public Block(BlockState beginState, BlockState exitState)
        {
            EnterStates = ImmutableArray<BlockTransitionState>.Empty;
            BeginState = beginState;
            ExitState = exitState;
            FollowStates = ImmutableArray<BlockTransitionState>.Empty;
        }

        public Block(ImmutableArray<BlockTransitionState> enterStates, BlockState beginState, BlockState exitState, ImmutableArray<BlockTransitionState> followStates)
        {
            EnterStates = enterStates;
            BeginState = beginState;
            ExitState = exitState;
            FollowStates = followStates;
        }

        internal Block AddEnterState(int blockIndex, BlockState state, bool natural) => new (EnterStates.Add(new BlockTransitionState(blockIndex, state, natural)), BeginState, ExitState, FollowStates);

        internal Block AddFollowState(int blockIndex, BlockState state, bool natural) => new (EnterStates, BeginState, ExitState, FollowStates.Add(new BlockTransitionState(blockIndex, state, natural)));

        public override string ToString()
            => $"[{BeginState}, {ExitState}]";
    }

    public readonly struct InstructionFlowMetadata
    {
        private readonly BlockState[] enterStates;
        private readonly BlockState[] followStates;

        /// <summary>
        /// All possible states at which the processor can arrive at this instruction.
        /// The states' program counters will refer to the previous instruction and the rest of the state will be as if the previous instruction was just carried out.
        /// </summary>
        public ReadOnlySpan<BlockState> EnterStates => enterStates;

        /// <summary>
        /// All possible states at which the processor can leave this instruction.
        /// The states' program counters will refer to this instruction and the rest of the state will be as if this instruction was just carried out.
        /// </summary>
        public ReadOnlySpan<BlockState> FollowStates => followStates;

        public InstructionFlowMetadata(ReadOnlySpan<BlockState> enterStates, ReadOnlySpan<BlockState> followStates)
        {
            this.enterStates = enterStates.ToArray();
            this.followStates = followStates.ToArray();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("enter {");
            foreach (var state in enterStates) { sb.Append(state.ToString()); }
            sb.Append("}, follow {");
            foreach (var state in followStates) { sb.Append(state.ToString()); }
            sb.Append("}");
            return sb.ToString();
        }
    }

    public readonly struct FlowAnalysis
    {
        public readonly ImmutableArray<Block> Blocks;

        public FlowAnalysis(IEnumerable<Block> blocks)
        {
            Blocks = blocks.ToImmutableArray();
        }

        public int? FindBlockContaining(int instructionIndex)
        {
            for (int i = 0; i < Blocks.Length; ++i)
            {
                var block = Blocks[i];
                if (instructionIndex >= block.FirstInstructionIndex && instructionIndex <= block.LastInstructionIndex)
                {
                    return i;
                }
            }
            return null;
        }

        private static IEnumerable<int> ResolveJumpTargets(Program program, BlockState beginState)
        {
            var instruction = program.Instructions[beginState.ProgramCounter];
            var operand = instruction.Operands[^1];
            var desc = instruction.OpCode.GetDescription();
            if (desc.Behaviour == OpCodeBehaviour.RelativeJump)
            {
                if (operand.Type == OperandValueType.Static)
                {
                    var jumpTarget = program.GetNextInstruction(instruction.LineIndex + operand.IntValue);
                    if (jumpTarget == null) { throw new InvalidOperationException(); }
                    yield return jumpTarget.Value;
                    yield break;
                }
                else
                {
                    // Relative jump to non-static value (e.g. a register) - does this even work in IC10?
                    throw new NotImplementedException();
                }
            }
            else if (desc.Behaviour == OpCodeBehaviour.Jump || desc.Behaviour == OpCodeBehaviour.JumpWithReturn)
            {
                if (operand.Type == OperandValueType.Name)
                {
                    if (operand.TextValue == "ra")
                    {
                        if (beginState.ReturnAddress != null)
                        {
                            yield return beginState.ReturnAddress.Value;
                            yield break;
                        }
                        // We don't know what ra is so let's assume worst case which is it could be any kind of jal
                        for (int i = 0; i < program.Instructions.Length; ++i)
                        {
                            var potentialJal = program.Instructions[i];
                            var potentialJalDesc = potentialJal.OpCode.GetDescription();
                            if (potentialJalDesc.Behaviour == OpCodeBehaviour.JumpWithReturn)
                            {
                                yield return i + 1;
                            }
                        }
                        yield break;
                    }
                    var labelIdx = program.FindLabel(operand.TextValue);
                    if (labelIdx == null) { throw new InvalidOperationException(); }
                    yield return program.Labels[labelIdx.Value].InstructionIndex;
                    yield break;
                }
                else if (operand.Type == OperandValueType.Static)
                {
                    var jumpTarget = program.GetNextInstruction(operand.IntValue);
                    if (jumpTarget == null) { throw new InvalidOperationException(); }
                    yield return jumpTarget.Value;
                    yield break;
                }
                else
                {
                    // Aboslute jump to non-static value (e.g. a register) - does this even work in IC10?
                    throw new NotImplementedException();
                }
            }
            else
            {
                throw new InvalidOperationException();
            }
        }

        private static void GenerateInstructionFlowMetadata(in Program program, Span<InstructionFlowMetadata> outInstructionFlowMetadatas)
        {
            // Enter state: pc refers to previous instruction, state is after previous instruction is executed
            // Begin state: pc refers to current instruction, state is after previous instruction is executed
            // Exit state: pc refers to current instruction, state is after current instruction is executed
            var exploredBeginStates = new HashSet<BlockState>();
            var unexploredBeginStates = new Stack<BlockState>();
            unexploredBeginStates.Push(new BlockState(0, null));
            var instructionEnterStates = new HashSet<BlockState>[program.Instructions.Length];
            var instructionFollowStates = new HashSet<BlockState>[program.Instructions.Length];
            while (unexploredBeginStates.Any())
            {
                var beginState = unexploredBeginStates.Pop();
                exploredBeginStates.Add(beginState);
                var instruction = program.Instructions[beginState.ProgramCounter];
                var desc = instruction.OpCode.GetDescription();
                var exitState = beginState; // TODO: Apply any instruction-specific changes to the state
                if (desc.Behaviour == OpCodeBehaviour.JumpWithReturn)
                {
                    exitState = exitState.SetReturnAddress(beginState.ProgramCounter + 1);
                }
                bool isUnconditionalJump;
                switch (desc.Behaviour)
                {
                    case OpCodeBehaviour.Jump:
                    case OpCodeBehaviour.JumpWithReturn:
                    case OpCodeBehaviour.RelativeJump:
                        var potentialJumpTargets = ResolveJumpTargets(program, beginState);
                        foreach (var jumpTarget in potentialJumpTargets)
                        {
                            var jumpBeginState = exitState.SetProgramCounter(jumpTarget);
                            if (!exploredBeginStates.Contains(jumpBeginState))
                            {
                                unexploredBeginStates.Push(jumpBeginState);
                            }
                            (instructionEnterStates[jumpTarget] ?? (instructionEnterStates[jumpTarget] = new HashSet<BlockState>())).Add(exitState);
                            (instructionFollowStates[beginState.ProgramCounter] ?? (instructionFollowStates[beginState.ProgramCounter] = new HashSet<BlockState>())).Add(jumpBeginState);
                        }
                        isUnconditionalJump = !desc.IsConditional;
                        break;
                    default:
                        isUnconditionalJump = false;
                        break;
                }
                if (!isUnconditionalJump)
                {
                    var followState = exitState.SetProgramCounter(beginState.ProgramCounter + 1);
                    if (!exploredBeginStates.Contains(followState))
                    {
                        unexploredBeginStates.Push(followState);
                    }
                    (instructionEnterStates[beginState.ProgramCounter + 1] ?? (instructionEnterStates[beginState.ProgramCounter + 1] = new HashSet<BlockState>())).Add(exitState);
                    (instructionFollowStates[beginState.ProgramCounter] ?? (instructionFollowStates[beginState.ProgramCounter] = new HashSet<BlockState>())).Add(followState);
                }
            }
            for (int i = 0; i < program.Instructions.Length; ++i)
            {
                outInstructionFlowMetadatas[i] = new InstructionFlowMetadata(
                    instructionEnterStates[i]?.ToArray() ?? ReadOnlySpan<BlockState>.Empty,
                    instructionFollowStates[i]?.ToArray() ?? ReadOnlySpan<BlockState>.Empty
                );
            }
        }

        public static FlowAnalysis Build(in Program program)
        {
            var instrutionFlowMetadatas = new InstructionFlowMetadata[program.Instructions.Length];
            GenerateInstructionFlowMetadata(program, instrutionFlowMetadatas);

            var blocks = new List<Block>();
            BlockState? currentBlockHeadState = new BlockState(0, null);
            var instructionToBlockIndex = new int[program.Instructions.Length];
            instructionToBlockIndex.AsSpan().Fill(-1);
            for (int i = 0; i < program.Instructions.Length; ++i)
            {
                if (instrutionFlowMetadatas[i].EnterStates.Length > 1 && currentBlockHeadState != null && currentBlockHeadState.Value.ProgramCounter < i)
                {
                    var prevExitState = instrutionFlowMetadatas[i - 1].EnterStates.GetBeginState(i - 1).SetProgramCounter(i - 1);
                    blocks.Add(new Block(currentBlockHeadState.Value, prevExitState));
                    currentBlockHeadState = null;
                }
                if (currentBlockHeadState == null)
                {
                    if (instrutionFlowMetadatas[i].EnterStates.Length == 0) { continue; }
                    currentBlockHeadState = instrutionFlowMetadatas[i].EnterStates.GetBeginState(i);
                }
                instructionToBlockIndex[i] = blocks.Count;
                if (instrutionFlowMetadatas[i].FollowStates.Length == 1 && instrutionFlowMetadatas[i].FollowStates[0].ProgramCounter == i + 1) { continue; }
                var exitState = instrutionFlowMetadatas[i].EnterStates.GetBeginState(i).SetProgramCounter(i);
                blocks.Add(new Block(currentBlockHeadState.Value, exitState));
                currentBlockHeadState = null;
            }
            for (int i = 0; i < blocks.Count; ++i)
            {
                var block = blocks[i];
                foreach (var enterState in instrutionFlowMetadatas[block.FirstInstructionIndex].EnterStates)
                {
                    block = block.AddEnterState(instructionToBlockIndex[enterState.ProgramCounter], enterState, enterState.ProgramCounter == block.FirstInstructionIndex - 1);
                }
                foreach (var followState in instrutionFlowMetadatas[block.LastInstructionIndex].FollowStates)
                {
                    block = block.AddFollowState(instructionToBlockIndex[followState.ProgramCounter], followState, followState.ProgramCounter == block.LastInstructionIndex + 1);
                }
                blocks[i] = block;
            }

            return new FlowAnalysis(blocks);
        }

    }

    internal static class FlowAnalysisExt
    {
        public static BlockState GetBeginState(this ReadOnlySpan<BlockState> enterStates, int instructionIndex)
        {
            if (enterStates.Length == 0) { throw new InvalidOperationException(); }
            if (enterStates.Length == 1) { return enterStates[0].SetProgramCounter(instructionIndex); }
            var currentBestKnownState = enterStates[0];
            for (int i = 1; i < enterStates.Length; ++i)
            {
                currentBestKnownState = BlockState.GetLeastKnownState(currentBestKnownState, enterStates[i]);
            }
            return currentBestKnownState.SetProgramCounter(instructionIndex);
        }
    }
}
