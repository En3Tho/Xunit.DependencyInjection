﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;

namespace Xunit.DependencyInjection.Analyzer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class XunitDependencyInjectionAnalyzer : DiagnosticAnalyzer
    {
        private static readonly SymbolEqualityComparer SymbolComparer = SymbolEqualityComparer.Default;

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Rules.SupportedDiagnostics;

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(SymbolAnalyzer.RegisterCompilationStartAction);
        }

        private class SymbolAnalyzer
        {
            private readonly INamedTypeSymbol _hostBuilder;
            private readonly INamedTypeSymbol _assemblyName;
            private readonly INamedTypeSymbol _serviceCollection;
            private readonly INamedTypeSymbol _hostBuilderContext;
            private readonly string _startupName;

            private SymbolAnalyzer(INamedTypeSymbol type1, INamedTypeSymbol type2, INamedTypeSymbol type3, INamedTypeSymbol type4, string startupName)
            {
                _hostBuilder = type1;
                _assemblyName = type2;
                _serviceCollection = type3;
                _hostBuilderContext = type4;
                _startupName = startupName;
            }

            public static void RegisterCompilationStartAction(CompilationStartAnalysisContext csac)
            {
                var item = GetTypeSymbol(csac.Compilation);
                if (item.Item1 == null || item.Item2 == null || item.Item3 == null || item.Item4 == null || item.Item5 == null) return;

                var sta = csac.Compilation.Assembly.GetAttributes().FirstOrDefault(attr => SymbolComparer.Equals(attr.AttributeClass, item.Item5));

                string startupName;
#if DEBUG
                if (sta == null) startupName = $"{csac.Compilation.Assembly.Name}.Startup";
#else
                if (sta == null) return;
#endif
                else if (sta.ConstructorArguments.Length == 1)
                {
                    if (sta.ConstructorArguments[0].Value is INamedTypeSymbol st &&
                        SymbolComparer.Equals(st.ContainingAssembly, csac.Compilation.Assembly))
                        startupName = $"{st.ContainingNamespace.Name}.{st.Name}";
                    else return;
                }
                else if (sta.ConstructorArguments.Length == 2 &&
                         sta.ConstructorArguments[0].Value is string name &&
                         (sta.ConstructorArguments[1].Value == null ||
                         sta.ConstructorArguments[1].Value is string ns &&
                         ns == csac.Compilation.AssemblyName))
                    startupName = name;
                else return;

                // See https://github.com/dotnet/roslyn/blob/master/docs/analyzers/Analyzer%20Actions%20Semantics.md for more information
                csac.RegisterSymbolAction(new SymbolAnalyzer(item.Item1, item.Item2, item.Item3, item.Item4, startupName).AnalyzeSymbol, SymbolKind.Method, SymbolKind.NamedType);
            }

            private static (INamedTypeSymbol, INamedTypeSymbol, INamedTypeSymbol, INamedTypeSymbol, INamedTypeSymbol) GetTypeSymbol(Compilation compilation)
            {
                var ass = compilation.References
                    .Select(compilation.GetAssemblyOrModuleSymbol)
                    .OfType<IAssemblySymbol>()
                    .ToArray();

                INamedTypeSymbol GetTypeSymbol0(string name) => ass.Select(assemblySymbol => assemblySymbol.GetTypeByMetadataName(name)).FirstOrDefault(t => t != null)!;

                return (GetTypeSymbol0("Microsoft.Extensions.Hosting.IHostBuilder"),
                 GetTypeSymbol0(typeof(AssemblyName).FullName),
                 GetTypeSymbol0("Microsoft.Extensions.DependencyInjection.IServiceCollection"),
                 GetTypeSymbol0("Microsoft.Extensions.Hosting.HostBuilderContext"),
                 GetTypeSymbol0("Xunit.DependencyInjection.StartupTypeAttribute"));
            }

            private void AnalyzeSymbol(SymbolAnalysisContext context)
            {
                switch (context.Symbol)
                {
                    case INamedTypeSymbol type:
                        if (!IsStartup(type)) return;

                        var ctors = type.InstanceConstructors.Where(ctor => ctor.DeclaredAccessibility == Accessibility.Public).ToArray();
                        if (ctors.Length > 1)
                            foreach (var ctor in ctors)
                                context.ReportDiagnostic(Diagnostic.Create(Rules.MultipleConstructor, ctor.Locations[0], ctor.Name));

                        AnalyzeOverride(context, type, "CreateHostBuilder");
                        AnalyzeOverride(context, type, "ConfigureHost");
                        AnalyzeOverride(context, type, "ConfigureServices");
                        AnalyzeOverride(context, type, "Configure");

                        return;
                    case IMethodSymbol method:
                        if (!IsStartup(method.ContainingType) || method.DeclaredAccessibility != Accessibility.Public) return;

                        switch (method.MethodKind)
                        {
                            case MethodKind.Constructor:
                                AnalyzeCtor(context, method);

                                return;
                            case MethodKind.Ordinary:
                                if ("CreateHostBuilder".Equals(method.Name, StringComparison.OrdinalIgnoreCase))
                                    AnalyzeCreateHostBuilder(context, method);
                                else if ("ConfigureHost".Equals(method.Name, StringComparison.OrdinalIgnoreCase))
                                    AnalyzeConfigureHost(context, method);
                                else if ("ConfigureServices".Equals(method.Name, StringComparison.OrdinalIgnoreCase))
                                    AnalyzeConfigureServices(context, method);
                                else if ("Configure".Equals(method.Name, StringComparison.OrdinalIgnoreCase))
                                    AnalyzeConfigure(context, method);

                                return;
                        }

                        return;
                }
            }

            private bool IsStartup(ISymbol type) => $"{type.ContainingNamespace.Name}.{type.Name}" == _startupName;

            private static void AnalyzeOverride(SymbolAnalysisContext context, INamespaceOrTypeSymbol type, string methodName)
            {
                var methods = type.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => m.MethodKind == MethodKind.Ordinary &&
                                m.DeclaredAccessibility == Accessibility.Public &&
                                methodName.Equals(m.Name, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (methods.Length < 2) return;

                foreach (var method in methods)
                    context.ReportDiagnostic(Diagnostic.Create(Rules.MultipleOverloads, method.Locations[0], method.Name));
            }

            private static void AnalyzeStatic(SymbolAnalysisContext context, IMethodSymbol method)
            {
                if (method.IsStatic)
                    context.ReportDiagnostic(Diagnostic.Create(Rules.NotStaticMethod, method.Locations[0], method.Name));
            }

            private static void AnalyzeReturnType(SymbolAnalysisContext context, IMethodSymbol method, ITypeSymbol? returnType)
            {
                if (returnType == null)
                {
                    if (!method.ReturnsVoid)
                        context.ReportDiagnostic(Diagnostic.Create(Rules.NoReturnType, method.Locations[0], method.Name));
                }
                else if (!IsAssignableFrom(returnType, method.ReturnType))
                    context.ReportDiagnostic(Diagnostic.Create(Rules.ReturnTypeAssignableTo, method.Locations[0], method.Name, returnType));
            }

            private static bool IsAssignableFrom(ITypeSymbol returnType, ITypeSymbol type)
            {
                if (SymbolComparer.Equals(returnType, type)) return true;

                if (returnType.TypeKind == TypeKind.Interface)
                    return type.AllInterfaces.Any(i => SymbolComparer.Equals(returnType, i));

                do
                {
                    if (SymbolComparer.Equals(type, returnType)) return true;
                } while ((type = type.BaseType!) != null);

                return false;
            }

            private static void AnalyzeCtor(SymbolAnalysisContext context, IMethodSymbol method)
            {
                if (method.Parameters.Length > 0)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rules.ParameterlessConstructor, method.Locations[0], method.Name));
                }
            }

            private void AnalyzeCreateHostBuilder(SymbolAnalysisContext context, IMethodSymbol method)
            {
                AnalyzeStatic(context, method);

                AnalyzeReturnType(context, method, _hostBuilder);

                if (method.Parameters.Length == 0) return;

                if (method.Parameters.Length > 1 || !SymbolComparer.Equals(_assemblyName, method.Parameters[0].Type))
                    context.ReportDiagnostic(Diagnostic.Create(Rules.ParameterlessOrSingleParameter, method.Locations[0], method.Name, _assemblyName.Name));
            }

            private void AnalyzeConfigureHost(SymbolAnalysisContext context, IMethodSymbol method)
            {
                AnalyzeStatic(context, method);

                AnalyzeReturnType(context, method, null);

                var parameters = method.Parameters;
                if (parameters.Length != 1 || !SymbolComparer.Equals(parameters[0].Type, _hostBuilder))
                    context.ReportDiagnostic(Diagnostic.Create(Rules.SingleParameter, method.Locations[0], method.Name, _hostBuilder.Name));
            }

            private void AnalyzeConfigureServices(SymbolAnalysisContext context, IMethodSymbol method)
            {
                AnalyzeStatic(context, method);

                AnalyzeReturnType(context, method, null);

                var parameters = method.Parameters;

                if (parameters.Length == 1 && SymbolComparer.Equals(parameters[0].Type, _serviceCollection)) return;

                if (parameters.Length == 2 && SymbolComparer.Equals(parameters[0].Type, _serviceCollection) && SymbolComparer.Equals(parameters[1].Type, _hostBuilderContext)) return;

                if (parameters.Length == 2 && SymbolComparer.Equals(parameters[1].Type, _serviceCollection) && SymbolComparer.Equals(parameters[0].Type, _hostBuilderContext)) return;

                context.ReportDiagnostic(Diagnostic.Create(Rules.ConfigureServices, method.Locations[0], method.Name));
            }

            private static void AnalyzeConfigure(SymbolAnalysisContext context, IMethodSymbol method)
            {
                AnalyzeStatic(context, method);

                AnalyzeReturnType(context, method, null);
            }
        }
    }
}
