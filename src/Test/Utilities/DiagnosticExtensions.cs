﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis
{
    public static class DiagnosticExtensions
    {
        private const int EN_US = 1033;
        public static Func<Exception, DiagnosticAnalyzer, bool> AlwaysCatchAnalyzerExceptions = (e, a) => true;
        public static Func<Exception, DiagnosticAnalyzer, bool> DonotCatchAnalyzerExceptions = (e, a) => e is OperationCanceledException;

        /// <summary>
        /// This is obsolete. Use Verify instead.
        /// </summary>
        public static void VerifyErrorCodes(this IEnumerable<Diagnostic> actual, params DiagnosticDescription[] expected)
        {
            Verify(actual, expected, errorCodeOnly: true);
        }

        public static void VerifyErrorCodes(this ImmutableArray<Diagnostic> actual, params DiagnosticDescription[] expected)
        {
            VerifyErrorCodes((IEnumerable<Diagnostic>)actual, expected);
        }

        internal static void Verify(this DiagnosticBag actual, params DiagnosticDescription[] expected)
        {
            Verify(actual.AsEnumerable(), expected, errorCodeOnly: false);
        }

        public static void Verify(this IEnumerable<Diagnostic> actual, params DiagnosticDescription[] expected)
        {
            Verify(actual, expected, errorCodeOnly: false);
        }

        public static void Verify(this IEnumerable<Diagnostic> actual, bool fallbackToErrorCodeOnlyForNonEnglish, params DiagnosticDescription[] expected)
        {
            Verify(actual, expected, errorCodeOnly: fallbackToErrorCodeOnlyForNonEnglish && EnsureEnglishUICulture.PreferredOrNull != null);
        }

        public static void VerifyWithFallbackToErrorCodeOnlyForNonEnglish(this IEnumerable<Diagnostic> actual, params DiagnosticDescription[] expected)
        {
            Verify(actual, true, expected);
        }

        public static void Verify(this ImmutableArray<Diagnostic> actual, params DiagnosticDescription[] expected)
        {
            Verify((IEnumerable<Diagnostic>)actual, expected);
        }

        private static void Verify(IEnumerable<Diagnostic> actual, DiagnosticDescription[] expected, bool errorCodeOnly)
        {
            if (expected == null)
            {
                throw new ArgumentException("Must specify expected errors.", "expected");
            }

            var unmatched = actual.Select(d => new DiagnosticDescription(d, errorCodeOnly)).ToList();

            // Try to match each of the 'expected' errors to one of the 'actual' ones.
            // If any of the expected errors don't appear, fail test.
            foreach (var d in expected)
            {
                int index = unmatched.IndexOf(d);
                if (index > -1)
                {
                    unmatched.RemoveAt(index);
                }
                else
                {
                    Assert.True(false, DiagnosticDescription.GetAssertText(expected, actual));
                }
            }

            // If any 'extra' errors appear that were not in the 'expected' list, fail test.
            if (unmatched.Count > 0)
            {
                Assert.True(false, DiagnosticDescription.GetAssertText(expected, actual));
            }
        }

        public static TCompilation VerifyDiagnostics<TCompilation>(this TCompilation c, params DiagnosticDescription[] expected)
            where TCompilation : Compilation
        {
            var diagnostics = c.GetDiagnostics();
            diagnostics.Verify(expected);
            return c;
        }

        public static void VerifyAnalyzerOccurrenceCount<TCompilation>(this TCompilation c, DiagnosticAnalyzer[] analyzers, int expectedCount, Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException = null)
            where TCompilation : Compilation
        {
            Assert.Equal(expectedCount, c.GetAnalyzerDiagnostics(analyzers, null, continueOnAnalyzerException).Length);
        }

        public static TCompilation VerifyAnalyzerDiagnostics<TCompilation>(
                this TCompilation c, DiagnosticAnalyzer[] analyzers, AnalyzerOptions options = null, Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException = null, params DiagnosticDescription[] expected)
            where TCompilation : Compilation
        {
            ImmutableArray<Diagnostic> diagnostics;
            c = c.GetAnalyzerDiagnostics(analyzers, options, continueOnAnalyzerException, diagnostics: out diagnostics);
            diagnostics.Verify(expected);
            return c; // note this is a new compilation
        }

        public static ImmutableArray<Diagnostic> GetAnalyzerDiagnostics<TCompilation>(this TCompilation c, DiagnosticAnalyzer[] analyzers, AnalyzerOptions options = null, Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException = null)
            where TCompilation : Compilation
        {
            ImmutableArray<Diagnostic> diagnostics;
            c = GetAnalyzerDiagnostics(c, analyzers, options, continueOnAnalyzerException, out diagnostics);
            return diagnostics;
        }

        private static TCompilation GetAnalyzerDiagnostics<TCompilation>(
                this TCompilation c,
                DiagnosticAnalyzer[] analyzers,
                AnalyzerOptions options,
                Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException,
                out ImmutableArray<Diagnostic> diagnostics)
            where TCompilation : Compilation
        {
            // We want unit tests to throw if any analyzer OR the driver throws, unless the test explicitly provides a delegate.
            continueOnAnalyzerException = continueOnAnalyzerException ?? DonotCatchAnalyzerExceptions;

            Compilation newCompilation;
            var driver = AnalyzerDriver.Create(c, analyzers.ToImmutableArray(), options, out newCompilation, continueOnAnalyzerException, CancellationToken.None);
            var discarded = newCompilation.GetDiagnostics();
            diagnostics = driver.GetDiagnosticsAsync().Result;
            return (TCompilation)newCompilation; // note this is a new compilation
        }

        /// <summary>
        /// Given a set of compiler or <see cref="IDiagnosticAnalyzer"/> generated <paramref name="diagnostics"/>, returns the effective diagnostics after applying the below filters:
        /// 1) <see cref="CompilationOptions.SpecificDiagnosticOptions"/> specified for the given <paramref name="compilation"/>.
        /// 2) <see cref="CompilationOptions.GeneralDiagnosticOption"/> specified for the given <paramref name="compilation"/>.
        /// 3) Diagnostic suppression through applied <see cref="System.Diagnostics.CodeAnalysis.SuppressMessageAttribute"/>.
        /// 4) Pragma directives for the given <paramref name="compilation"/>.
        /// </summary>
        public static IEnumerable<Diagnostic> GetEffectiveDiagnostics(this Compilation compilation, IEnumerable<Diagnostic> diagnostics)
        {
            return CompilationWithAnalyzers.GetEffectiveDiagnostics(diagnostics, compilation);
        }

        /// <summary>
        /// Returns true if all the diagnostics that can be produced by this analyzer are suppressed through options.
        /// <paramref name="continueOnError"/> says whether the caller would like the exception thrown by the analyzers to be handled or not. If true - Handles ; False - Not handled.
        /// </summary>
        public static bool IsDiagnosticAnalyzerSuppressed(this DiagnosticAnalyzer analyzer, CompilationOptions options)
        {
            return CompilationWithAnalyzers.IsDiagnosticAnalyzerSuppressed(analyzer, options, (exception, throwingAnalyzer) => true);
        }

        public static TCompilation VerifyEmitDiagnostics<TCompilation>(this TCompilation c, EmitOptions options, params DiagnosticDescription[] expected)
            where TCompilation : Compilation
        {
            c.Emit(new MemoryStream(), pdbStream: new MemoryStream(), options: options).Diagnostics.Verify(expected);
            return c;
        }

        public static TCompilation VerifyEmitDiagnostics<TCompilation>(this TCompilation c, params DiagnosticDescription[] expected)
            where TCompilation : Compilation
        {
            return VerifyEmitDiagnostics(c, EmitOptions.Default, expected);
        }

        public static TCompilation VerifyEmitDiagnostics<TCompilation>(this TCompilation c, IEnumerable<ResourceDescription> manifestResources, params DiagnosticDescription[] expected)
            where TCompilation : Compilation
        {
            c.Emit(new MemoryStream(), pdbStream: new MemoryStream(), manifestResources: manifestResources).Diagnostics.Verify(expected);
            return c;
        }

        public static string Concat(this string[] str)
        {
            return str.Aggregate(new StringBuilder(), (sb, s) => sb.AppendLine(s), sb => sb.ToString());
        }

        public static DiagnosticAnalyzer GetCompilerDiagnosticAnalyzer(string languageName)
        {
            return languageName == LanguageNames.CSharp ?
                (DiagnosticAnalyzer)new Diagnostics.CSharp.CSharpCompilerDiagnosticAnalyzer() :
                new Diagnostics.VisualBasic.VisualBasicCompilerDiagnosticAnalyzer();
        }

        public static ImmutableDictionary<string, ImmutableArray<DiagnosticAnalyzer>> GetCompilerDiagnosticAnalyzersMap()
        {
            var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<DiagnosticAnalyzer>>();
            builder.Add(LanguageNames.CSharp, ImmutableArray.Create(GetCompilerDiagnosticAnalyzer(LanguageNames.CSharp)));
            builder.Add(LanguageNames.VisualBasic, ImmutableArray.Create(GetCompilerDiagnosticAnalyzer(LanguageNames.VisualBasic)));
            return builder.ToImmutable();
        }

        public static AnalyzerReference GetCompilerDiagnosticAnalyzerReference(string languageName)
        {
            var analyzer = GetCompilerDiagnosticAnalyzer(languageName);
            return new AnalyzerImageReference(ImmutableArray.Create(analyzer), display: analyzer.GetType().FullName);
        }

        public static ImmutableDictionary<string, ImmutableArray<AnalyzerReference>> GetCompilerDiagnosticAnalyzerReferencesMap()
        {
            var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<AnalyzerReference>>();
            builder.Add(LanguageNames.CSharp, ImmutableArray.Create(GetCompilerDiagnosticAnalyzerReference(LanguageNames.CSharp)));
            builder.Add(LanguageNames.VisualBasic, ImmutableArray.Create(GetCompilerDiagnosticAnalyzerReference(LanguageNames.VisualBasic)));
            return builder.ToImmutable();
        }
    }
}
