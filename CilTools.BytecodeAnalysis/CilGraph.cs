﻿/* CilTools.BytecodeAnalysis library 
 * Copyright (c) 2022,  MSDN.WhiteKnight (https://github.com/MSDN-WhiteKnight) 
 * License: BSD 2.0 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using CilTools.Reflection;
using CilTools.SourceCode;
using CilTools.Syntax;
using CilTools.Syntax.Generation;

namespace CilTools.BytecodeAnalysis
{
    /// <summary>
    /// Represents a CIL graph, a graph that reflects a flow of control between CIL instructions in the method
    /// </summary>
    /// <remarks>
    /// <para>CIL graph is a directed graph with nodes representing CIL instructions withing method body and edges 
    /// representing how control flows between them when runtime executes method. The root of the graph is the first 
    /// instruction of the method. Each node stores a reference to the next instruction (which is usually executed after it) 
    /// and, if it's a jump instruction, a reference to the branch target (an instruction that would be executed if the 
    /// condition for the jump is met). For convenience, each instruction serving as branch target is assigned a label, 
    /// a string that identify it. The last instruction of the method has null as its next instruction reference.</para>
    /// <para>Use <see cref="CilGraph.Create"/> method to create CIL graph for a method.</para>
    /// </remarks>
    public class CilGraph
    {
        static IList<ExceptionBlock> FindTryBlocks(IList<ExceptionBlock> list, uint start, uint end)
        {
            if (list == null) throw new ArgumentNullException("list");
            IList<ExceptionBlock> res = new List<ExceptionBlock>(list.Count);

            foreach (var block in list)
            {
                if (block.TryOffset >= start && block.TryOffset < end) res.Add(block);
            }

            return res;
        }

        static IList<ExceptionBlock> FindTryBlockEnds(IList<ExceptionBlock> list, uint start, uint end)
        {
            if (list == null) throw new ArgumentNullException("list");
            IList<ExceptionBlock> res = new List<ExceptionBlock>(list.Count);

            foreach (var block in list)
            {
                if (block.TryOffset + block.TryLength >= start && block.TryOffset + block.TryLength < end) res.Add(block);
            }

            return res;
        }

        private class TryBlockComparer : IEqualityComparer<ExceptionBlock>
        {
            public bool Equals(ExceptionBlock x, ExceptionBlock y)
            {
                return x.TryOffset == y.TryOffset && x.TryLength == y.TryLength;
            }

            public int GetHashCode(ExceptionBlock obj)
            {
                return obj.TryOffset + obj.TryLength;
            }
        }

        static HashSet<ExceptionBlock> FindDistinctTryBlocks(IList<ExceptionBlock> list)
        {
            if (list == null) throw new ArgumentNullException("list");
            HashSet<ExceptionBlock> set = new HashSet<ExceptionBlock>(new TryBlockComparer());

            foreach (var block in list)
            {
                set.Add(block);
            }

            return set;
        }

        static IList<ExceptionBlock> FindFilterBlocks(IList<ExceptionBlock> list, uint start, uint end)
        {
            if (list == null) throw new ArgumentNullException("list");
            IList<ExceptionBlock> res = new List<ExceptionBlock>(list.Count);

            foreach (var block in list)
            {
                if (block.Flags != ExceptionHandlingClauseOptions.Filter) continue;
                if (block.FilterOffset >= start && block.FilterOffset < end) res.Add(block);
            }

            return res;
        }

        static IList<ExceptionBlock> FindHandlerBlocks(IList<ExceptionBlock> list, uint start, uint end)
        {
            if (list == null) throw new ArgumentNullException("list");

            IList<ExceptionBlock> res = new List<ExceptionBlock>(list.Count);
            foreach (var block in list)
            {
                if (block.HandlerOffset >= start && block.HandlerOffset < end) res.Add(block);
            }
            return res;
        }

        static IList<ExceptionBlock> FindBlockEnds(IList<ExceptionBlock> list, uint start, uint end)
        {
            if (list == null) throw new ArgumentNullException("list");

            IList<ExceptionBlock> res = new List<ExceptionBlock>(list.Count);
            foreach (var block in list)
            {
                if (block.HandlerOffset + block.HandlerLength >= start && block.HandlerOffset + block.HandlerLength < end) res.Add(block);
            }
            return res;
        }

        /// <summary>
        /// Returns <see cref="CilGraph"/> that represents a specified method
        /// </summary>
        /// <param name="m">Method for which to build CIL graph</param>
        /// <exception cref="System.ArgumentNullException">Source method is null</exception>
        /// <returns>CIL graph object</returns>
        public static CilGraph Create(MethodBase m)
        {
            if (m == null) throw new ArgumentNullException("m", "Source method cannot be null");

            List<CilInstruction> instructions;
            List<int> labels = new List<int>();
            m = (MethodBase)CustomMethod.PrepareMethod(m);

            if (ReflectionUtils.IsMethodWithoutBody(m))
            {
                //If method is abstract, PInvoke or provided by runtime, it does not have CIL method body by design,
                //so we simply return empty CilGraph
                return new CilGraph(null, m);
            }

            instructions = CilReader.GetInstructions(m).ToList();

            List<CilGraphNodeMutable> nodes = new List<CilGraphNodeMutable>(instructions.Count);
            CilGraphNode[] targets;

            foreach (CilInstruction instr in instructions)
            {
                nodes.Add(new CilGraphNodeMutable(instr));
                if (instr.Operand == null) continue;

                int target;

                //if instruction references branch targets, add byte offset into labels
                switch (instr.OpCode.OperandType)
                {
                    case OperandType.ShortInlineBrTarget:
                        target = (sbyte)instr.Operand + (int)instr.ByteOffset + (int)instr.TotalSize;
                        if (!labels.Contains(target)) labels.Add(target);
                        break;

                    case OperandType.InlineBrTarget:
                        target = (int)instr.Operand + (int)instr.ByteOffset + (int)instr.TotalSize;
                        if (!labels.Contains(target)) labels.Add(target);
                        break;

                    case OperandType.InlineSwitch:
                        int[] arr = instr.Operand as int[];
                        if (arr == null) break;

                        for (int i = 0; i < arr.Length; i++)
                        {
                            target = arr[i] + (int)instr.ByteOffset + (int)instr.TotalSize;
                            if (!labels.Contains(target)) labels.Add(target);
                        }

                        break;
                }
            }

            labels.Sort();

            targets = new CilGraphNodeMutable[labels.Count];

            //find all nodes that are referenced as labels and give them names

            foreach (CilGraphNodeMutable node in nodes)
            {
                CilInstruction instr = node.Instruction;

                for (int i = 0; i < labels.Count; i++)
                {
                    if (instr.ByteOffset == labels[i])
                    {
                        node.Name = "IL_" + (i + 1).ToString().PadLeft(4, '0');
                        targets[i] = node;
                        break;
                    }
                }
            }

            //build the final graph

            for (int n = 0; n < nodes.Count; n++)
            {
                CilInstruction instr = nodes[n].Instruction;

                //if instruction references branch target, connect it with respective node

                if (instr.OpCode.OperandType == OperandType.ShortInlineBrTarget && instr.Operand != null)
                {
                    int target = (sbyte)instr.Operand + (int)instr.ByteOffset + (int)instr.TotalSize;

                    for (int i = 0; i < labels.Count; i++)
                    {
                        if (target == labels[i])
                        {
                            nodes[n].BranchTarget = targets[i];
                            break;
                        }
                    }
                }
                else if (instr.OpCode.OperandType == OperandType.InlineBrTarget && instr.Operand != null)
                {
                    int target = (int)instr.Operand + (int)instr.ByteOffset + (int)instr.TotalSize;

                    for (int i = 0; i < labels.Count; i++)
                    {
                        if (target == labels[i])
                        {
                            nodes[n].BranchTarget = targets[i];
                            break;
                        }
                    }
                }
                else if (instr.OpCode.OperandType == OperandType.InlineSwitch && instr.Operand != null)
                {
                    //for switch instruction, set array of target instructions

                    int[] arr = instr.Operand as int[];
                    if (arr != null)
                    {
                        CilGraphNode[] swt = new CilGraphNode[arr.Length];

                        for (int j = 0; j < arr.Length; j++)
                        {
                            int target = arr[j] + (int)instr.ByteOffset + (int)instr.TotalSize;

                            for (int i = 0; i < labels.Count; i++)
                            {
                                if (target == labels[i])
                                {
                                    swt[j] = targets[i];
                                    break;
                                }
                            }
                        }

                        nodes[n].SetSwitchTargets(swt);
                    }
                }

                //connect previous and next nodes
                if (n > 0) nodes[n].Previous = nodes[n - 1];
                if (n < nodes.Count - 1) nodes[n].Next = nodes[n + 1];

            }

            return new CilGraph(nodes[0], m); //first node is a root node
        }

        /// <summary>
        /// A root node of this graph (the first instruction in the method)
        /// </summary>
        CilGraphNode _Root;

        /// <summary>
        /// A method object for which this graph is built
        /// </summary>
        MethodBase _Method;

        /// <summary>
        /// Creates new CIL graph. (Insfrastructure; not intended for user code)
        /// </summary>
        /// <param name="root">Root node</param>
        /// <param name="mb">Method associated with this graph object</param>
        /// <remarks>Use <see cref="CilGraph.Create"/> method to create CIL graph for a method instead of using this 
        /// contructor.</remarks>
        internal CilGraph(CilGraphNode root, MethodBase mb)
        {
            this._Root = root;
            this._Method = (MethodBase)CustomMethod.PrepareMethod(mb);
        }

        /// <summary>
        /// Gets a root node of this graph (the first instruction in the method)
        /// </summary>
        public CilGraphNode Root { get { return this._Root; } }

        /// <summary>
        /// Gets a method for which this graph is built
        /// </summary>
        public MethodBase Method { get { return this._Method; } }


        /// <summary>
        /// Writes the signature of the method represented by this graph into the specified TextWriter
        /// </summary>
        /// <param name="output">The destination TextWriter</param>
        public void PrintSignature(TextWriter output)
        {
            DirectiveSyntax.FromMethodSignature(this._Method, string.Empty).ToText(output);
        }

        /// <summary>
        /// Writes default parameter values of the method represented by this graph into the specified TextWriter
        /// </summary>
        /// <param name="output">The destination TextWriter</param>
        public void PrintDefaults(TextWriter output)
        {
            Assembly containingAssembly = ReflectionUtils.GetContainingAssembly(this._Method);
            SyntaxGenerator gen = new SyntaxGenerator(containingAssembly);
            SyntaxNode[] elems = gen.GetDefaultsSyntax(this._Method, 0);

            for (int i = 0; i < elems.Length; i++)
            {
                elems[i].ToText(output);
            }
        }

        /// <summary>
        /// Writes custom attributes of the method represented by this graph into the specified TextWriter
        /// </summary>
        /// <param name="output">The destination TextWriter</param>
        public void PrintAttributes(TextWriter output)
        {
            Assembly containingAssembly = ReflectionUtils.GetProviderAssembly(this._Method);
            SyntaxGenerator gen = new SyntaxGenerator(containingAssembly);
            SyntaxNode[] elems = gen.GetAttributesSyntax(this._Method, 1);

            for (int i = 0; i < elems.Length; i++)
            {
                elems[i].ToText(output);
            }
        }

        /// <summary>
        /// Writes the method header code of the method represented by this graph into the specified TextWriter
        /// </summary>
        /// <param name="output">The destination TextWriter</param>
        /// <remarks>
        /// The method header in CLI contains information such as stack size and local variables.
        /// </remarks>
        public void PrintHeader(TextWriter output)
        {
            SyntaxNode[] elems = this.HeaderAsSyntax(0, DisassemblerParams.Default);

            for (int i = 0; i < elems.Length; i++)
            {
                elems[i].ToText(output);
            }
        }

        SyntaxNode[] HeaderAsSyntax(int startIndent, DisassemblerParams disassemblerParams)
        {
            ICustomMethod cm = (ICustomMethod)this._Method;
            int maxstack = 0;
            bool has_maxstack = false;
            LocalVariable[] locals = null;
            List<SyntaxNode> ret = new List<SyntaxNode>(5);

            try
            {
                has_maxstack = cm.MaxStackSizeSpecified;

                if (has_maxstack)
                {
                    maxstack = cm.MaxStackSize;
                }
            }
            catch (Exception ex)
            {
                string error = "Exception occured when trying to get method header.";
                Diagnostics.OnError(this, new CilErrorEventArgs(ex, error));
            }

            try
            {
                locals = cm.GetLocalVariables();
            }
            catch (Exception ex)
            {
                string error = "Exception occured when trying to get local variables.";
                Diagnostics.OnError(this, new CilErrorEventArgs(ex, error));
            }

            Assembly containingAssembly;

            // If we need to assembly-qualify all types, just pretend that we don't know the
            // containing assembly.
            if (disassemblerParams.AssemblyQualifyAllTypes) containingAssembly = null;
            else containingAssembly = ReflectionUtils.GetContainingAssembly(this._Method);

            //.override (ECMA-335 II.10.3.2)
            MethodInfo mOverridden = null;
            try
            {
                mOverridden = ReflectionUtils.GetExplicitlyImplementedMethod(this._Method);
            }
            catch (Exception ex)
            {
                string error = "Exception occured when trying to get interface mappings.";
                Diagnostics.OnError(this, new CilErrorEventArgs(ex, error));
            }

            if (mOverridden != null)
            {
                List<SyntaxNode> list = new List<SyntaxNode>();

                if (mOverridden.DeclaringType.IsGenericType)
                {
                    //long form - prefixed with method as inline token form
                    SyntaxGenerator gen = new SyntaxGenerator(containingAssembly);

                    MemberRefSyntax mrs = gen.GetMethodRefSyntax(mOverridden, inlineTok: true,
                        forceTypeSpec: false, skipAssembly: false);

                    list.Add(mrs);
                    list.Add(new GenericSyntax(Environment.NewLine));
                }
                else
                {
                    IEnumerable<SyntaxNode> nodes = TypeSyntaxGenerator.GetTypeSpecSyntaxAuto(
                        mOverridden.DeclaringType, skipAssembly: false, containingAssembly);

                    foreach (SyntaxNode node in nodes)
                    {
                        list.Add(node);
                    }

                    list.Add(new PunctuationSyntax(string.Empty, "::", string.Empty));
                    list.Add(new IdentifierSyntax(string.Empty, mOverridden.Name, Environment.NewLine,
                        true, mOverridden));
                }

                DirectiveSyntax dir = new DirectiveSyntax(SyntaxUtils.GetIndentString(startIndent + 1),
                    "override", list.ToArray());
                ret.Add(dir);
            }

            //.vtentry (ECMA 335 II.25.3.3)
            string vtslot = null;
            try
            {
                vtslot = (string)ReflectionProperties.Get(this._Method, ReflectionProperties.VTableEntry);
            }
            catch (Exception ex)
            {
                string error = "Exception occured when trying to get vtable data.";
                Diagnostics.OnError(this, new CilErrorEventArgs(ex, error));
            }

            if (!string.IsNullOrEmpty(vtslot))
            {
                DirectiveSyntax dir = new DirectiveSyntax(SyntaxUtils.GetIndentString(startIndent + 1),
                    "vtentry", new SyntaxNode[] { new GenericSyntax(vtslot + Environment.NewLine) });
                ret.Add(dir);
            }

            //display bytecode size in bytes if specified
            if (disassemblerParams.IncludeCodeSize && !ReflectionUtils.IsMethodWithoutBody(this._Method))
            {
                try
                {
                    byte[] bytecode = cm.GetBytecode();

                    if (bytecode != null)
                    {
                        CommentSyntax comment = CommentSyntax.Create(SyntaxUtils.GetIndentString(startIndent + 1),
                            " Code size: " + bytecode.Length.ToString(CultureInfo.InvariantCulture),
                            Environment.NewLine, false);
                        ret.Add(comment);
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.OnError(this, new CilErrorEventArgs(ex, "Failed to get code size"));
                }
            }

            if (ReflectionUtils.IsEntryPoint(this._Method))
            {
                DirectiveSyntax dir = new DirectiveSyntax(SyntaxUtils.GetIndentString(startIndent + 1), "entrypoint",
                    new SyntaxNode[] { new GenericSyntax(Environment.NewLine) });
                ret.Add(dir);
            }

            if (has_maxstack)
            {
                DirectiveSyntax dir = new DirectiveSyntax(SyntaxUtils.GetIndentString(startIndent + 1), "maxstack",
                    new SyntaxNode[] { new GenericSyntax(" " + maxstack.ToString() + Environment.NewLine) });
                ret.Add(dir);
            }

            //local variables
            if (locals != null && locals.Length > 0)
            {
                List<SyntaxNode> inner = new List<SyntaxNode>(locals.Length * 4);

                if (cm.InitLocalsSpecified)
                {
                    if (cm.InitLocals)
                    {
                        inner.Add(new KeywordSyntax(" ", "init", string.Empty,
                            KeywordKind.Other));
                    }
                }

                inner.Add(new PunctuationSyntax(" ", "(", string.Empty));

                for (int i = 0; i < locals.Length; i++)
                {
                    if (i >= 1)
                    {
                        inner.Add(new PunctuationSyntax(string.Empty, ",",
                            "\r\n" + SyntaxUtils.GetIndentString(startIndent + 4)));
                    }

                    LocalVariable local = locals[i];
                    inner.Add(local.LocalTypeSpec.ToSyntax(containingAssembly));
                    inner.Add(new IdentifierSyntax(" ", "V_" + local.LocalIndex.ToString(), string.Empty, false, local));
                }

                inner.Add(new PunctuationSyntax(string.Empty, ")", Environment.NewLine));

                DirectiveSyntax dir = new DirectiveSyntax(SyntaxUtils.GetIndentString(startIndent + 1),
                    "locals", inner.ToArray());

                ret.Add(dir);
            }

            return ret.ToArray();
        }

        /// <summary>
        /// Writes the CIL code corresponding to this graph into the specified TextWriter, optionally including signature, 
        /// default parameter values, attributes and method header
        /// </summary>
        /// <param name="output">The destination TextWriter, or null to use standard output</param>
        /// <param name="IncludeSignature">Indicates that method signature should be included in the output</param>
        /// <param name="IncludeDefaults">Indicates that default parameter values should be included in the output</param>
        /// <param name="IncludeAttributes">Indicates that custom attributes should be included in the output</param>
        /// <param name="IncludeHeader">Indicates that method header should be included in the output</param>
        /// <remarks>
        /// <para>Method header contains information such as maximum stack size and local variable types.</para>
        /// </remarks>
        public void Print(
            TextWriter output = null,
            bool IncludeSignature = false,
            bool IncludeDefaults = false,
            bool IncludeAttributes = false,
            bool IncludeHeader = false)
        {
            if (output == null) output = Console.Out;

            CilGraphNode node = this._Root;

            if (IncludeSignature)
            {
                PrintSignature(output);
                output.WriteLine('{');
            }

            if (IncludeAttributes)
            {
                //attributes
                try
                {
                    PrintAttributes(output);
                }
                catch (InvalidOperationException) { }
                catch (TypeLoadException ex)
                {
                    Diagnostics.OnError(
                        this,
                        new CilErrorEventArgs(ex, "Failed to load attributes for " + this._Method.ToString())
                        );
                }
            }

            if (IncludeDefaults)
            {
                //optional parameters
                PrintDefaults(output);
            }

            //method header           
            if (IncludeHeader)
            {
                PrintHeader(output);
            }

            output.WriteLine();

            //instructions
            if (node != null)
            {
                SyntaxNode[] elems = this.BodyAsSyntaxTree(DisassemblerParams.Default, 0);

                for (int i = 0; i < elems.Length; i++)
                {
                    elems[i].ToText(output);
                }

            }//endif

            if (IncludeSignature) output.WriteLine("}");
        }

        SyntaxNode[] BodyAsSyntaxTree(DisassemblerParams dpars, int startIndent)
        {
            CilGraphNode node = this._Root;
            if (node == null) return SyntaxNode.EmptyArray;

            int n_iter = 0;
            IList<ExceptionBlock> trys = new List<ExceptionBlock>();
            ICustomMethod cm = (ICustomMethod)this._Method;
            Assembly containingAssembly;

            // If we need to assembly-qualify all types, just pretend that we don't know the
            // containing assembly.
            if (dpars.AssemblyQualifyAllTypes) containingAssembly = null;
            else containingAssembly = ReflectionUtils.GetContainingAssembly(this._Method);

            //prepare for source code output
            SourceFragment[] fragments = new SourceFragment[0];
            SourceFragment nextFragment = null;
            int nextFragmentIndex = 0;
            const string commentIndent = "          ";

            if (dpars.IncludeSourceCode)
            {
                try
                {
                    SourceDocument[] sourceDocs = dpars.CodeProvider.GetSourceCodeDocuments(this._Method).ToArray();

                    if (sourceDocs.Length > 0)
                    {
                        SourceDocument sourceDoc = sourceDocs[0];
                        fragments = sourceDoc.Fragments.ToArray();
                    }

                    if (fragments.Length > 0) nextFragment = fragments[0];
                }
                catch (Exception ex)
                {
                    Diagnostics.OnError(this, new CilErrorEventArgs(ex, "Failed to load source code"));
                    nextFragment = new SourceFragment();
                    nextFragment.Text = "???" + Environment.NewLine;
                    fragments = new SourceFragment[] { nextFragment };
                }
            }

            //start disassembling
            try
            {
                trys = cm.GetExceptionBlocks();
            }
            catch (Exception ex)
            {
                string error = "Exception occured when trying to get method header.";
                Diagnostics.OnError(this, new CilErrorEventArgs(ex, error));
            }

            Stack<char> indent = new Stack<char>();

            for (int i = 0; i < startIndent; i++) indent.Push(' ');

            BlockSyntax root = new BlockSyntax(string.Empty, SyntaxNode.EmptyArray, SyntaxNode.EmptyArray);
            List<BlockSyntax> currentpath = new List<BlockSyntax>(20);

            BlockSyntax curr_node = root;
            BlockSyntax new_node;

            while (true)
            {
                CilInstruction instr = node.Instruction;

                //exception handling clauses
                IList<ExceptionBlock> started_trys = FindTryBlocks(trys, instr.ByteOffset, instr.ByteOffset + instr.TotalSize);
                HashSet<ExceptionBlock> distinct_starts = FindDistinctTryBlocks(started_trys);

                IList<ExceptionBlock> ended_trys = FindTryBlockEnds(trys, instr.ByteOffset, instr.ByteOffset + instr.TotalSize);
                HashSet<ExceptionBlock> distinct_ends = FindDistinctTryBlocks(ended_trys);

                IList<ExceptionBlock> filters = FindFilterBlocks(trys, instr.ByteOffset, instr.ByteOffset + instr.TotalSize);
                IList<ExceptionBlock> ended_blocks = FindBlockEnds(trys, instr.ByteOffset, instr.ByteOffset + instr.TotalSize);

                // try
                for (int i = 0; i < distinct_starts.Count; i++)
                {
                    indent.Push(' ');

                    DirectiveSyntax dir = new DirectiveSyntax(
                        new string(indent.ToArray()), "try", SyntaxNode.EmptyArray
                        );

                    curr_node.AddChildNode(dir);

                    new_node = new BlockSyntax(new string(indent.ToArray()), SyntaxNode.EmptyArray, SyntaxNode.EmptyArray);

                    currentpath.Add(new_node);
                    curr_node = new_node;
                }

                for (int i = 0; i < distinct_ends.Count; i++)
                {
                    if (currentpath.Count == 0)
                    {
                        throw new CilParserException("Parse error: Unexpected block end");
                    }

                    new_node = currentpath[currentpath.Count - 1];

                    currentpath.RemoveAt(currentpath.Count - 1);

                    if (currentpath.Count > 0)
                        curr_node = currentpath[currentpath.Count - 1];
                    else
                        curr_node = root;

                    curr_node.AddChildNode(new_node);

                    if (indent.Count > 0) indent.Pop();
                }

                // end handler
                for (int i = 0; i < ended_blocks.Count; i++)
                {
                    if (currentpath.Count == 0)
                    {
                        throw new CilParserException("Parse error: Unexpected block end");
                    }

                    new_node = currentpath[currentpath.Count - 1];

                    currentpath.RemoveAt(currentpath.Count - 1);

                    if (currentpath.Count > 0)
                        curr_node = currentpath[currentpath.Count - 1];
                    else
                        curr_node = root;

                    curr_node.AddChildNode(new_node);

                    if (indent.Count > 0) indent.Pop();
                }

                // filter
                for (int i = 0; i < filters.Count; i++)
                {
                    indent.Push(' ');

                    new_node = new BlockSyntax(
                        new string(indent.ToArray()),
                        new SyntaxNode[] { new KeywordSyntax("filter", string.Empty) },
                        SyntaxNode.EmptyArray);

                    currentpath.Add(new_node);
                    curr_node = new_node;
                }

                // handler start
                var blocks = FindHandlerBlocks(trys, instr.ByteOffset, instr.ByteOffset + instr.TotalSize);

                foreach (var block in blocks)
                {
                    indent.Push(' ');

                    if (block.Flags == ExceptionHandlingClauseOptions.Clause)
                    {
                        Type t = block.CatchType;

                        List<SyntaxNode> header_nodes = new List<SyntaxNode>();
                        header_nodes.Add(new KeywordSyntax("catch", " "));

                        if (t != null)
                        {
                            IEnumerable<SyntaxNode> nodes = TypeSyntaxGenerator.GetTypeSpecSyntaxAuto(
                                t, skipAssembly: false, containingAssembly);

                            foreach (SyntaxNode x in nodes) header_nodes.Add(x);
                        }

                        new_node = new BlockSyntax(
                            new string(indent.ToArray()),
                            header_nodes.ToArray(),
                            SyntaxNode.EmptyArray);

                        currentpath.Add(new_node);
                        curr_node = new_node;
                    }
                    else if ((block.Flags & ExceptionHandlingClauseOptions.Filter) != 0)
                    {
                        if (indent.Count > 0) indent.Pop();

                        if (currentpath.Count == 0)
                        {
                            throw new CilParserException("Parse error: Unexpected block end");
                        }

                        new_node = currentpath[currentpath.Count - 1];

                        currentpath.RemoveAt(currentpath.Count - 1);

                        if (currentpath.Count > 0)
                            curr_node = currentpath[currentpath.Count - 1];
                        else
                            curr_node = root;

                        curr_node.AddChildNode(new_node);

                        new_node = new BlockSyntax(new string(indent.ToArray()), SyntaxNode.EmptyArray, SyntaxNode.EmptyArray);
                        currentpath.Add(new_node);
                        curr_node = new_node;
                    }
                    else if ((block.Flags & ExceptionHandlingClauseOptions.Finally) != 0)
                    {
                        new_node = new BlockSyntax(
                            new string(indent.ToArray()),
                            new SyntaxNode[] { new KeywordSyntax("finally", string.Empty) },
                            SyntaxNode.EmptyArray);

                        currentpath.Add(new_node);
                        curr_node = new_node;
                    }
                    else if ((block.Flags & ExceptionHandlingClauseOptions.Fault) != 0)
                    {
                        new_node = new BlockSyntax(
                            new string(indent.ToArray()),
                            new SyntaxNode[] { new KeywordSyntax("fault", string.Empty) },
                            SyntaxNode.EmptyArray);

                        currentpath.Add(new_node);
                        curr_node = new_node;
                    }
                }

                //add source code if needed
                if (nextFragment != null && node.Instruction.ByteOffset >= nextFragment.CilStart)
                {
                    string fragmentCode = nextFragment.Text;
                    string[] fragmentCodeArr = SourceUtils.SplitSourceCodeFragment(fragmentCode);

                    //source comment is indented more to align with instruction name instead of label
                    string lead = new string(indent.ToArray()) + commentIndent;

                    //insert source code as comments
                    for (int i = 0; i < fragmentCodeArr.Length; i++)
                    {
                        CommentSyntax cs;

                        if (i == 0) cs = CommentSyntax.Create(Environment.NewLine + lead, fragmentCodeArr[i], null, false);
                        else cs = CommentSyntax.Create(lead, fragmentCodeArr[i], null, false);

                        curr_node.AddChildNode(cs);
                    }

                    nextFragmentIndex++;

                    if (nextFragmentIndex < fragments.Length)
                    {
                        nextFragment = fragments[nextFragmentIndex];
                    }
                    else
                    {
                        nextFragment = null;//last fragment
                    }
                }

                //add instruction
                curr_node.AddChildNode(new InstructionSyntax(new string(indent.ToArray()), node, dpars));

                if (node.Next == null) break; //last instruction
                else node = node.Next;

                n_iter++;
                if (n_iter > 100000)
                {
                    throw new CilParserException(
                        "Error: Too many iterations while trying to process graph (possibly a cyclic or extremely large graph)"
                        );
                }
            }// end while

            if (currentpath.Count > 0)
            {
                // Some blocks are still not closed. Find blocks that close after last instruction.
                uint endOffset = node.Instruction.ByteOffset + node.Instruction.TotalSize;
                IList<ExceptionBlock> ended_blocks = FindBlockEnds(trys, endOffset, endOffset + 1);

                if (ended_blocks.Count < currentpath.Count)
                {
                    throw new CilParserException("Parse error: Not all blocks are closed");
                }

                for (int i = 0; i < ended_blocks.Count; i++)
                {
                    new_node = currentpath[currentpath.Count - 1];
                    currentpath.RemoveAt(currentpath.Count - 1);

                    if (currentpath.Count > 0)
                        curr_node = currentpath[currentpath.Count - 1];
                    else
                        curr_node = root;

                    curr_node.AddChildNode(new_node);
                }
            }

            return root.Content.ToArray();
        }

        /// <summary>
        /// Gets the syntax tree for the method represented by this graph using default disassembler parameters
        /// </summary>
        /// <returns>The root method definition node of the syntax tree</returns>
        public MethodDefSyntax ToSyntaxTree()
        {
            return this.ToSyntaxTreeImpl(DisassemblerParams.Default, 0);
        }

        /// <summary>
        /// Gets the syntax tree for the method represented by this graph using specified disassembler parameters
        /// </summary>
        /// <returns>The root method definition node of the syntax tree</returns>
        public MethodDefSyntax ToSyntaxTree(DisassemblerParams pars)
        {
            return this.ToSyntaxTreeImpl(pars, 0);
        }

        internal MethodDefSyntax ToSyntaxTreeImpl(DisassemblerParams pars, int startIndent)
        {
            if (pars == null) pars = DisassemblerParams.Default;

            string strIndent = SyntaxUtils.GetIndentString(startIndent);
            DirectiveSyntax sig = DirectiveSyntax.FromMethodSignature(this._Method, strIndent);

            List<SyntaxNode> nodes = new List<SyntaxNode>(100);
            SyntaxNode[] arr;
            Assembly containingAssembly;

            // If we need to assembly-qualify all types, just pretend that we don't know the
            // containing assembly.
            if (pars.AssemblyQualifyAllTypes) containingAssembly = null;
            else containingAssembly = ReflectionUtils.GetProviderAssembly(this._Method);

            SyntaxGenerator gen = new SyntaxGenerator(containingAssembly);

            try
            {
                arr = gen.GetAttributesSyntax(this._Method, startIndent + 1);

                for (int i = 0; i < arr.Length; i++)
                {
                    nodes.Add(arr[i]);
                }
            }
            catch (InvalidOperationException)
            {
                nodes.Add(CommentSyntax.Create(SyntaxUtils.GetIndentString(startIndent + 1),
                    "NOTE: Custom attributes are not shown.", null, false));
            }

            arr = gen.GetDefaultsSyntax(this._Method, startIndent);

            for (int i = 0; i < arr.Length; i++)
            {
                nodes.Add(arr[i]);
            }

            arr = this.HeaderAsSyntax(startIndent, pars);

            for (int i = 0; i < arr.Length; i++)
            {
                nodes.Add(arr[i]);
            }

            nodes.Add(new GenericSyntax(Environment.NewLine));

            arr = this.BodyAsSyntaxTree(pars, startIndent);

            for (int i = 0; i < arr.Length; i++)
            {
                nodes.Add(arr[i]);
            }

            BlockSyntax body = new BlockSyntax(strIndent, SyntaxNode.EmptyArray, nodes.ToArray());
            
            return new MethodDefSyntax(sig, body);
        }

        /// <summary>
        /// Returns CIL code corresponding to this graph as a string
        /// </summary>
        /// <returns>A string of CIL code</returns>
        public string ToText()
        {
            StringBuilder sb = new StringBuilder(2048);
            StringWriter wr = new StringWriter(sb);

            using (wr)
            {
                this.Print(wr, true, true, true, true);
                wr.Flush();
                return sb.ToString();
            }
        }

        /// <summary>
        /// Returns the signature of the method represented by this graph
        /// </summary>
        /// <returns>The string with method signature</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(2048);
            StringWriter wr = new StringWriter(sb);

            using (wr)
            {
                this.PrintSignature(wr);
                wr.Flush();
                return sb.ToString();
            }
        }

        /// <summary>
        /// Enumerates nodes in this graph
        /// </summary>
        /// <returns>The collection of graph nodes</returns>
        public IEnumerable<CilGraphNode> GetNodes()
        {
            CilGraphNode node = this._Root;

            while (true)
            {
                if (node == null) break;

                yield return node;

                if (node.Next == null) break; //last instruction
                else node = node.Next;
            }
        }

        /// <summary>
        /// Enumerates all instructions represented by this graph's nodes
        /// </summary>
        /// <returns>The collection of instructions</returns>
        public IEnumerable<CilInstruction> GetInstructions()
        {
            CilGraphNode node = this._Root;

            while (true)
            {
                if (node == null) break;

                yield return node.Instruction;

                if (node.Next == null) break; //last instruction
                else node = node.Next;
            }
        }

        /// <summary>
        /// Gets the collection of nodes that make up the handler of the specified exception block
        /// </summary>
        /// <param name="block">The exception block to get handler</param>
        /// <returns>The collection of nodes corresponding to exception block</returns>
        /// <remarks>
        /// The exception block must belong to the method from which this graph was created. If the 
        /// block belongs to another method, the behaviour is undefined. You can get exception blocks 
        /// that enclose the given graph node using <see cref="CilGraphNode.GetExceptionBlocks"/> 
        /// method.
        /// </remarks>
        public IEnumerable<CilGraphNode> GetHandlerNodes(ExceptionBlock block)
        {
            int start = block.HandlerOffset;
            int end = start + block.HandlerLength;

            foreach (CilGraphNode node in this.GetNodes())
            {
                if ((int)node.Instruction.ByteOffset >= start &&
                    (int)node.Instruction.ByteOffset < end) yield return node;
            }
        }

#if !NETSTANDARD
        /// <summary>
        /// Emits the entire content of this CIL graph into the specified IL generator, 
        /// optionally calling user callback for each processed instruction.
        /// </summary>
        /// <param name="gen">Target IL generator. </param>
        /// <param name="callback">User callback to be called for each processed instruction.</param>
        /// <remarks>Passing user callback into this method enables you to filter instructions that you want to be emitted 
        /// into target IL generator. 
        /// Return <see langword="true"/> to skip emitting instruction, or <see langword="false"/> to emit instruction.</remarks>
        public void EmitTo(ILGenerator gen, Func<CilInstruction, bool> callback = null)
        {
            Dictionary<uint, Label> labels = new Dictionary<uint, Label>();
            Label label;
            ICustomMethod cm = this._Method as ICustomMethod;
            IList<ExceptionBlock> trys = cm.GetExceptionBlocks();
            LocalVariable[] locals = cm.GetLocalVariables();

            //local variables
            if (locals != null)
            {
                for (int i = 0; i < locals.Length; i++)
                {
                    LocalVariable local = locals[i];

                    Type localType = local.LocalType;

                    //DeclareLocal requires runtime type
                    if (localType.UnderlyingSystemType != null) localType = localType.UnderlyingSystemType;

                    gen.DeclareLocal(localType);
                }
            }

            List<CilGraphNode> nodes = this.GetNodes().ToList();

            //first stage - create labels
            foreach (CilGraphNode node in nodes)
            {
                if (!string.IsNullOrEmpty(node.Name))
                {
                    //if instruction is marked with label, save label in dictionary
                    label = gen.DefineLabel();
                    labels[node.Instruction.OrdinalNumber] = label;
                }
            }

            //second stage - emit actual IL
            foreach (CilGraphNode node in nodes)
            {
                CilInstruction instr = node.Instruction;

                //exception handling clauses
                IList<ExceptionBlock> block_starts = FindTryBlocks(trys, instr.ByteOffset, instr.ByteOffset + instr.TotalSize);
                IList<ExceptionBlock> filters = FindFilterBlocks(trys, instr.ByteOffset, instr.ByteOffset + instr.TotalSize);
                IList<ExceptionBlock> block_ends = FindBlockEnds(trys, instr.ByteOffset, instr.ByteOffset + instr.TotalSize);

                IList<ExceptionBlock> filters_next = new ExceptionBlock[0];
                if (node.Next != null)
                {
                    filters_next = FindFilterBlocks(
                        trys, node.Next.Instruction.ByteOffset, node.Next.Instruction.ByteOffset + instr.TotalSize
                        );
                }

                for (int i = 0; i < block_starts.Count; i++)
                {
                    gen.BeginExceptionBlock();
                }

                for (int i = 0; i < block_ends.Count; i++)
                {
                    gen.EndExceptionBlock();
                }

                for (int i = 0; i < filters.Count; i++)
                {
                    gen.BeginExceptFilterBlock();
                }

                var blocks = FindHandlerBlocks(trys, instr.ByteOffset, instr.ByteOffset + instr.TotalSize);

                foreach (var block in blocks)
                {
                    if (block.Flags == ExceptionHandlingClauseOptions.Clause)
                    {
                        Type t = block.CatchType;
                        gen.BeginCatchBlock(t);
                    }
                    else if ((block.Flags & ExceptionHandlingClauseOptions.Filter) != 0)
                    {
                        gen.BeginCatchBlock(null);
                    }
                    else if ((block.Flags & ExceptionHandlingClauseOptions.Finally) != 0)
                    {
                        gen.BeginFinallyBlock();
                    }
                    else if ((block.Flags & ExceptionHandlingClauseOptions.Fault) != 0)
                    {
                        gen.BeginFaultBlock();
                    }
                }

                //labels
                if (!string.IsNullOrEmpty(node.Name))
                {
                    //if instruction has label, mark it
                    if (labels.ContainsKey(instr.OrdinalNumber))
                    {
                        label = labels[instr.OrdinalNumber];
                        gen.MarkLabel(label);
                    }
                }

                //user callback
                bool should_skip = false;

                if (callback != null)
                {
                    should_skip = callback(instr); //execute user callback                    
                }

                //instruction itself
                if (!should_skip)
                {
                    //if instruction is not processed by callback, emit it

                    if (node.BranchTarget != null)
                    {
                        //if this instruction references branch, find label and emit jump instruction
                        if (labels.ContainsKey(node.BranchTarget.Instruction.OrdinalNumber))
                        {
                            label = labels[node.BranchTarget.Instruction.OrdinalNumber];

                            if (instr.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                            {
                                //convert short form instructions into long form, to prevent failure when
                                //method body is larger then expected

                                OpCode opc;
                                if (instr.OpCode == OpCodes.Brfalse_S) opc = OpCodes.Brfalse;
                                else if (instr.OpCode == OpCodes.Brtrue_S) opc = OpCodes.Brtrue;
                                else if (instr.OpCode == OpCodes.Leave_S) opc = OpCodes.Leave;
                                else if (instr.OpCode == OpCodes.Br_S) opc = OpCodes.Br;
                                else if (instr.OpCode == OpCodes.Blt_S) opc = OpCodes.Blt;
                                else if (instr.OpCode == OpCodes.Blt_Un_S) opc = OpCodes.Blt_Un;
                                else if (instr.OpCode == OpCodes.Bne_Un_S) opc = OpCodes.Bne_Un;
                                else if (instr.OpCode == OpCodes.Ble_S) opc = OpCodes.Ble;
                                else if (instr.OpCode == OpCodes.Ble_Un_S) opc = OpCodes.Ble_Un;
                                else throw new NotSupportedException("OpCode not supported: " + instr.OpCode.ToString());

                                gen.Emit(opc, label);
                            }
                            else //long form branching instruction
                            {
                                gen.Emit(instr.OpCode, label); //emit as-is
                            }
                        }
                        else throw new CilParserException("Cannot find label for branch instruction");
                    }
                    else if (instr.OpCode == OpCodes.Switch)
                    {
                        //emit jump table
                        CilGraphNode[] swtargets = node.GetSwitchTargets();
                        Label[] swlabels = new Label[swtargets.Length];

                        for (int i = 0; i < swtargets.Length; i++)
                        {
                            if (labels.ContainsKey(swtargets[i].Instruction.OrdinalNumber))
                            {
                                swlabels[i] = labels[swtargets[i].Instruction.OrdinalNumber];
                            }
                            else throw new CilParserException("Cannot find label for switch instruction");
                        }

                        gen.Emit(instr.OpCode, swlabels);
                    }
                    else if (instr.OpCode != OpCodes.Endfilter && instr.OpCode != OpCodes.Endfinally)
                    {
                        //endfilter/endfinally are already emitted with exception blocks

                        instr.EmitTo(gen); //emit regular instruction                        
                    }
                }

            }//end foreach            
        }
#endif

    }
}
