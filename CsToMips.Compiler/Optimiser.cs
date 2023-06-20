using System;
using System.Collections.Generic;
using System.Linq;

namespace CsToMips.Compiler
{
    using MIPS;

    internal class Optimiser
    {
        private readonly struct EditableProgram
        {
            public readonly IList<Instruction> Instructions;
            public readonly IList<Label> Labels;

            public EditableProgram(in Program program)
            {
                Instructions = new List<Instruction>(program.Instructions.ToArray());
                Labels = new List<Label>(program.Labels.ToArray());
            }

            public Program ToProgram()
                => new (Instructions.ToArray(), Labels.ToArray());

            public MIPS.FlowAnalysis GetFlowAnalysis() => MIPS.FlowAnalysis.Build(ToProgram());

            public delegate Instruction? InstructionVisitor(int instructionIndex, in Instruction instruction);

            public delegate Label? LabelVisitor(int labelIndex, in Label label);

            public void VisitInstructions(InstructionVisitor visitor)
            {
                for (int i = 0; i < Instructions.Count; ++i)
                {
                    var instruction = Instructions[i];
                    var newInstruction = visitor(i, instruction);
                    if (newInstruction == null)
                    {
                        RemoveInstruction(i);
                        --i;
                        continue;
                    }
                    Instructions[i] = newInstruction.Value;
                }
            }

            public void VisitLabels(LabelVisitor visitor)
            {
                for (int i = 0; i < Labels.Count; ++i)
                {
                    var label = Labels[i];
                    var newLabel = visitor(i, label);
                    if (newLabel == null)
                    {
                        RemoveLabel(i);
                        --i;
                        continue;
                    }
                    Labels[i] = newLabel.Value;
                }
            }

            public int? FindLabel(int instructionIndex)
            {
                for (int i = 0; i < Labels.Count; ++i)
                {
                    if (Labels[i].InstructionIndex == instructionIndex) { return i; }
                }
                return null;
            }

            public int? FindLabel(string text)
            {
                for (int i = 0; i < Labels.Count; ++i)
                {
                    if (Labels[i].Text == text) { return i; }
                }
                return null;
            }

            public int FindOrCreateLabel(int instructionIndex, string? defaultName = null)
            {
                for (int i = 0; i < Labels.Count; ++i)
                {
                    if (Labels[i].InstructionIndex == instructionIndex) { return i; }
                }
                Labels.Add(new Label(defaultName ?? $"_{instructionIndex}", instructionIndex));
                return Labels.Count - 1;
            }

            public int GetJumpTarget(int instructionIndex)
            {
                var instruction = Instructions[instructionIndex];
                var desc = instruction.OpCode.GetDescription();
                var jumpTargetOperand = instruction.Operands[^1];
                int? jumpTarget;
                switch (desc.Behaviour)
                {
                    case OpCodeBehaviour.RelativeJump:
                        if (jumpTargetOperand.Type != OperandValueType.Static)
                        {
                            jumpTarget = null;
                            break;
                        }
                        jumpTarget = GetNextInstruction(instruction.LineIndex + jumpTargetOperand.IntValue);
                        break;
                    case OpCodeBehaviour.Jump:
                    case OpCodeBehaviour.JumpWithReturn:
                        if (jumpTargetOperand.Type != OperandValueType.Name)
                        {
                            jumpTarget = null;
                            break;
                        }
                        var labelIndex = FindLabel(jumpTargetOperand.TextValue);
                        if (labelIndex == null)
                        {
                            jumpTarget = null;
                            break;
                        }
                        jumpTarget = Labels[labelIndex.Value].InstructionIndex;
                        break;
                    default:
                        jumpTarget = null;
                        break;
                }
                if (jumpTarget == null) { throw new InvalidOperationException($"Instruction was not a jump or was invalid"); }
                return jumpTarget.Value;
            }

            public int? GetNextInstruction(int lineIndex)
            {
                for (int instructionIndex = 0; instructionIndex < Instructions.Count; ++instructionIndex)
                {
                    if (Instructions[instructionIndex].LineIndex >= lineIndex)
                    {
                        return instructionIndex;
                    }
                }
                return null;
            }

            public void RemoveInstruction(int instructionIndex)
            {
                for (int i = Labels.Count - 1; i >= 0; --i)
                {
                    if (Labels[i].InstructionIndex == instructionIndex)
                    {
                        Labels.RemoveAt(i);
                    }
                    else if (Labels[i].InstructionIndex > instructionIndex)
                    {
                        Labels[i] = new Label(Labels[i].Text, Labels[i].InstructionIndex - 1);
                    }
                }
                Instructions.RemoveAt(instructionIndex);
                // TODO: Technically we should be rewriting relative jump instructions and correcting the jump offset to account for the removed instruction, but since we don't use relative jumps, for now don't bother
            }

            public void RemoveLabel(int labelIndex)
            {
                // TODO: Check or do something about instructions that reference the label
                Labels.RemoveAt(labelIndex);
            }

            public void InsertInstruction(int instructionIndex, Instruction instruction)
            {
                for (int i = Labels.Count - 1; i >= 0; --i)
                {
                    if (Labels[i].InstructionIndex >= instructionIndex)
                    {
                        Labels[i] = new Label(Labels[i].Text, Labels[i].InstructionIndex + 1);
                    }
                }
                Instructions.Insert(instructionIndex, instruction);
                // TODO: Technically we should be rewriting relative jump instructions and correcting the jump offset to account for the removed instruction, but since we don't use relative jumps, for now don't bother
            }

            public void Splice(EditableProgram program, int instructionIndex)
            {
                for (int i = 0; i < program.Instructions.Count; ++i)
                {
                    Instructions.Insert(instructionIndex + i, program.Instructions[i]);
                }
                for (int i = Labels.Count - 1; i >= 0; --i)
                {
                    if (Labels[i].InstructionIndex >= instructionIndex)
                    {
                        Labels[i] = new Label(Labels[i].Text, Labels[i].InstructionIndex + program.Instructions.Count);
                    }
                }
                foreach (var label in program.Labels)
                {
                    Labels.Add(new Label(label.Text, label.InstructionIndex + instructionIndex));
                }
            }

            public void Append(EditableProgram program)
            {
                var startIdx = Instructions.Count;
                for (int i = 0; i < program.Instructions.Count; ++i)
                {
                    Instructions.Add(program.Instructions[i]);
                }
                foreach (var label in program.Labels)
                {
                    Labels.Add(new Label(label.Text, label.InstructionIndex + startIdx));
                }
            }

            public EditableProgram Slice(int instructionIndex, int instructionCount)
            {
                var result = new EditableProgram(Program.Blank);
                for (int i = 0; i < instructionCount; ++i)
                {
                    result.Instructions.Add(Instructions[i + instructionIndex]);
                }
                foreach (var label in Labels)
                {
                    if (label.InstructionIndex < instructionIndex || label.InstructionIndex >= instructionIndex + instructionCount) { continue; }
                    result.Labels.Add(new Label(label.Text, label.InstructionIndex - instructionIndex));
                }
                return result;
            }

            public override string ToString()
                => ToProgram().ToString();
        }

        public string Optimise(string ic10)
        {
            var lines = ic10.Split(Environment.NewLine);
            return Optimise(lines);
        }

        private string Optimise(ReadOnlySpan<string> lines)
        {
            var program = MIPS.Program.Parse(lines);
            var editableProgram = new EditableProgram(program);
            Optimise(editableProgram);
            return editableProgram.ToProgram().ToString();
        }

        private void Optimise(EditableProgram program)
        {
            Optimise_NormaliseJumps(program);
            Optimise_ControlFlow(program);
            Optimise_RedundantJumps(program);
            Optimise_RedundantLabels(program);
        }

        private void Optimise_NormaliseJumps(EditableProgram program)
        {
            // Convert all relative jumps to absolute ones - insert labels where needed
            // This will allow us to remove and reorder instructions easily as long as we keep label consistency
            for (int i = 0; i < program.Instructions.Count; ++i)
            {
                var instruction = program.Instructions[i];
                var desc = instruction.OpCode.GetDescription();
                if (desc.Behaviour == OpCodeBehaviour.RelativeJump)
                {
                    var jumpTargetOperand = instruction.Operands[^1];
                    if (jumpTargetOperand.Type != OperandValueType.Static) { throw new InvalidOperationException($"Relative jumps to non-static values are not supported"); }
                    var jumpTarget = program.GetNextInstruction(instruction.LineIndex + jumpTargetOperand.IntValue);
                    if (jumpTarget == null) { continue; }
                    var label = program.Labels[program.FindOrCreateLabel(jumpTarget.Value)].Text;
                    instruction = instruction.RewriteBehaviour(OpCodeBehaviour.Jump).SetOperand(^1, OperandValue.FromName(label));
                    program.Instructions[i] = instruction;
                }
            }
        }

        private void Optimise_ControlFlow(EditableProgram program)
        {
            
            {
                var flowAnalysis = program.GetFlowAnalysis();

                // Rewrite jump-with-return instructions to just jumps if there is no flow back to them
                program.VisitInstructions((int instructionIndex, in Instruction instruction) =>
                {
                    var blockIndex = flowAnalysis.FindBlockContaining(instructionIndex);
                    if (blockIndex == null) { return instruction; }
                    var block = flowAnalysis.Blocks[blockIndex.Value];
                    if (block.FirstInstructionIndex == instructionIndex || block.LastInstructionIndex > instructionIndex) { return instruction; }
                    var desc = instruction.OpCode.GetDescription();
                    if (desc.Behaviour != OpCodeBehaviour.JumpWithReturn) { return instruction; }
                    return instruction.RewriteBehaviour(OpCodeBehaviour.Jump);
                });

                // Reorder blocks and exclude unreachable ones
                var blocks = new List<int>();
                var remainingBlocks = new List<int>(Enumerable.Range(0, flowAnalysis.Blocks.Length));
                while (remainingBlocks.Count > 0)
                {
                    int? nextBlockIdx = null;

                    if (blocks.Count == 0)
                    {
                        // Entrypoint block always comes first
                        nextBlockIdx = 0;
                    }
                    else
                    {
                        // Search blocks for one we can put next
                        for (int i = blocks.Count - 1; i >= 0; --i)
                        {
                            var block = flowAnalysis.Blocks[i];
                            foreach (var followState in block.FollowStates)
                            {
                                if (!remainingBlocks.Contains(followState.BlockIndex)) { continue; }
                                // Also check if this candidate has any natural enter states - if it does we can't place it next unless it's naturally following this one
                                if (flowAnalysis.Blocks[followState.BlockIndex].EnterStates.Any(s => s.Natural))
                                {
                                    continue;
                                }
                                nextBlockIdx = followState.BlockIndex;
                            }
                            if (nextBlockIdx != null) { break; }
                        }
                    }

                    if (nextBlockIdx == null)
                    {
                        // All remaining blocks contain unreachable code
                        break;
                    }

                    remainingBlocks.Remove(nextBlockIdx.Value);
                    blocks.Add(nextBlockIdx.Value);

                    // If the block we just added has a natural follow state, this must always come next
                    var lastBlockAddedIdx = blocks[blocks.Count - 1];
                    while (flowAnalysis.Blocks[lastBlockAddedIdx].FollowStates.Any(s => s.Natural))
                    {
                        var followBlockIdx = lastBlockAddedIdx + 1;
                        if (!remainingBlocks.Remove(followBlockIdx))
                        {
                            throw new InvalidOperationException($"Block {lastBlockAddedIdx} has a natural follow state to block {followBlockIdx} but {followBlockIdx} was already added to the list!");
                        }
                        blocks.Add(followBlockIdx);
                        lastBlockAddedIdx = followBlockIdx;
                    }
                }
                var blockPrograms = new EditableProgram[blocks.Count];
                for (int i = 0; i < blocks.Count; ++i)
                {
                    var block = flowAnalysis.Blocks[blocks[i]];
                    blockPrograms[i] = program.Slice(block.FirstInstructionIndex, (block.LastInstructionIndex - block.FirstInstructionIndex) + 1);
                }
                program.Instructions.Clear();
                program.Labels.Clear();
                for (int i = 0; i < blockPrograms.Length; ++i)
                {
                    program.Append(blockPrograms[i]);
                }
            }

        }

        private void Optimise_RedundantJumps(EditableProgram program)
        {
            program.VisitInstructions((int instructionIndex, in Instruction instruction) =>
            {
                var desc = instruction.OpCode.GetDescription();
                if (desc.IsJump)
                {
                    var jumpTarget = program.GetJumpTarget(instructionIndex);
                    if (jumpTarget == instructionIndex + 1)
                    {
                        return null;
                    }
                }
                return instruction;
            });
        }

        private void Optimise_RedundantLabels(EditableProgram program)
        {
            var names = new HashSet<string>();
            program.VisitInstructions((int instructionIndex, in Instruction instruction) =>
            {
                foreach (var operand in instruction.Operands)
                {
                    names.Add(operand.TextValue);
                }
                return instruction;
            });
            program.VisitLabels((int labelIndex, in Label label) =>
            {
                if (!names.Contains(label.Text))
                {
                    return null;
                }
                return label;
            });
        }

    }
}
