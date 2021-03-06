// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Collections.Generic;
using System.CommandLine;
using System.Reflection;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;
using Internal.IL;

using Internal.CommandLine;
using System.Text.RegularExpressions;

namespace ILVerify
{
    class Program
    {
        private bool _help;

        private Dictionary<string, string> _inputFilePaths = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        private Dictionary<string, string> _referenceFilePaths = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        private IReadOnlyList<Regex> _includePatterns = Array.Empty<Regex>();
        private IReadOnlyList<Regex> _excludePatterns = Array.Empty<Regex>();

        private SimpleTypeSystemContext _typeSystemContext;

        private int _numErrors;

        private Program()
        {
        }

        private void Help(string helpText)
        {
            Console.WriteLine("ILVerify version " + typeof(Program).Assembly.GetName().Version.ToString());
            Console.WriteLine();
            Console.WriteLine("-help        Display this usage message (Short form: -?)");
            Console.WriteLine("-reference   Reference metadata from the specified assembly (Short form: -r)");
            Console.WriteLine("-include     Use only methods/types/namespaces, which match the given regular expression(s)");
            Console.WriteLine("-exclude     Skip methods/types/namespaces, which match the given regular expression(s)");
        }

        public static IReadOnlyList<Regex> StringPatternsToRegexList(IReadOnlyList<string> patterns)
        {
            List<Regex> patternList = new List<Regex>();
            foreach (var pattern in patterns)
                patternList.Add(new Regex(pattern));
            return patternList;
        }

        private ArgumentSyntax ParseCommandLine(string[] args)
        {
            IReadOnlyList<string> inputFiles = Array.Empty<string>();
            IReadOnlyList<string> referenceFiles = Array.Empty<string>();
            IReadOnlyList<string> includePatterns = Array.Empty<string>();
            IReadOnlyList<string> excludePatterns = Array.Empty<string>();

            AssemblyName name = typeof(Program).GetTypeInfo().Assembly.GetName();
            ArgumentSyntax argSyntax = ArgumentSyntax.Parse(args, syntax =>
            {
                syntax.ApplicationName = name.Name.ToString();

                // HandleHelp writes to error, fails fast with crash dialog and lacks custom formatting.
                syntax.HandleHelp = false;
                syntax.HandleErrors = true;

                syntax.DefineOption("h|help", ref _help, "Help message for ILC");
                syntax.DefineOptionList("r|reference", ref referenceFiles, "Reference file(s) for compilation");
                syntax.DefineOptionList("i|include", ref includePatterns, "Use only methods/types/namespaces, which match the given regular expression(s)");
                syntax.DefineOptionList("e|exclude", ref excludePatterns, "Skip methods/types/namespaces, which match the given regular expression(s)");

                syntax.DefineParameterList("in", ref inputFiles, "Input file(s) to compile");
            });
            foreach (var input in inputFiles)
                Helpers.AppendExpandedPaths(_inputFilePaths, input, true);

            foreach (var reference in referenceFiles)
                Helpers.AppendExpandedPaths(_referenceFilePaths, reference, false);

            _includePatterns = StringPatternsToRegexList(includePatterns);
            _excludePatterns = StringPatternsToRegexList(excludePatterns);

            return argSyntax;
        }

        private void VerifyMethod(MethodDesc method, MethodIL methodIL)
        {
            // Console.WriteLine("Verifying: " + method.ToString());

            try
            {
                var importer = new ILImporter(method, methodIL);

                importer.ReportVerificationError = (args) =>
                {
                    var message = new StringBuilder();

                    message.Append("[IL]: Error: ");
                    
                    message.Append("[");
                    message.Append(_typeSystemContext.GetModulePath(((EcmaMethod)method).Module));
                    message.Append(" : ");
                    message.Append(((EcmaType)method.OwningType).Name);
                    message.Append("::");
                    message.Append(method.Name);
                    message.Append("]");

                    message.Append("[offset 0x");
                    message.Append(args.Offset.ToString("X8"));
                    message.Append("]");

                    if (args.Found != null)
                    {
                        message.Append("[found ");
                        message.Append(args.Found);
                        message.Append("]");
                    }

                    if (args.Expected != null)
                    {
                        message.Append("[expected ");
                        message.Append(args.Expected);
                        message.Append("]");
                    }

                    if (args.Token != 0)
                    {
                        message.Append("[token  0x");
                        message.Append(args.Token.ToString("X8"));
                        message.Append("]");
                    }

                    message.Append(" ");

                    message.Append(SR.GetResourceString(args.Code.ToString(), null) ?? args.Code.ToString());

                    Console.WriteLine(message);

                    _numErrors++;
                };

                importer.Verify();
            }
            catch (VerificationException)
            {
            }
            catch (BadImageFormatException)
            {
                Console.WriteLine("Unable to resolve token");
            }
            catch (PlatformNotSupportedException e)
            {
                Console.WriteLine(e.Message);
            }
        }

        private void VerifyModule(EcmaModule module)
        {
            foreach (var methodHandle in module.MetadataReader.MethodDefinitions)
            {
                var method = (EcmaMethod)module.GetMethod(methodHandle);

                var methodIL = EcmaMethodIL.Create(method);
                if (methodIL == null)
                    continue;

                var methodName = method.ToString();
                if (_includePatterns.Count > 0 && !_includePatterns.Any(p => p.IsMatch(methodName)))
                    continue;
                if (_excludePatterns.Any(p => p.IsMatch(methodName)))
                    continue;

                VerifyMethod(method, methodIL);
            }
        }

        private int Run(string[] args)
        {
            ArgumentSyntax syntax = ParseCommandLine(args);
            if (_help)
            {
                Help(syntax.GetHelpText());
                return 1;
            }

            if (_inputFilePaths.Count == 0)
                throw new CommandLineException("No input files specified");

            _typeSystemContext = new SimpleTypeSystemContext();
            _typeSystemContext.InputFilePaths = _inputFilePaths;
            _typeSystemContext.ReferenceFilePaths = _referenceFilePaths;

            _typeSystemContext.SetSystemModule(_typeSystemContext.GetModuleForSimpleName("mscorlib"));

            foreach (var inputPath in _inputFilePaths.Values)
            {
                _numErrors = 0;

                VerifyModule(_typeSystemContext.GetModuleFromPath(inputPath));
                
                if (_numErrors > 0)
                    Console.WriteLine(_numErrors + " Error(s) Verifying " + inputPath);
                else
                    Console.WriteLine("All Classes and Methods in " + inputPath + " Verified.");
            }

            return 0;
        }

        private static int Main(string[] args)
        {
            try
            {
                return new Program().Run(args);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Error: " + e.Message);
                return 1;
            }
        }
    }
}
