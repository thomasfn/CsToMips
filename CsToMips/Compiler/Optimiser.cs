using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CsToMips.Compiler
{
    internal enum IC10InstructionKind
    {
        Instruction,
        Label
    }

    internal readonly struct IC10Instruction
    {
        public readonly string OpCode;
        public readonly IC10InstructionKind Kind;
        private readonly string[] operands;

        public ReadOnlySpan<string> Operands => operands;

        public IC10Instruction(string opCode, IC10InstructionKind kind, ReadOnlySpan<string> operands)
        {
            OpCode = opCode;
            Kind = kind;
            this.operands = operands.ToArray();
        }

        public IC10Instruction ReplaceOperand(int operandIndex, string newValue)
        {
            if (operandIndex < 0 || operandIndex >= operands.Length) { throw new ArgumentOutOfRangeException(nameof(operandIndex)); }
            var newOperands = new string[operands.Length];
            Operands.CopyTo(newOperands);
            newOperands[operandIndex] = newValue;
            return new IC10Instruction(OpCode, Kind, newOperands);
        }

        public bool IsUnconditionalJump => Kind == IC10InstructionKind.Instruction && (OpCode == "j" || OpCode == "jal");

        public bool IsUnconditionalJumpNoReturn => Kind == IC10InstructionKind.Instruction && OpCode == "j";

        public override string ToString() => Kind == IC10InstructionKind.Instruction ? $"{OpCode.ToLowerInvariant()} {string.Join(" ", operands)}" : $"{OpCode}:";
    }

    internal class Optimiser
    {
        public string Optimise(string ic10)
        {
            var lines = ic10.Split(Environment.NewLine);
            return Optimise(lines);
        }

        private string Optimise(ReadOnlySpan<string> lines)
        {
            var instructions = new List<IC10Instruction>();
            foreach (var line in lines)
            {
                var cleanedLine = line.Trim();
                if (string.IsNullOrEmpty(cleanedLine)) { continue; }
                if (cleanedLine[^1] == ':')
                {
                    instructions.Add(new IC10Instruction(line[0..^1], IC10InstructionKind.Label, ReadOnlySpan<string>.Empty));
                    continue;
                }
                var pieces = cleanedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (pieces.Length < 1) { continue; }
                instructions.Add(new IC10Instruction(pieces[0], IC10InstructionKind.Instruction, pieces.AsSpan().Slice(1)));
            }
            Optimise(instructions);
            var sb = new StringBuilder();
            foreach (var instruction in instructions)
            {
                sb.AppendLine(instruction.ToString());
            }
            return sb.ToString().Trim();
        }

        private void Optimise(IList<IC10Instruction> instructions)
        {
            bool changesMade;
            do
            {
                changesMade = false;
                changesMade |= Optimise_RedundantStackUsage(instructions);
                changesMade |= Optimise_RedundantJumps(instructions);
                changesMade |= Optimise_InlineSmallSections(instructions);
                changesMade |= Optimise_ChainLabels(instructions);
                changesMade |= Optimise_UnusedLabels(instructions);
            }
            while (changesMade);
        }

        private bool Optimise_RedundantStackUsage(IList<IC10Instruction> instructions)
        {
            for (int i = 0; i < instructions.Count - 1; ++i)
            {
                var instruction = instructions[i];
                var nextInstruction = instructions[i + 1];
                if (instruction.OpCode == "pop" && nextInstruction.OpCode == "push" && instruction.Operands[0] == "ra" && nextInstruction.Operands[0] == "ra")
                {
                    instructions.RemoveAt(i + 1);
                    instructions.RemoveAt(i);
                    --i;
                }
            }
            return false;
        }

        private bool Optimise_RedundantJumps(IList<IC10Instruction> instructions)
        {
            bool changesMade = false;
            for (int i = 0; i < instructions.Count - 1; ++i)
            {
                var instruction = instructions[i];
                var nextInstruction = instructions[i + 1];
                if (instruction.OpCode == "j" && nextInstruction.Kind == IC10InstructionKind.Label && instruction.Operands[0] == nextInstruction.OpCode)
                {
                    instructions.RemoveAt(i);
                    --i;
                    changesMade |= true;
                }
            }
            return changesMade;
        }

        private bool Optimise_InlineSmallSections(IList<IC10Instruction> instructions)
        {
            bool changesMade = false;
            for (int i = 0; i < instructions.Count - 1; ++i)
            {
                var instruction = instructions[i];
                var nextInstruction = instructions[i + 1];
                if (instruction.Kind == IC10InstructionKind.Label)
                {
                    var sectionSize = GetSectionFiniteSize(instructions, instruction.OpCode);
                    if (sectionSize == null || sectionSize > 1) { continue; }
                    var refs = FindLabelRefs(instructions, instruction.OpCode).ToArray();
                    foreach (var (instructionIndex, _) in refs)
                    {
                        if (instructions[instructionIndex].IsUnconditionalJumpNoReturn)
                        {
                            instructions[instructionIndex] = nextInstruction;
                            changesMade |= true;
                        }
                        // TODO: the ref could be a conditional jump e.g. begz, we could still inline the section IF the section only consists of a single "j"
                    }
                    
                }
            }
            return changesMade;
        }

        private bool Optimise_ChainLabels(IList<IC10Instruction> instructions)
        {
            bool changesMade = false;
            for (int i = 0; i < instructions.Count - 1; ++i)
            {
                var instruction = instructions[i];
                var nextInstruction = instructions[i + 1];
                if (instruction.Kind != IC10InstructionKind.Label || nextInstruction.Kind != IC10InstructionKind.Label) { continue; }
                FixupLabelRefs(instructions, nextInstruction.OpCode, instruction.OpCode);
                instructions.RemoveAt(i + 1);
                changesMade |= true;
            }
            return changesMade;
        }

        private bool Optimise_UnusedLabels(IList<IC10Instruction> instructions)
        {
            bool changesMade = false;
            for (int i = 0; i < instructions.Count; ++i)
            {
                var instruction = instructions[i];
                if (instruction.Kind == IC10InstructionKind.Label && !FindLabelRefs(instructions, instruction.OpCode).Any())
                {
                    instructions.RemoveAt(i);
                    --i;
                    changesMade |= true;
                }
            }
            return changesMade;
        }

        private bool FixupLabelRefs(IList<IC10Instruction> instructions, string labelName, string replaceLabelName)
        {
            bool changesMade = false;
            foreach (var (instructionIndex, operandIndex) in FindLabelRefs(instructions, labelName))
            {
                if (instructions[instructionIndex].Operands[operandIndex] != labelName) { continue; }
                instructions[instructionIndex] = instructions[instructionIndex].ReplaceOperand(operandIndex, replaceLabelName);
                changesMade |= true;
            }
            return changesMade;
        }

        private IEnumerable<(int instructionIndex, int operandIndex)> FindLabelRefs(IList<IC10Instruction> instructions, string labelName)
        {
            for (int i = 0; i < instructions.Count; ++i)
            {
                var instruction = instructions[i];
                for (int j = 0; j < instruction.Operands.Length; ++j)
                {
                    if (instruction.Operands[j] != labelName) { continue; }
                    yield return (i, j);
                }
            }
        }

        private int? GetSectionFiniteSize(IList<IC10Instruction> instructions, string labelName)
        {
            int? sectionStart = FindLabel(instructions, labelName);
            if (sectionStart == null) { return null; }
            int length = 0;
            for (int i = sectionStart.Value + 1; i < instructions.Count; ++i)
            {
                ++length;
                if (instructions[i].IsUnconditionalJumpNoReturn) { break; }
            }
            return length;
        }

        private int? FindLabel(IList<IC10Instruction> instructions, string labelName)
        {
            for (int i = 0; i < instructions.Count; ++i)
            {
                var instruction = instructions[i];
                if (instruction.Kind == IC10InstructionKind.Label && instruction.OpCode == labelName)
                {
                    return i;
                }
            }
            return null;
        }
    }
}
