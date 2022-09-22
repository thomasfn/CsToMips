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
                //changesMade |= Optimise_RedundantStackUsage(instructions);
                //changesMade |= Optimise_RedundantJumps(instructions);
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
            for (int i = 0; i < instructions.Count - 1; ++i)
            {
                var instruction = instructions[i];
                var nextInstruction = instructions[i + 1];
                if (instruction.OpCode == "j" && nextInstruction.Kind == IC10InstructionKind.Label && instruction.Operands[0] == nextInstruction.OpCode)
                {
                    instructions.RemoveAt(i);
                    --i;
                }
            }
            return false;
        }
    }
}
