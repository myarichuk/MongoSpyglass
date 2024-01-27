using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using System.Linq;

namespace MongoSpyglass.Proxy
{
    [Generator]
    public class MessageMutatorGenerator : ISourceGenerator
    {
        private static readonly string[] NetCoreAssemblies = { 
            "System.Private.CoreLib", 
            "System.Runtime", 
            "netstandard" 
        };
        private readonly HashSet<string> _candidatesForMutateors = new();

        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context)
        {
            //if (!Debugger.IsAttached)
            //{
            //    Debugger.Launch();
            //}

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

                    _candidatesForMutateors.Add(symbol.ToDisplayString());
                }
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

            sb.AppendLine("using System;");
            sb.AppendLine($"namespace {symbol.ContainingNamespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    public abstract class {symbol.Name}MutatorBase");
            sb.AppendLine("    {");

            GenerateMutateMethods(symbol, sb);

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private void GenerateMutateMethods(INamedTypeSymbol symbol, StringBuilder sb)
        {
            foreach (var member in 
                     symbol.GetMembers()
                           .OfType<IFieldSymbol>())
            {
                //var memberType = member.Type.ToDisplayString();
                //var memberName = member.Name;

                if (!IsPrimitive(member.Type) &&
                    !IsCoreBclType(member.Type) &&
                    member.Type is INamedTypeSymbol { TypeKind: TypeKind.Struct, IsRefLikeType: true } nestedSymbol)
                {
                    sb.AppendLine($"        protected abstract {nestedSymbol.Name}MutatorBase Create{nestedSymbol.Name}Mutator();");
                }
            }

            foreach (var member in symbol.GetMembers()
                         .OfType<IFieldSymbol>()
                         .Where(member => 
                             !_candidatesForMutateors.Contains(member.ToDisplayString()) &&
                             (IsCoreBclType(member.Type) || 
                             IsPrimitive(member.Type) ||
                             member.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum })))
            {
                var memberType = member.Type.ToDisplayString();
                var memberName = member.Name;
                sb.AppendLine($"        public abstract {memberType} Mutate{memberName}({memberType} value);");
            }

            sb.AppendLine();
            sb.AppendLine($"        public void Mutate(ref {symbol.Name} item)");
            sb.AppendLine("        {");

            foreach (var member in symbol.GetMembers()
                         .OfType<IFieldSymbol>()
                         .Where(m => 
                             m.Type is { IsAnonymousType: false }))
            {
                var memberName = member.Name;

                // If member type is another ref struct, delegate to its visitor using the factory method
                if (member.Type is INamedTypeSymbol { TypeKind: TypeKind.Struct } nestedSymbol && 
                    !IsCoreBclType(member.Type))
                {
                    sb.AppendLine($"            item.{memberName} = new(); //give default value before passing it through 'ref' param");
                    sb.AppendLine($"            Create{nestedSymbol.Name}Mutator().Mutate(ref item.{memberName});");
                }
                else // For other non-primitive types, just visit the field
                {
                    sb.AppendLine($"            item.{memberName} = Mutate{memberName}(item.{memberName});");
                }
            }

            sb.AppendLine("        }");
        }

        private static bool IsPrimitive(ITypeSymbol type)
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

        private static bool IsCoreBclType(ITypeSymbol type) => 
            NetCoreAssemblies.Contains(type.ContainingAssembly.Name);
    }
}
