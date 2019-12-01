﻿/* CilBytecodeParser library unit tests
 * Copyright (c) 2019,  MSDN.WhiteKnight (https://github.com/MSDN-WhiteKnight) 
 * License: BSD 2.0 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CilBytecodeParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CilBytecodeParser.Tests
{
    [TestClass]
    public class CilReaderTests
    {
        public static void PrintHelloWorld()
        {            
            Console.WriteLine("Hello, World");            
        }

        [TestMethod]
        public void TestHelloWorld()
        {
            MethodInfo mi = typeof(CilReaderTests).GetMethod("PrintHelloWorld");
            CilInstruction[] instructions = CilReader.GetInstructions(mi).ToArray();
                        
            AssertThat.NotEmpty(instructions, "The result of PrintHelloWorld method parsing should not be empty collection");
                        
            AssertThat.HasOnlyOneMatch(
                instructions,
                (x) => x.OpCode == OpCodes.Ldstr && x.ReferencedString == "Hello, World",
                "The result of PrintHelloWorld method parsing should contain a single 'ldstr' instruction referencing \"Hello, World\" literal"
                );            

            //verify that instruction sequence contains a single call to Console.WriteLine
            AssertThat.HasOnlyOneMatch(
                instructions,
                (x) => {
                    if (x.OpCode != OpCodes.Call) return false;
                    var method = x.ReferencedMember as MethodBase;
                    if (method == null) return false;

                    return method.Name == "WriteLine" && method.DeclaringType.Name == "Console";
                },
                "The result of PrintHelloWorld method parsing should contain a single call to Console.WriteLine"
                );

            Assert.IsTrue(instructions[instructions.Length - 1].OpCode == OpCodes.Ret, "The last instruction of PrintHelloWorld method should be 'ret'");
            
        }

        public static double CalcSum(double x, double y)
        {
            return x + y;
        }

        [TestMethod]
        public void TestCalcSum()
        {
            MethodInfo mi = typeof(CilReaderTests).GetMethod("CalcSum");
            CilInstruction[] instructions = CilReader.GetInstructions(mi).ToArray();
            
            AssertThat.NotEmpty(instructions, "The result of CalcSum method parsing should not be empty collection");

            AssertThat.HasAtLeastOneMatch(
                instructions,
                (x) => x.OpCode == OpCodes.Ldarg_0 || x.OpCode == OpCodes.Ldarg || x.OpCode == OpCodes.Ldarg_S,
                "The result of CalcSum method parsing should contain at least one instruction loading first argument");

            AssertThat.HasAtLeastOneMatch(
                instructions,
                (x) => x.OpCode == OpCodes.Ldarg_1 || x.OpCode == OpCodes.Ldarg || x.OpCode == OpCodes.Ldarg_S,
                "The result of CalcSum method parsing should contain at least one instruction loading second argument");
            
            AssertThat.HasOnlyOneMatch(
                instructions,
                (x) => x.OpCode == OpCodes.Add,
                "The result of CalcSum method parsing should contain a single 'add' instruction"
                );

            Assert.IsTrue(instructions[instructions.Length - 1].OpCode == OpCodes.Ret, "The last instruction of CalcSum method should be 'ret'");
        }

        static int Foo=2;

        public static void SquareFoo()
        {
            Foo = Foo * Foo;
        }

        [TestMethod]
        public void TestStaticFieldAccess()
        {
            MethodInfo mi = typeof(CilReaderTests).GetMethod("SquareFoo");
            CilInstruction[] instructions = CilReader.GetInstructions(mi).ToArray();
                        
            AssertThat.NotEmpty(instructions, "The result of SquareFoo method parsing should not be empty collection");
            
            AssertThat.HasOnlyOneMatch(
                instructions,
                (x) => x.OpCode == OpCodes.Mul,
                "The result of SquareFoo method parsing should contain a single 'mul' instruction"
                );

            AssertThat.HasAtLeastOneMatch(
                instructions,
                (x) => x.OpCode == OpCodes.Ldsfld && ((FieldInfo)x.ReferencedMember).Name == "Foo",
                "The result of SquareFoo method parsing should contain at least one 'ldsfld Foo' instruction"
                );

            AssertThat.HasOnlyOneMatch(
                instructions,
                (x) => x.OpCode == OpCodes.Stsfld && ((FieldInfo)x.ReferencedMember).Name == "Foo",
                "The result of SquareFoo method parsing should contain a single 'stsfld Foo' instruction"
                );

            Assert.IsTrue(instructions[instructions.Length - 1].OpCode == OpCodes.Ret, "The last instruction of SquareFoo method should be 'ret'");
        }

        public static int GetInterfaceCount(Type t)
        {
            Type[] array = t.GetInterfaces();
            return array.Length;
        }

        [TestMethod]
        public void TestVirtualCall()
        {
            MethodInfo mi = typeof(CilReaderTests).GetMethod("GetInterfaceCount");
            CilInstruction[] instructions = CilReader.GetInstructions(mi).ToArray();

            AssertThat.NotEmpty(instructions, "The result of GetInterfaceCount method parsing should not be empty collection");

            AssertThat.HasOnlyOneMatch(
                instructions,
                (x) => x.OpCode == OpCodes.Callvirt && ((MethodBase)x.ReferencedMember).Name == "GetInterfaces",
                "The result of GetInterfaceCount method parsing should contain a single 'callvirt' instruction"
                );

            Assert.IsTrue(instructions[instructions.Length - 1].OpCode == OpCodes.Ret, "The last instruction of GetInterfaceCount method should be 'ret'");
        }

        [TestMethod]
        public void TestExternalAssemblyAccess()
        {
            MethodInfo mi = typeof(System.IO.Path).GetMethod("GetExtension");
            CilInstruction[] instructions = CilReader.GetInstructions(mi).ToArray();

            AssertThat.NotEmpty(instructions, "The result of Path.GetExtension method parsing should not be empty collection");
                  
            AssertThat.HasAtLeastOneMatch(
                instructions,
                (x) => x.OpCode == OpCodes.Ret,
                "The result of Path.GetExtension method parsing should contain at least one 'ret' instruction"
                );

            AssertThat.HasAtLeastOneMatch(
                instructions,
                (x) => x.OpCode != OpCodes.Nop && x.OpCode != OpCodes.Ret,
                "The result of Path.GetExtension method parsing should contain at least one instruction which is not 'nop' or 'ret'"
                );
        }
        
    }
}
