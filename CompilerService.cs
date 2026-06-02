using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Net;
using System.Text.Json;

public class CompilerService
{
    private readonly HttpClient _httpClient;
    private List<MetadataReference>? _references;

    public CompilerService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task InitializeAsync()
    {
        if (_references != null) return;

        var bootJson = await _httpClient.GetStringAsync("_framework/blazor.boot.json");
        using var doc = JsonDocument.Parse(bootJson);
        var resources = doc.RootElement.GetProperty("resources");

        // Prefixes of assemblies required for basic C# console programs
        var needed = new[]
        {
            "System.Private.CoreLib.",
            "System.Runtime.",
            "System.Console.",
            "System.Collections.",
            "System.Linq.",
            "System.Text.RegularExpressions.",
            "System.Text.Json.",
            "System.IO.",
            "netstandard."
        };

        var filenames = new List<string>();

        foreach (var section in new[] { "coreAssembly", "assembly" })
        {
            if (!resources.TryGetProperty(section, out var el)) continue;
            filenames.AddRange(
                el.EnumerateObject()
                  .Select(p => p.Name)
                  .Where(name => needed.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            );
        }

        var references = new List<MetadataReference>();

        foreach (var filename in filenames)
        {
            var response = await _httpClient.GetAsync($"_framework/{filename}");
            if (!response.IsSuccessStatusCode) continue;

            var bytes = await response.Content.ReadAsByteArrayAsync();
            references.Add(MetadataReference.CreateFromImage(bytes));
        }

        if (references.Count == 0)
            throw new InvalidOperationException("No framework metadata references could be loaded from _framework.");

        _references = references;
    }

    public async Task<string> CompileAndRun(string code)
    {
        await InitializeAsync();

        var syntaxTree = CSharpSyntaxTree.ParseText(code,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

        var globalUsings = CSharpSyntaxTree.ParseText("""
            global using System;
            global using System.Collections.Generic;
            global using System.Linq;
            global using System.Text;
            """, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

        var compilation = CSharpCompilation.Create("UserAssembly")
            .WithOptions(new CSharpCompilationOptions(OutputKind.ConsoleApplication))
            .AddReferences(_references!)
            .AddSyntaxTrees(globalUsings, syntaxTree);

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"Error: {d.GetMessage()}");
            return string.Join("\n", errors);
        }

        var outputWriter = new StringWriter();
        var previousOut = Console.Out;
        Console.SetOut(outputWriter);

        try
        {
            ms.Seek(0, SeekOrigin.Begin);
            var assembly = Assembly.Load(ms.ToArray());
            var entryPoint = assembly.EntryPoint;
            entryPoint?.Invoke(null, entryPoint.GetParameters().Length > 0
                ? new object[] { Array.Empty<string>() }
                : null);
        }
        catch (Exception ex)
        {
            outputWriter.WriteLine($"Runtime exception: {ex.InnerException?.Message ?? ex.Message}");
        }
        finally
        {
            Console.SetOut(previousOut);
        }

        var output = outputWriter.ToString();
        return string.IsNullOrWhiteSpace(output) ? "(no output)" : output;
    }
}