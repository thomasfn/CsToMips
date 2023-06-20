using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CsToMips.MIPS
{
    public readonly struct Label : IEquatable<Label>
    {
        public readonly string Text;
        public readonly int InstructionIndex;

        public Label(string text, int instructionIndex)
        {
            Text = text;
            InstructionIndex = instructionIndex;
        }

        public override bool Equals(object? obj) => obj is Label label && Equals(label);

        public bool Equals(Label other)
            => Text == other.Text
            && InstructionIndex == other.InstructionIndex;

        public override int GetHashCode() => HashCode.Combine(Text, InstructionIndex);

        public static bool operator ==(Label left, Label right) => left.Equals(right);

        public static bool operator !=(Label left, Label right) => !(left == right);

        public override string ToString() => $"{Text}:";
    }

    public readonly struct Program
    {
        public static readonly Program Blank = new(ReadOnlySpan<Instruction>.Empty, ReadOnlySpan<Label>.Empty);

        private readonly Instruction[] instructions;
        private readonly Label[] labels;

        public ReadOnlySpan<Instruction> Instructions => instructions;

        public ReadOnlySpan<Label> Labels => labels;

        public Program(ReadOnlySpan<Instruction> instructions, ReadOnlySpan<Label> labels)
        {
            this.instructions = instructions.ToArray();
            this.labels = labels.ToArray();
        }

        public int? GetNextInstruction(int lineIndex)
        {
            for (int instructionIndex = 0; instructionIndex < instructions.Length; ++instructionIndex)
            {
                if (instructions[instructionIndex].LineIndex >= lineIndex)
                {
                    return instructionIndex;
                }
            }
            return null;
        }

        public int? FindLabel(string text)
        {
            for (int i = 0; i < labels.Length; ++i)
            {
                if (labels[i].Text.Equals(text, StringComparison.InvariantCultureIgnoreCase)) { return i; }
            }
            return null;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < instructions.Length; ++i)
            {
                foreach (var label in labels)
                {
                    if (label.InstructionIndex != i) { continue; }
                    sb.AppendLine(label.ToString());
                }
                sb.AppendLine(instructions[i].ToString());
            }
            return sb.ToString();
        }

        public static Program Parse(ReadOnlySpan<string> lines)
        {
            var instructions = new List<Instruction>();
            var labels = new List<Label>();
            for (int i = 0; i < lines.Length; ++i)
            {
                var line = lines[i];
                var cleanedLine = line.Trim();
                if (string.IsNullOrEmpty(cleanedLine)) { continue; }
                if (cleanedLine.EndsWith(':'))
                {
                    labels.Add(new Label(cleanedLine[..^1], instructions.Count));
                    continue;
                }
                instructions.Add(Instruction.Parse(i, line));
            }
            return new Program(instructions.ToArray(), labels.ToArray());
        }
    }

}
