using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using System.Linq;

namespace MongoSpyglass.Proxy.SourceGenerators
{
    [Generator]
    public class MessageMutatorGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (!Debugger.IsAttached)
            {
                Debugger.Launch();
            }

            foreach (var syntaxTree in context.Compilation.SyntaxTrees)
            {
                var model = context.Compilation.GetSemanticModel(syntaxTree);
                var refStructs = syntaxTree.GetRoot()
                    .DescendantNodes()
                    .OfType<StructDeclarationSyntax>()
                    .Where(s => s.Modifiers.Any(SyntaxKind.RefKeyword));

                foreach (var refStruct in refStructs)
                {
                    var symbol = model.GetDeclaredSymbol(refStruct);
                    if (symbol == null ||
                            !symbol.ContainingNamespace
                                  .ToDisplayString()
                                  .StartsWith("MongoSpyglass.Proxy.WireProtocol.Raw"))
                        continue;

                    var sourceCode = GenerateMutatorCode(symbol);
                    context.AddSource($"{symbol.Name}.MutatorBase", SourceText.From(sourceCode, Encoding.UTF8));
                }
            }
        }

        private string GenerateMutatorCode(INamedTypeSymbol symbol)
        {
            var sb = new StringBuilder();
            var candidatesForMutators = new Queue<INamedTypeSymbol>();

            sb.AppendLine("using System;");
            sb.AppendLine($"namespace {symbol.ContainingNamespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    public abstract class {symbol.Name}MutatorBase");
            sb.AppendLine("    {");

            // Kick off BFS
            candidatesForMutators.Enqueue(symbol);
            while (candidatesForMutators.Count > 0)
            {
                var currentSymbol = candidatesForMutators.Dequeue();
                GenerateMutateMethod(currentSymbol, sb, candidatesForMutators);
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private void GenerateMutateMethod(INamedTypeSymbol symbol, StringBuilder sb, Queue<INamedTypeSymbol> candidatesForMutators)
        {
            sb.AppendLine($"        public void Mutate(ref {symbol.Name} item)");
            sb.AppendLine("        {");

            foreach (var member in symbol.GetMembers().OfType<IFieldSymbol>())
            {
                var memberType = member.Type.ToDisplayString();
                var memberName = member.Name;

                // If member type is another ref struct, enqueue it for BFS and call its mutator
                if (member.Type is INamedTypeSymbol nestedSymbol && nestedSymbol.TypeKind == TypeKind.Struct)
                {
                    sb.AppendLine($"            Create{nestedSymbol.Name}Mutator().Mutate(ref item.{memberName});");
                    candidatesForMutators.Enqueue(nestedSymbol);
                }
                else if (IsPrimitiveType(member.Type)) // For primitive types
                {
                    sb.AppendLine($"            item.{memberName} = default;");  // reset to default, or any other mutation logic
                }
                else // For other non-primitive types
                {
                    sb.AppendLine($"            // Custom mutation logic for {memberName} of type {memberType}");
                }
            }

            sb.AppendLine("        }");
        }

        private bool IsPrimitiveType(ITypeSymbol type)
        {
            return type.SpecialType switch
            {
                SpecialType.System_Byte => true,
                SpecialType.System_SByte => true,
                SpecialType.System_Int16 => true,
                SpecialType.System_UInt16 => true,
                SpecialType.System_Int32 => true,
                SpecialType.System_UInt32 => true,
                SpecialType.System_Int64 => true,
                SpecialType.System_UInt64 => true,
                SpecialType.System_Single => true,
                SpecialType.System_Double => true,
                SpecialType.System_Char => true,
                SpecialType.System_Boolean => true,
                _ => false,
            };
        }
    }
}
