using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace MongoSpyglass.Proxy.SourceGenerators
{
    [Generator]
    public class MessageVisitorGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var allSyntaxTrees = context.Compilation.SyntaxTrees;
            context.ReportDiagnostic(Diagnostic.Create("SourceGenerator", "SG0001", "Starting generation of ref struct visitors", DiagnosticSeverity.Info, DiagnosticSeverity.Info, true, 1));
            //if (!Debugger.IsAttached)
            //{
            //    Debugger.Launch();
            //}

            foreach (var syntaxTree in allSyntaxTrees)
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

                    // Generate the visitor for this ref struct
                    var sourceCode = GenerateAbstractVisitorCode(symbol);
                    context.AddSource($"{symbol.Name}.VisitorBase", SourceText.From(sourceCode, Encoding.UTF8));
                }
            }
        }

        private string GenerateAbstractVisitorCode(INamedTypeSymbol symbol)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine($"namespace {symbol.ContainingNamespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    public abstract class {symbol.Name}VisitorBase");
            sb.AppendLine("    {");

            // Generate abstract methods for each field
            foreach (var member in symbol.GetMembers().OfType<IFieldSymbol>())
            {
                var memberType = member.Type.ToDisplayString();
                var memberName = member.Name;
                sb.AppendLine($"        public abstract void Visit{memberName}({memberType} value);");
            }

            // Generate the Visit method that calls the abstract methods
            sb.AppendLine($"        public void Visit(ref {symbol.Name} item)");
            sb.AppendLine("        {");
            foreach (var member in symbol.GetMembers().OfType<IFieldSymbol>())
            {
                var memberName = member.Name;
                sb.AppendLine($"            Visit{memberName}(item.{memberName});");
            }
            sb.AppendLine("        }");

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

    }
}