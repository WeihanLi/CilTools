﻿/* CIL Tools 
 * Copyright (c) 2022, MSDN.WhiteKnight (https://github.com/MSDN-WhiteKnight) 
 * License: BSD 2.0 */
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CilView.Common;
using CilView.Core.Syntax;

namespace CilView.SourceCode
{
    class CppDecompiler : Decompiler
    {
        public CppDecompiler(MethodBase method) : base(method)
        {

        }

        static void GetTypeTokens(Type t, List<SourceToken> target)
        {
            if (t == null) return;

            if (t.IsArray && t.GetArrayRank() == 1)
            {
                target.Add(new SourceToken("array", TokenKind.Keyword, "", " "));
                target.Add(new SourceToken("<", TokenKind.Punctuation));
                GetTypeTokens(t.GetElementType(), target);
                target.Add(new SourceToken(">", TokenKind.Punctuation, "", " "));
                target.Add(new SourceToken("^", TokenKind.Punctuation));
                return;
            }

            if (t.IsGenericType)
            {
                StringBuilder sb = new StringBuilder(100);
                sb.Append(GetGenericDefinitionName(t.Name));
                sb.Append('<');
                Type[] args = t.GetGenericArguments();

                for (int i = 0; i < args.Length; i++)
                {
                    if (i >= 1) sb.Append(", ");

                    sb.Append(GetTypeString(args[i]));
                }

                sb.Append("> ^");
                target.Add(new SourceToken(sb.ToString(), TokenKind.Unknown));
                return;
            }

            //reference types are represented by handles in C++/CLI
            if (t.IsClass || t.IsInterface)
            {
                target.Add(new SourceToken(t.Name, TokenKind.TypeName, "", " "));
                target.Add(new SourceToken("^", TokenKind.Punctuation));
                return;
            }

            //process built-in types
            SourceToken tok = ProcessCommonTypes(t);

            if (tok != null)
            {
                target.Add(tok);
                return;
            }

            if (Utils.TypeEquals(t, typeof(uint)))
            {
                target.Add(new SourceToken("unsigned int", TokenKind.Keyword));
            }
            else if (Utils.TypeEquals(t, typeof(ushort)))
            {
                target.Add(new SourceToken("unsigned short", TokenKind.Keyword));
            }
            else if (Utils.TypeEquals(t, typeof(byte)))
            {
                target.Add(new SourceToken("unsigned char", TokenKind.Keyword));
            }
            else if (Utils.TypeEquals(t, typeof(sbyte)))
            {
                target.Add(new SourceToken("signed char", TokenKind.Keyword));
            }
            else if (Utils.TypeEquals(t, typeof(char)))
            {
                target.Add(new SourceToken("wchar_t", TokenKind.Keyword));
            }
            else
            {
                target.Add(new SourceToken(t.Name, TokenKind.TypeName));
            }
        }

        static string GetTypeString(Type t)
        {
            if (t == null) return string.Empty;

            if (t.IsArray && t.GetArrayRank() == 1)
            {
                StringBuilder sb = new StringBuilder(100);
                sb.Append("array <");
                sb.Append(GetTypeString(t.GetElementType()));
                sb.Append("> ^");
                return sb.ToString();
            }

            if (t.IsGenericType)
            {
                StringBuilder sb = new StringBuilder(100);
                sb.Append(GetGenericDefinitionName(t.Name));
                sb.Append('<');
                Type[] args = t.GetGenericArguments();

                for (int i = 0; i < args.Length; i++)
                {
                    if (i >= 1) sb.Append(", ");

                    sb.Append(GetTypeString(args[i]));
                }

                sb.Append("> ^");
                return sb.ToString();
            }

            //reference types are represented by handles in C++/CLI
            if (t.IsClass || t.IsInterface) return t.Name + " ^";

            //process built-in types
            SourceToken tok = ProcessCommonTypes(t);

            if (tok != null) return tok.Content;

            if (Utils.TypeEquals(t, typeof(uint)))        return "unsigned int";
            else if (Utils.TypeEquals(t, typeof(ushort))) return "unsigned short";
            else if (Utils.TypeEquals(t, typeof(byte)))   return "unsigned char";
            else if (Utils.TypeEquals(t, typeof(sbyte)))  return "signed char";
            else if (Utils.TypeEquals(t, typeof(char)))   return "wchar_t";

            return t.Name;
        }
        
        public override IEnumerable<SourceToken> GetMethodSigTokens()
        {
            MethodBase m = this._method;
            List<SourceToken> ret = new List<SourceToken>();
            
            ParameterInfo[] pars = m.GetParameters();

            //global functions don't have access modifiers
            bool isGlobalFunc = m.DeclaringType == null || Utils.StringEquals(m.DeclaringType.Name, "<Module>");

            if (!isGlobalFunc)
            {
                if (m.IsPublic)
                {
                    ret.Add(new SourceToken("public", TokenKind.Keyword));
                    ret.Add(new SourceToken(":", TokenKind.Punctuation, "", " "));
                }
                else if (m.IsFamily)
                {
                    ret.Add(new SourceToken("protected", TokenKind.Keyword));
                    ret.Add(new SourceToken(":", TokenKind.Punctuation, "", " "));
                }
                else if (m.IsAssembly)
                {
                    ret.Add(new SourceToken("internal", TokenKind.Keyword));
                    ret.Add(new SourceToken(":", TokenKind.Punctuation, "", " "));
                }
            }

            if (m.IsGenericMethod)
            {
                StringBuilder sb = new StringBuilder(100);
                sb.Append("generic <");

                Type[] args = m.GetGenericArguments();
                for (int i = 0; i < args.Length; i++)
                {
                    if (i >= 1) sb.Append(", ");

                    sb.Append("typename ");
                    sb.Append(args[i].Name);
                }

                sb.Append('>');
                sb.AppendLine();
                ret.Add(new SourceToken(sb.ToString(), TokenKind.Unknown));
            }

            if (!isGlobalFunc)
            {
                if (m.IsStatic) ret.Add(new SourceToken("static", TokenKind.Keyword, "", " "));
            }

            Type t = GetReturnType(m);
            
            if (t != null)
            {
                if (Utils.StringEquals(t.FullName, "System.Void"))
                {
                    ret.Add(new SourceToken("void", TokenKind.Keyword, "", " "));
                }
                else
                {
                    GetTypeTokens(t, ret);
                    ret.Add(new SourceToken(" ", TokenKind.Unknown));
                }
            }

            ret.Add(new SourceToken(m.Name, TokenKind.FunctionName));
            ret.Add(new SourceToken("(", TokenKind.Punctuation));
            
            for (int i = 0; i < pars.Length; i++)
            {
                if (i >= 1) ret.Add(new SourceToken(",", TokenKind.Punctuation, "", " "));
                GetTypeTokens(pars[i].ParameterType, ret);
                ret.Add(new SourceToken(" ", TokenKind.Unknown));
                
                string parname = pars[i].Name;

                if (string.IsNullOrEmpty(parname))
                {
                    parname = "par" + (i + 1).ToString();
                }

                ret.Add(new SourceToken(parname, TokenKind.Name));
            }

            ret.Add(new SourceToken(")", TokenKind.Punctuation, "", ""));

            //due to K&R braces in C++ the opening brace is effectively a part of signature
            ret.Add(new SourceToken("{", TokenKind.Punctuation));

            return ret;
        }
    }
}
