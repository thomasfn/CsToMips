﻿using System;
using System.Reflection;
using System.IO;
using System.Linq;

namespace CsToMips
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var programs = Assembly.GetExecutingAssembly().DefinedTypes
                .Where(t => typeof(IStationeersProgram).IsAssignableFrom(t) && !t.IsAbstract && t.IsClass)
                .ToArray();
            foreach (var program in programs)
            {
                var compiler = new Compiler.Compiler(program, Compiler.CompilerOptions.Default);
                var ic10 = compiler.Compile();
                var filename = $"{program.Name}.ic10";
                File.WriteAllText(filename, ic10);
                Console.WriteLine($"Compiled {program.FullName}, wrote to '{filename}'");
            }
        }
    }
}
