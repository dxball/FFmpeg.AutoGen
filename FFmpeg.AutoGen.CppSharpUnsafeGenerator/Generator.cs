﻿using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CppSharp;
using CppSharp.AST;
using CppSharp.Parser;
using FFmpeg.AutoGen.CppSharpUnsafeGenerator.Definitions;
using FFmpeg.AutoGen.CppSharpUnsafeGenerator.Processors;
using ClangParser = CppSharp.ClangParser;
using MacroDefinition = FFmpeg.AutoGen.CppSharpUnsafeGenerator.Definitions.MacroDefinition;

namespace FFmpeg.AutoGen.CppSharpUnsafeGenerator
{
    internal class Generator
    {
        private bool _hasParsingErrors;
        private ASTProcessor _astProcessor;

        public string[] Defines { get; set; } = { };
        public string[] IncludeDirs { get; set; } = { };
        public FunctionExport[] Exports { get; set; }

        public string Namespace { get; set; }
        public string ClassName { get; set; }

        public void Parse(params string[] sourceFiles)
        {
            _hasParsingErrors = false;
            var context = ParseInternal(sourceFiles);
            if (_hasParsingErrors)
                throw new InvalidOperationException();

            Process(context);
        }

        public void WriteEnums(string outputFile)
        {
            WriteInternal(outputFile, (units, writer) =>
            {
                units.OfType<EnumerationDefinition>()
                    .OrderBy(x => x.Name)
                    .ToList()
                    .ForEach(x =>
                    {
                        writer.Write(x);
                        writer.WriteLine();
                    });
            });
        }

        public void WriteDelegates(string outputFile)
        {
            WriteInternal(outputFile, (units, writer) =>
            {
                units.OfType<DelegateDefinition>().ToList().ForEach(x =>
                {
                    writer.Write(x);
                    writer.WriteLine();
                });
            });
        }

        public void WriteMacros(string outputFile)
        {
            WriteInternal(outputFile, (units, writer) =>
            {
                writer.WriteLine($"public unsafe static partial class {ClassName}");
                using (writer.BeginBlock())
                    units.OfType<MacroDefinition>()
                        .OrderBy(x => x.Name)
                        .ToList()
                        .ForEach(writer.Write);
            });
        }

        public void WriteFunctions(string outputFile)
        {
            WriteInternal(outputFile, (units, writer) =>
            {
                writer.WriteLine($"public unsafe static partial class {ClassName}");
                using (writer.BeginBlock())
                    units.OfType<FunctionDefinition>()
                        .OrderBy(x => x.LibraryName)
                        .ThenBy(x => x.Name)
                        .ToList()
                        .ForEach(x =>
                        {
                            writer.Write(x);
                            writer.WriteLine();
                        });
            });
        }

        public void WriteArrays(string outputFile)
        {
            WriteInternal(outputFile, (units, writer) =>
            {
                writer.WriteLine("#pragma warning disable 169");
                writer.WriteLine();
                units.OfType<FixedArrayDefinition>()
                    .OrderBy(x => x.Size)
                    .ThenBy(x => x.Name)
                    .ToList().ForEach(x =>
                    {
                        writer.Write(x);
                        writer.WriteLine();
                    });
            });
        }

        public void WriteStructures(string outputFile)
        {
            WriteInternal(outputFile, (units, writer) =>
            {
                units.OfType<StructureDefinition>()
                    .Where(x => x.IsComplete)
                    .ToList()
                    .ForEach(x =>
                    {
                        writer.Write(x);
                        writer.WriteLine();
                    });
            });
        }

        public void WriteIncompleteStructures(string outputFile)
        {
            WriteInternal(outputFile, (units, writer) =>
            {
                units.OfType<StructureDefinition>()
                    .Where(x => !x.IsComplete)
                    .ToList()
                    .ForEach(x =>
                    {
                        writer.Write(x);
                        writer.WriteLine();
                    });
            });
        }

        private ASTContext ParseInternal(string[] sourceFiles)
        {
            var parserOptions = new ParserOptions
            {
                Verbose = true,
                ASTContext = new CppSharp.Parser.AST.ASTContext(),
                LanguageVersion = LanguageVersion.C99_GNU
            };

            parserOptions.SetupMSVC(VisualStudioVersion.Latest);
            
            foreach (var includeDir in IncludeDirs) parserOptions.AddIncludeDirs(includeDir);

            foreach (var define in Defines) parserOptions.AddDefines(define);
            
            var clangParser = new ClangParser(new CppSharp.Parser.AST.ASTContext());
            clangParser.SourcesParsed += OnSourceFileParsed;
            clangParser.ParseSourceFiles(sourceFiles, parserOptions);
            return ClangParser.ConvertASTContext(clangParser.ASTContext);
        }

        private void OnSourceFileParsed(IEnumerable<string> files, ParserResult result)
        {
            switch (result.Kind)
            {
                case ParserResultKind.Success:
                    Diagnostics.Message("Parsed '{0}'", string.Join(", ", files));
                    break;
                case ParserResultKind.Error:
                    Diagnostics.Error("Error parsing '{0}'", string.Join(", ", files));
                    _hasParsingErrors = true;
                    break;
                case ParserResultKind.FileNotFound:
                    Diagnostics.Error("A file from '{0}' was not found", string.Join(",", files));
                    break;
            }
            for (uint i = 0; i < result.DiagnosticsCount; ++i)
            {
                var diagnostics = result.GetDiagnostics(i);

                var message = $"{diagnostics.FileName}({diagnostics.LineNumber},{diagnostics.ColumnNumber}): " +
                              $"{diagnostics.Level.ToString().ToLower()}: {diagnostics.Message}";
                Diagnostics.Message(message);
            }
        }

        private void Process(ASTContext context)
        {
            if (_astProcessor == null)
                _astProcessor = new ASTProcessor {FunctionExportMap = Exports.ToDictionary(x => x.Name)};
            _astProcessor.Process(context.TranslationUnits.Where(x => !x.IsSystemHeader));
        }

        private void WriteInternal(string outputFile, Action<IReadOnlyList<IDefinition>, Writer> execute)
        {
            using (var streamWriter = File.CreateText(outputFile))
            using (var textWriter = new IndentedTextWriter(streamWriter))
            {
                var writer = new Writer(textWriter);
                writer.WriteLine("using System;");
                writer.WriteLine("using System.Runtime.InteropServices;");
                writer.WriteLine();
                writer.WriteLine($"namespace {Namespace}");
                using (writer.BeginBlock()) execute(_astProcessor.Units, writer);
            }
        }
    }
}