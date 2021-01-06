﻿/* CIL Tools 
 * Copyright (c) 2021,  MSDN.WhiteKnight (https://github.com/MSDN-WhiteKnight) 
 * License: BSD 2.0 */
using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Reflection.Emit;
using CilTools.BytecodeAnalysis;
using CilTools.Reflection;

namespace CilView
{
    public class ExceptionInfo
    {
        Type type;
        string stack;

        public ExceptionInfo(Type t, string st)
        {
            this.type = t;
            this.stack = st;
        }

        public Type ExceptionType { get { return this.type; } }

        public string Callstack { get { return this.stack; } }

        class GetExceptionsRecursive_Context
        {
            public Dictionary<Type, ExceptionInfo> results;
            public HashSet<MethodBase> visited;
            public Stack<string> callstack=new Stack<string>();
        }

        static void GetExceptionsRecursive(
            MethodBase m,GetExceptionsRecursive_Context ctx,int c,bool returned
            )
        {
//common false positives:

//ObjectDisposedException

            if (!returned && ctx.visited.Contains(m)) return;
            ctx.visited.Add(m);

            //exclude some common false positives

            if (String.Equals(m.Name,"Sleep",StringComparison.InvariantCulture) &&
                String.Equals(m.DeclaringType.FullName,"System.Threading.Thread", StringComparison.InvariantCulture) 
                && c>0)
            {
                //Thread.Sleep initiates wait and thus brings up some exceptions it 
                //could not realistically throw, 
                //such as AbandonedMutexException or ObjectDisposedException
                return;
            }

            if (String.Equals(m.Name, ".ctor", StringComparison.InvariantCulture) &&
                String.Equals(m.DeclaringType.FullName, "System.Text.UTF8Encoding", StringComparison.InvariantCulture)
                && c > 0)
            {
                //UTF8Encoding ctor brings up ArgumentOutOfRangeException that does not 
                //actually happen, because the value passed to Encoding ctor is a  
                //constant which is always valid
                return;
            }

            if (String.Equals(m.Name, "Concat", StringComparison.InvariantCulture) &&
                String.Equals(m.DeclaringType.FullName, "System.String", StringComparison.InvariantCulture)
                && c > 0)
            {
                //String.Concat calls FillStringChecked that brings up IndexOutOfRangeException, 
                //but in practice it is not thrown 
                return;
            }

            if (String.Equals(m.Name, "get_CurrentCulture", StringComparison.InvariantCulture) &&
                String.Equals(
                    m.DeclaringType.FullName, "System.Threading.Thread", StringComparison.InvariantCulture
                    )
                && c > 0)
            {
                //Thread.CurrentCulture tries to load System.Resources.WindowsRuntimeResourceManager type  
                //(System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089), 
                //which brings up TypeLoadException. But it could only really happen when a bug 
                //in runtime packages causes WinRT-related code to be executed on wrong OS
                return;
            }

            if (String.Equals(m.Name, "get_CurrentUICulture", StringComparison.InvariantCulture) &&
                String.Equals(
                    m.DeclaringType.FullName, "System.Threading.Thread", StringComparison.InvariantCulture
                    )
                && c > 0)
            {
                //Thread.CurrentUICulture - same issue as Thread.CurrentCulture above 
                return;
            }

            string mstr = "";
            mstr = m.DeclaringType.FullName + "." + CilVisualization.MethodToString(m);
            
            ctx.callstack.Push(mstr);
            string stackstr = String.Join(Environment.NewLine, ctx.callstack);

            if (c > 100000)
            {
                throw new ApplicationException("Error: recursion is too deep in GetExceptions");
            }

            try
            {
                CilGraph graph;

                try
                {
                    graph = CilGraph.Create(m);
                }
                catch (TypeLoadException)
                {
                    return;
                }

                foreach (CilGraphNode node in graph.GetNodes())
                {
                    if (node.Instruction.OpCode == OpCodes.Throw)
                    {
                        CilGraphNode prev = node.Previous;
                        if (prev == null) continue;

                        if (prev.Instruction.OpCode == OpCodes.Newobj)
                        {
                            //the exception instance was constructed directly on the stack
                            MethodBase constr = prev.Instruction.ReferencedMember as MethodBase;
                            if (constr != null)
                            {
                                if (!ctx.results.ContainsKey(constr.DeclaringType))
                                {
                                    ctx.results[constr.DeclaringType] = new ExceptionInfo(constr.DeclaringType, stackstr);
                                    System.Diagnostics.Debug.WriteLine("Newobj: " + constr.DeclaringType.ToString());
                                }
                            }
                        }
                        else if (prev.Instruction.OpCode == OpCodes.Ldloc)
                        {
                            //the exception instance was stored in local
                            int index = Convert.ToInt32(prev.Instruction.Operand);
                            Type lt = null;

                            if (m is CustomMethod)
                            {
                                CustomMethod cm = (CustomMethod)m;
                                LocalVariable[] locals = cm.GetLocalVariables();
                                if (locals.Length != 0) lt = locals[index].LocalType;
                            }
                            else
                            {
                                lt = prev.Instruction.ReferencedLocal.LocalType;
                            }

                            if (lt != null)
                            {
                                if (!ctx.results.ContainsKey(lt))
                                {
                                    ctx.results[lt] = new ExceptionInfo(lt, stackstr);
                                    System.Diagnostics.Debug.WriteLine("Local: " + lt.ToString());
                                }
                            }
                        }
                        else if (prev.Instruction.OpCode.FlowControl == FlowControl.Call)
                        {
                            //the exception instance is a return value of the method
                            MethodBase target = prev.Instruction.ReferencedMember as MethodBase;

                            if (target != null) GetExceptionsRecursive(target, ctx, c + 1, true);
                        }
                    }
                    else if (returned && node.Instruction.OpCode == OpCodes.Ret)
                    {
                        //the method's return value is an exception that will be thrown

                        CilGraphNode prev = node.Previous;
                        if (prev == null) continue;

                        if (prev.Instruction.OpCode == OpCodes.Newobj)
                        {
                            MethodBase constr = prev.Instruction.ReferencedMember as MethodBase;
                            if (constr != null)
                            {
                                if (!ctx.results.ContainsKey(constr.DeclaringType))
                                {
                                    ctx.results[constr.DeclaringType] = new ExceptionInfo(constr.DeclaringType, stackstr);
                                    System.Diagnostics.Debug.WriteLine("Method: " + constr.DeclaringType.ToString());
                                }
                            }
                        }
                    }
                    else if (node.Instruction.OpCode.FlowControl == FlowControl.Call)
                    {
                        MethodBase target = node.Instruction.ReferencedMember as MethodBase;

                        //if it is a virtual call to virtual method, the real called method 
                        //is determined at runtime, so we skip analysis in this case 

                        if (target != null && 
                            !(node.Instruction.OpCode==OpCodes.Callvirt && target.IsVirtual)
                            )
                        {
                            //recursively check exceptions of the called method
                            GetExceptionsRecursive(target, ctx, c + 1,false);
                        }
                    }//endif
                }//end foreach
            }//end try
            finally
            {
                ctx.callstack.Pop();
            }
        }

        public static IEnumerable<ExceptionInfo> GetExceptions(MethodBase m)
        {
            Dictionary<Type,ExceptionInfo> results = new Dictionary<Type, ExceptionInfo>();
            HashSet<MethodBase> visited = new HashSet<MethodBase>();
            GetExceptionsRecursive_Context ctx = new GetExceptionsRecursive_Context();
            ctx.results = results;
            ctx.visited = visited;
            GetExceptionsRecursive(m, ctx,0,false);

            foreach (ExceptionInfo ex in results.Values) yield return ex;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(1000);
            if (type != null) sb.AppendLine(type.FullName);
            sb.AppendLine();
            sb.AppendLine("--- Callstack: ---");
            sb.AppendLine(stack);
            sb.AppendLine("------------------");
            return sb.ToString();
        }
    }
}
