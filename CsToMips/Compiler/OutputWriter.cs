using System.Text;

namespace CsToMips.Compiler
{
    internal readonly struct InstructionOutput
    {
        public readonly string? Comment;
        public readonly string? Code;
        public readonly bool WithLabel;

        public InstructionOutput(string? comment, string? code, bool withLabel)
        {
            Comment = comment;
            Code = code;
            WithLabel = withLabel;
        }
    }

    internal class OutputWriter
    {
        private readonly InstructionOutput[] outputs;

        public string LabelPrefix { get; set; } = "";

        public string Preamble { get; set; } = "";

        public string Postamble { get; set; } = "";

        public InstructionOutput this[int instructionIndex] { get => outputs[instructionIndex]; }

        public OutputWriter(int numOutputs)
        {
            outputs = new InstructionOutput[numOutputs];
        }

        public void SetCode(int instructionIndex, string code)
        {
            var currentOutput = outputs[instructionIndex];
            outputs[instructionIndex] = new InstructionOutput(currentOutput.Comment, code, currentOutput.WithLabel);
        }

        public void SetComment(int instructionIndex, string comment)
        {
            var currentOutput = outputs[instructionIndex];
            outputs[instructionIndex] = new InstructionOutput(comment, currentOutput.Code, currentOutput.WithLabel);
        }

        public void SetWithLabel(int instructionIndex, bool withLabel)
        {
            var currentOutput = outputs[instructionIndex];
            outputs[instructionIndex] = new InstructionOutput(currentOutput.Comment, currentOutput.Code, withLabel);
        }

        public string GetLabel(int instructionIndex)
        {
            return $"{LabelPrefix}_il_{instructionIndex}";
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Preamble);
            int i = 0;
            foreach (var output in outputs)
            {
                if (!string.IsNullOrEmpty(output.Comment))
                {
                    sb.AppendLine($"#{output.Comment}");
                }
                if (output.WithLabel)
                {
                    sb.AppendLine($"{GetLabel(i)}:");
                }
                if (!string.IsNullOrEmpty(output.Code))
                {
                    sb.AppendLine($"{output.Code}");
                }
                ++i;
            }
            sb.AppendLine(Postamble);
            return sb.ToString().Trim();
        }
    }
}
