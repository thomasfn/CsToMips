namespace CsToMips.Compiler
{
    internal readonly struct RegisterAllocations
    {
        public const int NumTotal = 16;

        private readonly uint allocated;

        public int NumAllocated
        {
            get
            {
                int num = 0;
                for (int i = 0; i < NumTotal; ++i)
                {
                    if (IsAllocated(i)) { ++num; }
                }
                return num;
            }
        }

        public int NumFree => NumTotal - NumAllocated;

        private RegisterAllocations(uint allocated)
        {
            this.allocated = allocated;
        }

        public bool IsAllocated(int registerIndex)
        {
            if (registerIndex < 0 || registerIndex >= NumTotal) { throw new ArgumentOutOfRangeException(nameof(registerIndex)); }
            return (allocated >> registerIndex & 1) == 1;
        }

        public RegisterAllocations Free(int registerIndex)
        {
            if (registerIndex < 0 || registerIndex >= NumTotal) { throw new ArgumentOutOfRangeException(nameof(registerIndex)); }
            return new RegisterAllocations(allocated & ~(1u << registerIndex));
        }

        public RegisterAllocations Allocate(int registerIndex)
        {
            if (registerIndex < 0 || registerIndex >= NumTotal) { throw new ArgumentOutOfRangeException(nameof(registerIndex)); }
            return new RegisterAllocations(allocated | 1u << registerIndex);
        }

        public RegisterAllocations Allocate(out int allocatedRegisterIndex)
        {
            allocatedRegisterIndex = -1;
            for (int i = 0; i < NumTotal; ++i)
            {
                if (IsAllocated(i)) { continue; }
                allocatedRegisterIndex = i;
                break;
            }
            if (allocatedRegisterIndex == -1) { throw new InvalidOperationException($"No free registers"); }
            return Allocate(allocatedRegisterIndex);
        }

        public static RegisterAllocations operator &(in RegisterAllocations lhs, in RegisterAllocations rhs)
            => new RegisterAllocations(lhs.allocated & rhs.allocated);

        public static RegisterAllocations operator |(in RegisterAllocations lhs, in RegisterAllocations rhs)
            => new RegisterAllocations(lhs.allocated | rhs.allocated);

        public static RegisterAllocations operator ~(in RegisterAllocations lhs)
           => new RegisterAllocations(~lhs.allocated);

        public static bool operator ==(in RegisterAllocations lhs, in RegisterAllocations rhs)
            => lhs.allocated == rhs.allocated;

        public static bool operator !=(in RegisterAllocations lhs, in RegisterAllocations rhs)
            => lhs.allocated != rhs.allocated;

        public override int GetHashCode()
            => allocated.GetHashCode();

        public override bool Equals(object? obj) => obj is RegisterAllocations registerAllocations && this == registerAllocations;
    }
}
