/* CIL Tools
 * Copyright (c) 2021,  MSDN.WhiteKnight (https://github.com/MSDN-WhiteKnight) 
 * License: BSD 2.0 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CilTools.BytecodeAnalysis;
using CilTools.Metadata;
using CilTools.Syntax;

namespace CilTools.CommandLine
{
    class Program
    {
        static void PrintHelp()
        {
            string exeName = typeof(Program).Assembly.GetName().Name;
            Console.WriteLine(" Commands:");
            Console.WriteLine();

            Console.WriteLine("view - Print CIL code of the method or methods with the specified name");
            Console.WriteLine("Usage: " + exeName + " view [--nocolor] <assembly path> <type full name> <method name>");
            Console.WriteLine("[--nocolor] - disable syntax highlighting");
            Console.WriteLine();

            Console.WriteLine("disasm - Write CIL code of the method or methods with the specified name into file");
            Console.WriteLine("Usage: " + exeName + 
                " disasm [--output <output path>] <assembly path> <type full name> <method name>");
            Console.WriteLine("[--output <output path>] - Output file path");
            Console.WriteLine();

            Console.WriteLine("help - Print available commands");
            Console.WriteLine();
        }

        static string GetErrorInfo()
        {
            string exeName = typeof(Program).Assembly.GetName().Name;
            return "Use \"" + exeName + " help\" to print the list of available commands and their arguments.";
        }

        static void PrintNode(SyntaxNode node, bool noColor, TextWriter target)
        {
            //recursively prints CIL syntax tree to console

            SyntaxNode[] children = node.GetChildNodes();

            if (children.Length == 0)
            {
                //if it a leaf node, print its content to console

                if (noColor)
                {
                    //no syntax highlighting
                    node.ToText(target);
                    return;
                }

                //hightlight syntax elements
                ConsoleColor originalColor = Console.ForegroundColor;

                if (node is KeywordSyntax)
                {
                    KeywordSyntax ks = (KeywordSyntax)node;

                    if (ks.Kind == KeywordKind.Other) Console.ForegroundColor = ConsoleColor.Yellow;
                    else if (ks.Kind == KeywordKind.DirectiveName) Console.ForegroundColor = ConsoleColor.Magenta;
                }
                else if (node is IdentifierSyntax)
                {
                    IdentifierSyntax id = (IdentifierSyntax)node;

                    if (id.IsMemberName) Console.ForegroundColor = ConsoleColor.Cyan;
                }
                else if (node is LiteralSyntax)
                {
                    LiteralSyntax lit = (LiteralSyntax)node;
                    if (lit.Value is string) Console.ForegroundColor = ConsoleColor.Red;
                }
                else if (node is CommentSyntax)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                }

                node.ToText(target);

                //restore console color to default value
                Console.ForegroundColor = originalColor;
            }
            else
            {
                //if the node has child nodes, process them

                for (int i = 0; i < children.Length; i++)
                {
                    PrintNode(children[i], noColor, target);
                }
            }
        }

        static void PrintMethod(MethodBase method, bool noColor, TextWriter target)
        {
            CilGraph graph = CilGraph.Create(method);
            SyntaxNode root = graph.ToSyntaxTree();
            PrintNode(root, noColor, target);
        }

        static int DisassembleMethod(string asspath, string type, string method, bool noColor, TextWriter target) 
        {
            AssemblyReader reader = new AssemblyReader();
            Assembly ass;
            int retCode;

            try
            {
                ass = reader.LoadFrom(asspath);
                Type t = ass.GetType(type);

                MemberInfo[] methods = t.GetMembers(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                    );

                MethodBase[] selectedMethods = methods.OfType<MethodBase>().Where((x) => { return x.Name == method; }).ToArray();

                for (int i = 0; i < selectedMethods.Length; i++)
                {
                    PrintMethod(selectedMethods[i], noColor, target);
                    target.WriteLine();
                }

                retCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error:");
                Console.WriteLine(ex.ToString());
                retCode = 1;
            }
            finally
            {
                reader.Dispose();
            }

            return retCode;
        }

        static bool TryReadExpectedParameter(string[] args, int pos, string expected)
        {
            if (pos >= args.Length) return false;

            if (args[pos] == expected) return true;
            else return false;
        }

        static string ReadCommandParameter(string[] args, int pos)
        {
            if (pos >= args.Length) return null;

            return args[pos];
        }

        static int OnDisasmCommand(string[] args) 
        {
            string asspath;
            string type;
            string method;
            string outpath=null;

            if (args.Length < 4)
            {
                Console.WriteLine("Error: not enough arguments for 'disasm' command.");
                Console.WriteLine(GetErrorInfo());
                return 1;
            }

            int pos = 1;

            if (TryReadExpectedParameter(args, pos, "--output"))
            {
                pos++;
                outpath = ReadCommandParameter(args, pos);
                pos++;
            }

            asspath = ReadCommandParameter(args, pos);
            pos++;

            if (string.IsNullOrEmpty(asspath))
            {
                Console.WriteLine("Error: Assembly path is not provided for the 'disasm' command.");
                Console.WriteLine(GetErrorInfo());
                return 1;
            }

            type = ReadCommandParameter(args, pos);
            pos++;

            if (type == null)
            {
                Console.WriteLine("Error: Type name is not provided for the 'disasm' command.");
                Console.WriteLine(GetErrorInfo());
                return 1;
            }

            method = ReadCommandParameter(args, pos);

            if (string.IsNullOrEmpty(method))
            {
                Console.WriteLine("Error: Method name is not provided for the 'disasm' command.");
                Console.WriteLine(GetErrorInfo());
                return 1;
            }

            if (string.IsNullOrEmpty(outpath)) 
            {
                outpath = Path.GetFileNameWithoutExtension(asspath) + ".il";
            }

            Console.WriteLine("Input file: " + asspath);
            StreamWriter wr;

            try
            {
                wr = new StreamWriter(outpath, append: false, Encoding.UTF8);
            }
            catch (Exception ex) 
            {
                Console.WriteLine("Error: Cannot open output path " + outpath);
                Console.WriteLine(ex.ToString());
                return 1;
            }

            using (wr)
            {
                Console.WriteLine("Disassembling CIL...");
                int res = DisassembleMethod(asspath, type, method, noColor: true, wr);

                if (res == 0) Console.WriteLine("Output successfully written to " + outpath);
                else Console.WriteLine("Failed to disassemble");

                return res;
            }
        }

        static int Main(string[] args)
        {
            Console.WriteLine("*** CIL Tools command line ***");
            Console.WriteLine("Copyright (c) 2021, MSDN.WhiteKnight (https://github.com/MSDN-WhiteKnight/CilTools)");
            Console.WriteLine();

            //read and validate command line arguments

            if (args.Length == 0)
            {
                PrintHelp();
                return 1;
            }
            else if (args[0] == "help" || args[0] == "/?" || args[0] == "-?")
            {
                PrintHelp();
                return 0;
            }
            else if (args[0] == "view")
            {
                string asspath;
                string type;
                string method;
                bool noColor = false;

                if (args.Length < 4)
                {
                    Console.WriteLine("Error: not enough arguments for 'view' command.");
                    Console.WriteLine(GetErrorInfo());
                    return 1;
                }

                int pos = 1;

                if (TryReadExpectedParameter(args, pos, "--nocolor"))
                {
                    noColor = true;
                    pos++;
                }

                asspath = ReadCommandParameter(args, pos);
                pos++;

                if (string.IsNullOrEmpty(asspath))
                {
                    Console.WriteLine("Error: Assembly path is not provided for the 'view' command.");
                    Console.WriteLine(GetErrorInfo());
                    return 1;
                }

                type = ReadCommandParameter(args, pos);
                pos++;

                if (type == null)
                {
                    Console.WriteLine("Error: Type name is not provided for the 'view' command.");
                    Console.WriteLine(GetErrorInfo());
                    return 1;
                }

                method = ReadCommandParameter(args, pos);

                if (string.IsNullOrEmpty(method))
                {
                    Console.WriteLine("Error: Method name is not provided for the 'view' command.");
                    Console.WriteLine(GetErrorInfo());
                    return 1;
                }

                //view command implementation
                Console.WriteLine("Assembly: " + asspath);
                Console.WriteLine("{0}.{1}", type, method);
                Console.WriteLine();
                return DisassembleMethod(asspath, type, method, noColor, Console.Out);
            }
            else if (args[0] == "disasm") 
            {
                return OnDisasmCommand(args);
            }
            else
            {
                Console.WriteLine("Error: unknown command " + args[0]);
                Console.WriteLine(GetErrorInfo());
                return 1;
            }
        }
    }
}
