using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using IronPython.Compiler.Ast;
using Microsoft.Scripting;

namespace PyToJs{
    public class JavascriptGenerator : PythonWalker
    {
        public const int INVALID_PARAM_ORDER = 90001;
        public const int UNSUPPORTED_OPERATOR = 90002;
        public const int UNSUPPORTED_STATEMENT = 90003;
        public const int TOKEN_ALREADY_DECLARED = 90004;
        public const int TUPLE_COUNT_DIFFERS = 90005;
        public const int TUPLE_ASSIGNMENT = 90006;
        public const int NAMED_PARAMETER = 90007;
        public const int GLOBBAL_VARAIBLE = 90008;
        public const int PARAM_AS_GLOBBAL_VARAIBLE = 90009;
        public const int INVALID_CLASS_METHOD = 90010;
        public const int RESERVED_WORD = 90011;

        private readonly Stack<string> content = new Stack<string>();
        private readonly Stack<Node> tree = new Stack<Node>();
        private readonly Stack<Scope> scope = new Stack<Scope>();
        private uint indent = 0;
        private SourceUnit src;
        private ErrorSink sink;

        public JavascriptGenerator(SourceUnit src, ErrorSink sink)
            : base()
        {
            this.src = src;
            this.sink = sink;
        }

        private Node Parent(int level = 1)
        {
            if (tree.Count == 0)
            {
                return null;
            }
            level++;

            int cnt = 0;
            Node treeNode = null;

            foreach (var item in tree)
            {
                treeNode = item;

                cnt++;
                if (cnt == level)
                {
                    break;
                }
            }

            return treeNode;
        }

        private class Scope
        {
            private readonly Stack<HashSet<string>> scope = new Stack<HashSet<string>>();

            public void BeginLevel()
            {
                scope.Push(new HashSet<string>());
            }

            public void EndLevel()
            {
                scope.Pop();
            }

            public bool IsDefined(string name)
            {
                return scope.Any(sc => sc.Contains(name));
            }

            public void Define(string name)
            {
                var sc = scope.Peek();
                sc.Add(name);
            }
        }

        private void CommonPostWalk(Node node, bool skip = false)
        {
            if (tree.Count > 0)
            {
                tree.Pop();
            }

            if (skip)
            {
                return;
            }

            Node parent = tree.Count > 0
                ? tree.Peek()
                : null;

            if (parent is SuiteStatement && content.Count > 0)
            {
                var s = Content();
                Content("{0}{1};", Indent(), s);
            }
        }

        private void CommonWalk(Node node)
        {
            tree.Push(node);
        }

        private void BeginScope()
        {
            scope.Push(new Scope());
        }

        private void EndScope()
        {
            scope.Pop();
        }

        private void BeginScopeLevel()
        {
            var currentScope = scope.Peek();
            currentScope.BeginLevel();
        }

        private void EndScopeLevel()
        {
            var currentScope = scope.Peek();
            currentScope.EndLevel();
        }

        private bool IsDefined(string name, bool global = false)
        {
            var currentScope = scope.Peek();

            if (global && scope.Count > 1)
            {
                scope.Pop();
                var globalScope = scope.Peek();
                scope.Push(currentScope);

                return currentScope.IsDefined(name) || globalScope.IsDefined(name);
            }

            return currentScope.IsDefined(name);
        }

        private void Define(string name)
        {
            var currentScope = scope.Peek();
            currentScope.Define(name);
        }

        private void Content(string s)
        {
            content.Push(s);
        }

        private void Content(string template, params object[] arg)
        {
            content.Push(String.Format(template, arg));
        }

        private string Content()
        {
            return content.Pop();
        }

        private string View()
        {
            return content.Peek();
        }

        private void BeginIndent()
        {
            indent++;
        }

        private void EndIndent()
        {
            indent--;
        }

        const int INDENT_SIZE = 4;

        private string Indent(uint? level = null)
        {
            level = (level ?? indent) - 1;
            return new String(' ', (int)(level * INDENT_SIZE));
        }


        public override bool Walk(AndExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(AndExpression node)
        {
            string right = Content();
            string left = Content();

            Content("{0} && {1}", left, right);

            CommonPostWalk(node, true);
        }


        public override bool Walk(Arg node)
        {
            if (!String.IsNullOrWhiteSpace(node.Name))
            {
                sink.Add(src, "Named parameters are not supported.", node.Span, NAMED_PARAMETER, Severity.FatalError);
            }

            CommonWalk(node);

            return true;
        }

        public override void PostWalk(Arg node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(AssertStatement node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(AssertStatement node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(AssignmentStatement node)
        {
            if (node.Left.Any(item => item is TupleExpression)
                && node.Right is ConstantExpression)
            {
                sink.Add(src, "Cannot assign CONSTANT to TUPLE", node.Span, TUPLE_ASSIGNMENT, Severity.Error);
            }

            if (node.Right is TupleExpression && node.Left.Any(item => item is TupleExpression))
            {
                TupleExpression rightTuple = (TupleExpression)node.Right;
                int count = node.Left.Sum(item => item is TupleExpression
                    ? ((TupleExpression)item).Items.Count
                    : 1
                );

                if (count != rightTuple.Items.Count)
                {
                    sink.Add(src, "Tuple item count on LEFT and RIGHT side differs", node.Span, TUPLE_COUNT_DIFFERS, Severity.Error);
                }
            }

            foreach (var tuple in node.Left.OfType<TupleExpression>())
            {
                string[] names = tuple.Items.OfType<NameExpression>().Select(item => item.Name).ToArray();
                if (names.Length <= 1)
                {
                    continue;
                }

                for (int i = 0; i < names.Length - 1; i++)
                {
                    string cName = names[i];

                    for (int j = i + 1; j < names.Length; j++)
                    {
                        if (cName == names[j])
                        {
                            sink.Add(src, String.Format("Tuple definition already declares item with name {0}", cName), node.Span, TOKEN_ALREADY_DECLARED, Severity.Error);
                        }
                    }
                }
            }

            CommonWalk(node);
            return true;
        }

        public override void PostWalk(AssignmentStatement node)
        {
            string right = Content();
            bool isInClass = Parent() is ClassDefinition || Parent(2) is ClassDefinition;
            bool isTuple = node.Right is TupleExpression || node.Left.Any(item => item is TupleExpression);

            List<string> left = new List<string>();
            for (int i = 0; i < node.Left.Sum(item => item is TupleExpression
                ? ((TupleExpression)item).Items.Count
                : 1); i++)
            {
                left.Add(Content());
            }
            left.Reverse();

            StringBuilder sb = new StringBuilder();
            string ind = isInClass
                ? String.Empty
                : Indent();

            int idx = 0;
            foreach (var item in node.Left)
            {
                bool varIsDefined = false;
                int count = 1;

                if (item is MemberExpression)
                {
                    varIsDefined = true;
                }
                else
                {
                    if (item is TupleExpression)
                    {
                        count = ((TupleExpression)item).Items.Count;
                    }
                }

                if (count > 1)
                {
                    string first = left[idx++];
                    InsertAssignment(IsDefined(first) || isInClass, sb, ind, first, right);

                    for (int i = 1; i < count; i++)
                    {
                        if (i > 0)
                        {
                            sb.AppendLine();
                        }

                        string name = left[idx++];
                        string value = String.Format("{0}[{1}]", first, idx - 1);

                        InsertAssignment(IsDefined(name) || isInClass, sb, ind, name, value);
                    }

                    sb.AppendLine();
                    InsertAssignment(true, sb, ind, first, String.Format("{0}[0]", first, idx - 1));
                }
                else
                {
                    if (idx > 0)
                    {
                        sb.AppendLine();
                    }

                    string name = left[idx++];
                    varIsDefined |= IsDefined(name) || isInClass;

                    InsertAssignment(varIsDefined, sb, ind, name, right);

                    right = name;
                }
            }

            Content(sb.ToString());

            CommonPostWalk(node, true);
        }

        private void InsertAssignment(bool isDefined, StringBuilder sb, string ind, string name, string value)
        {
            if (isDefined)
            {
                sb.AppendFormat("{0}{1} = {2};", ind, name, value);
            }
            else
            {
                sb.AppendFormat("{0}var {1} = {2};", ind, name, value);
                Define(name);
            }
        }


        public override bool Walk(AugmentedAssignStatement node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(AugmentedAssignStatement node)
        {
            string right = Content();
            string left = Content();

            switch (node.Operator)
            {
                case IronPython.Compiler.PythonOperator.Add:
                    Content("{0} += {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.BitwiseAnd:
                    Content("{0} &= {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.BitwiseOr:
                    Content("{0} |= {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Divide:
                    Content("{0} /= {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Xor:
                    //case IronPython.Compiler.PythonOperator.ExclusiveOr:
                    Content("{0} ^= {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.FloorDivide:
                    Content("{0} = Math.floor({0} / {1})", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.LeftShift:
                    Content("{0} <<= {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Mod:
                    Content("{0} %= {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Multiply:
                    Content("{0} = Python.mul({0}, {1})", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Pos:
                    break;

                case IronPython.Compiler.PythonOperator.Power:
                    Content("{0} = Math.pow({0}, {1})", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.RightShift:
                    Content("{0} >>= {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Subtract:
                    Content("{0} -= {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.TrueDivide:
                    break;

                case IronPython.Compiler.PythonOperator.Negate:
                case IronPython.Compiler.PythonOperator.None:
                case IronPython.Compiler.PythonOperator.Not:
                case IronPython.Compiler.PythonOperator.NotIn:
                case IronPython.Compiler.PythonOperator.LessThan:
                case IronPython.Compiler.PythonOperator.LessThanOrEqual:
                case IronPython.Compiler.PythonOperator.GreaterThan:
                case IronPython.Compiler.PythonOperator.GreaterThanOrEqual:
                case IronPython.Compiler.PythonOperator.In:
                case IronPython.Compiler.PythonOperator.Invert:
                case IronPython.Compiler.PythonOperator.Is:
                case IronPython.Compiler.PythonOperator.IsNot:
                case IronPython.Compiler.PythonOperator.NotEqual:
                //case IronPython.Compiler.PythonOperator.NotEquals:
                case IronPython.Compiler.PythonOperator.Equal:
                //case IronPython.Compiler.PythonOperator.Equals:
                default:
                    sink.Add(src, String.Format("Operator {0} is not supported.", node.Operator), node.Span, UNSUPPORTED_OPERATOR, Severity.FatalError);
                    break;
            }

            CommonPostWalk(node);
        }


        public override bool Walk(BackQuoteExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(BackQuoteExpression node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(BinaryExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(BinaryExpression node)
        {
            string right = Content();
            string left = Content();

            switch (node.Operator)
            {
                case IronPython.Compiler.PythonOperator.Add:
                    Content("{0} + {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.BitwiseAnd:
                    Content("{0} & {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.BitwiseOr:
                    Content("{0} | {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Divide:
                    Content("{0} / {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Equal:
                    //case IronPython.Compiler.PythonOperator.Equals:
                    Content("{0} === {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Xor:
                    //case IronPython.Compiler.PythonOperator.ExclusiveOr:
                    Content("{0} ^ {1}", left, right);

                    break;

                case IronPython.Compiler.PythonOperator.FloorDivide:
                    Content("Math.floor({0} / {1})", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.GreaterThan:
                    Content("{0} > {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.GreaterThanOrEqual:
                    Content("{0} >== {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.LeftShift:
                    Content("{0} << {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.LessThan:
                    Content("{0} < {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.LessThanOrEqual:
                    Content("{0} <== {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Mod:
                    Content("{0} % {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Multiply:
                    Content("Python.mul({0}, {1})", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.NotEqual:
                    //case IronPython.Compiler.PythonOperator.NotEquals:
                    Content("{0} != {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Power:
                    Content("Math.pow({0}, {1})", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.RightShift:
                    Content("{0} >> {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Subtract:
                    Content("{0} - {1}", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.In:
                    Content("Python.isIn({0}, {1})", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.NotIn:
                    Content("!Python.isIn({0}, {1})", left, right);
                    break;

                case IronPython.Compiler.PythonOperator.Invert:
                case IronPython.Compiler.PythonOperator.Is:
                case IronPython.Compiler.PythonOperator.IsNot:
                case IronPython.Compiler.PythonOperator.None:
                case IronPython.Compiler.PythonOperator.Pos:
                case IronPython.Compiler.PythonOperator.TrueDivide:
                default:
                    sink.Add(src, String.Format("Operator {0} is not supported.", node.Operator), node.Span, UNSUPPORTED_OPERATOR, Severity.FatalError);
                    break;
            }

            CommonPostWalk(node);
        }


        public override bool Walk(BreakStatement node)
        {
            CommonWalk(node);

            Content("break");

            return false;
        }

        public override void PostWalk(BreakStatement node)
        {
            CommonPostWalk(node);
        }


        private static readonly Dictionary<string, string> functionNameMapping = new Dictionary<string, string>
        {
            { "eval", "doEval" },
            { "float", "toFloat" },
            { "int", "toInt" },
            { "long", "toLong" },
            { "super", "makeSuper" },
            { "type", "getType" }
        };

        public override bool Walk(CallExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(CallExpression node)
        {
            List<string> args = new List<string>();
            for (int i = 0; i < node.Args.Count; i++)
            {
                args.Add(Content());
            }
            args.Reverse();

            string name = Content();

            Content(
                "{0}({1})", 
                functionNameMapping.ContainsKey(name)
                    ? functionNameMapping[name]
                    : name,
                String.Join(", ", args)
            );

            CommonPostWalk(node);
        }


        public override bool Walk(ClassDefinition node)
        {
            CheckForIllegalWords(node, node.Name);
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(ClassDefinition node)
        {
            SuiteStatement body = node.Body as SuiteStatement;

            int count = body == null
                ? 1
                : body.Statements.Count;

            var methods = body != null
                ? body.Statements
                    .OfType<FunctionDefinition>()
                    .ToList()
                : new List<FunctionDefinition>();

            var mDic = methods.ToDictionary(
                k => (Statement)k,
                v => -1
            );

            FunctionDefinition constructor = methods
                .FirstOrDefault(n => n.Name == "__init__");

            FunctionDefinition toString = methods
                .FirstOrDefault(n => n.Name == "__str__");

            List<string> statements = new List<string>();
            for (int i = count - 1; i >= 0; i--)
            {
                string st = Content();
                if (!String.IsNullOrWhiteSpace(st))
                {
                    if (body != null && body.Statements[i] is FunctionDefinition)
                    {
                        mDic[body.Statements[i]] = i;
                    }

                    statements.Add(st);
                }
            }
            statements.Reverse();

            StringBuilder sb = new StringBuilder();

            string[] constructorArgs = constructor != null
                ? constructor.Parameters
                    .Skip(1)
                    .Select(p => p.Name)
                    .ToArray()
                : new string[0];

            string factoryName = String.Format("{0}Class", node.Name);
            sb.AppendFormat("{0} = function({2}) {{ return new {1}({2}); }};", node.Name, factoryName, String.Join(", ", constructorArgs));
            sb.AppendLine();

            if (constructor != null)
            {
                sb.AppendFormat("{0} = {1}", factoryName, statements[mDic[constructor]]);
            }
            else
            {
                sb.AppendFormat("{0} = function() {{ }};", factoryName);
            }
            sb.AppendLine();

            for (int i = 0; i < statements.Count; i++)
            {
                if (mDic.Values.Contains(i))
                {
                    continue;
                }

                sb.AppendFormat("{0}.prototype.{1}", factoryName, statements[i]);
                sb.AppendLine();
                sb.AppendLine();
            }

            if (toString != null)
            {
                sb.AppendFormat("{0}.prototype.toString = {1}", factoryName, statements[mDic[toString]]);
                sb.AppendLine();
            }

            methods.Remove(constructor);
            methods.Remove(toString);

            foreach (var m in methods)
            {
                sb.AppendFormat("{0}.prototype.{1} = {2}", factoryName, m.Name, statements[mDic[m]]);
                sb.AppendLine();
            }

            Content(sb.ToString());

            CommonPostWalk(node, true);
        }


        public override bool Walk(ConditionalExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(ConditionalExpression node)
        {
            CommonPostWalk(node);
        }

        private void WriteString(StringBuilder sb, string s, bool addParnthesis = true)
        {
            if (addParnthesis)
            {
                sb.Append('\"');
            }

            foreach (char c in s)
            {
                switch (c)
                {
                    case '\t': sb.Append("\\t"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '"':
                    case '\\': sb.Append("\\" + c); break;
                    default:
                        {
                            if (c >= ' ' && c < 128)
                                sb.Append(c);
                            else
                                sb.Append("\\u" + ((int)c).ToString("X4"));
                        }
                        break;
                }
            }

            if (addParnthesis)
            {
                sb.Append('\"');
            }
        }

        private string WriteValue(object obj)
        {
            StringBuilder sb = new StringBuilder();

            if (obj == null)
            {
                sb.Append("null");
            }
            else if (obj is string)
            {
                WriteString(sb, (string)obj);
            }
            else if (obj is sbyte || obj is byte || obj is short || obj is ushort || obj is int || obj is uint || obj is long || obj is ulong || obj is decimal || obj is double || obj is float)
            {
                sb.Append(Convert.ToString(obj, NumberFormatInfo.InvariantInfo));
            }
            else if (obj is bool)
            {
                sb.Append(obj.ToString().ToLower());
            }
            else if (obj is char || obj is Enum || obj is Guid)
            {
                WriteString(sb, "" + obj);
            }
            else if (obj is DateTime)
            {
                DateTime dateTime = (DateTime)obj;

                // dateTime.Month - 1 because JavaScript count months from 0 - 11
                sb.AppendFormat(
                    "new Date({0},{1},{2},{3},{4},{5},{6})",
                    dateTime.Year,
                    dateTime.Month - 1,
                    dateTime.Day,
                    dateTime.Hour,
                    dateTime.Minute,
                    dateTime.Second,
                    dateTime.Millisecond
                );
            }

            return sb.ToString();
        }

        public override bool Walk(ConstantExpression node)
        {
            Content(WriteValue(node.Value));

            return false;
        }

        public override void PostWalk(ConstantExpression node)
        {
        }


        public override bool Walk(ContinueStatement node)
        {
            CommonWalk(node);

            Content("continue");

            return false;
        }

        public override void PostWalk(ContinueStatement node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(DelStatement node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(DelStatement node)
        {
            Content("delete {0}", Content());

            CommonPostWalk(node);
        }


        public override bool Walk(DictionaryExpression node)
        {
            CommonWalk(node);

            BeginIndent();

            return true;
        }

        public override void PostWalk(DictionaryExpression node)
        {
            EndIndent();

            string ind = Indent(indent + 1);
            List<string> items = new List<string>();
            for (int i = 0; i < node.Items.Count; i++)
            {
                items.Add(ind + Content());
            }
            items.Reverse();

            StringBuilder sb = new StringBuilder();

            sb.AppendLine("{");

            sb.AppendLine(String.Join(",\n", items));

            sb.Append(Indent());
            sb.Append("}");

            Content(sb.ToString());

            CommonPostWalk(node, true);
        }


        public override bool Walk(DottedName node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(DottedName node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(EmptyStatement node)
        {
            Content("");

            return true;
        }

        public override void PostWalk(EmptyStatement node)
        {
        }


        public override bool Walk(ErrorExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(ErrorExpression node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(ExecStatement node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(ExecStatement node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(ExpressionStatement node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(ExpressionStatement node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(ForStatement node)
        {
            CommonWalk(node);

            return true;
        }

        private const string LIST = "_{0}list_";
        private const string COUNTER = "_{0}cnt_";
        

        public override void PostWalk(ForStatement node)
        {
            string elseSt = node.Else != null
                ? Content()
                : null;
            string body = Content();
            string list = Content();
            string variable = Content();

            string listVar = String.Format(LIST, variable);
            string cntVar = String.Format(COUNTER, variable);

            StringBuilder sb = new StringBuilder();
            sb.Append(Indent());
            sb.AppendFormat("var {0} = _.toArray({1});", listVar, list);
            sb.AppendLine();

            sb.Append(Indent());
            sb.AppendFormat("for (var {0} = 0; {0} < {1}.length; {0}++) {{",
                cntVar,
                listVar
            );
            sb.AppendLine();

            sb.Append(Indent(indent + 1));
            sb.AppendFormat("var {0} = {1}[{2}];", variable, listVar, cntVar);
            sb.AppendLine();
            sb.AppendLine();

            sb.AppendLine(body);

            if (elseSt != null)
            {
                sb.AppendLine();

                sb.Append(Indent(indent + 1));
                sb.AppendFormat("if ({0} == {1}.length - 1) {{", cntVar, listVar);
                sb.AppendLine();

                sb.AppendLine(elseSt);

                sb.Append(Indent(indent + 1));
                sb.AppendLine("}");
            }

            sb.Append(Indent());
            sb.AppendLine("}");

            Content(sb.ToString());

            CommonPostWalk(node, true);
        }


        public override bool Walk(FromImportStatement node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(FromImportStatement node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(FunctionDefinition node)
        {
            CheckForIllegalWords(node, node.Name);

            bool optinoalParamsFound = false;
            for (int i = 0; i < node.Parameters.Count; i++)
            {
                var p = node.Parameters[i];
                if (optinoalParamsFound && p.DefaultValue == null)
                {
                    sink.Add(src, "All required parameters must be before all optional parameters", p.Span, INVALID_PARAM_ORDER, Severity.FatalError);
                }

                optinoalParamsFound |= p.DefaultValue != null;
            }

            bool isClassMethod = tree.Any(n => n is ClassDefinition);
            if (isClassMethod && node.Parameters.Count < 1)
            {
                sink.Add(src, "Class method must have at least one parameter.", node.Span, INVALID_CLASS_METHOD, Severity.FatalError);
            }

            CommonWalk(node);
            BeginScope();
            BeginScopeLevel();

            if (node.IsLambda || !(node.Body is SuiteStatement))
            {
                BeginIndent();
            }

            return true;
        }

        private void AddOptinonalParam(StringBuilder sb, Parameter p)
        {
            JavascriptGenerator gen = new JavascriptGenerator(src, sink);
            p.DefaultValue.Walk(gen);

            sb.AppendFormat("{0}if (typeof({1}) == 'undefined') {{",
                Indent(),
                p.Name
            );
            sb.AppendLine();

            sb.AppendFormat("{0}{1} = {2};",
                Indent(indent + 1),
                p.Name,
                gen.ToString()
            );
            sb.AppendLine();

            sb.AppendFormat("{0}}}", Indent());
            sb.AppendLine();
        }

        public override void PostWalk(FunctionDefinition node)
        {
            bool isClassMethod = tree.Any(n => n is ClassDefinition);

            StringBuilder sb = new StringBuilder();

            string body = Content();

            List<string> args = new List<string>();
            for (int i = 0; i < node.Parameters.Count; i++)
            {
                args.Add(Content());
            }
            args.Reverse();

            string classRefArg = null;
            if (isClassMethod)
            {
                classRefArg = args[0];
                args.RemoveAt(0);
            }

            sb.Append("function");
            if (!node.IsLambda && !isClassMethod)
            {
                sb.AppendFormat(" {0}", node.Name);
            }

            sb.Append("(");
            if (args.Count > 0)
            {
                sb.Append(String.Join(", ", args));
            }
            sb.AppendLine(") {");

            BeginIndent();
            if (!String.IsNullOrWhiteSpace(classRefArg))
            {
                sb.Append(Indent());
                sb.AppendFormat("var {0} = this;", classRefArg);
                sb.AppendLine();
                sb.AppendLine();
            }

            foreach (var item in node.Parameters.Where(p => p.DefaultValue != null))
            {
                AddOptinonalParam(sb, item);
            }
            EndIndent();

            sb.AppendLine(body);

            if (node.IsLambda || !(node.Body is SuiteStatement))
            {
                EndIndent();
            }

            if (node.IsLambda)
            {
                sb.Append("}");
            }
            else
            {
                sb.AppendLine(isClassMethod ? "};" : "}");
            }

            Content(sb.ToString());

            EndScopeLevel();
            EndScope();

            CommonPostWalk(node, !node.IsLambda);
        }


        public override bool Walk(GeneratorExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(GeneratorExpression node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(GlobalStatement node)
        {
            foreach (var name in node.Names)
            {
                if (IsDefined(name))
                {
                    sink.Add(src, String.Format("Variable \"{0}\" is already declared before or some value already is assigned to it.", name), node.Span, PARAM_AS_GLOBBAL_VARAIBLE, Severity.Warning);
                }

                sink.Add(src, String.Format("Variable \"{0}\" will point to GLOBAL variable. Incorrect usage of global variables my lead to hardly detectable bugs.", name), node.Span, GLOBBAL_VARAIBLE, Severity.Warning);
                Define(name);
            }

            return false;
        }

        public override void PostWalk(GlobalStatement node)
        {
            Content("");
        }


        public override bool Walk(IfStatement node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(IfStatement node)
        {
            string ifFalse = node.ElseStatement != null
                ? Content()
                : null;
            string ifTrue = Content();
            string test = Content();

            StringBuilder sb = new StringBuilder();

            sb.Append(Indent());
            sb.AppendFormat("if ({0}) {{", test);
            sb.AppendLine();

            sb.AppendLine(ifTrue);

            sb.Append(Indent());
            sb.Append("}");

            if (!String.IsNullOrWhiteSpace(ifFalse))
            {
                sb.AppendLine();

                sb.Append(Indent());
                sb.AppendFormat("else {{", test);
                sb.AppendLine();

                sb.AppendLine(ifFalse);

                sb.Append(Indent());
                sb.Append("}");
            }

            Content(sb.ToString());

            CommonPostWalk(node, true);
        }


        public override bool Walk(IfStatementTest node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(IfStatementTest node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(ImportStatement node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(ImportStatement node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(IndexExpression node)
        {
            CommonWalk(node);

            return true;
        }

        public override void PostWalk(IndexExpression node)
        {
            SliceExpression slice = node.Index as SliceExpression;

            if (slice != null)
            {
                List<string> parts = new List<string>();
                parts.Add(slice.SliceStop != null ? Content() : "null");
                if (slice.SliceStep != null)
                {
                    Content();
                }
                parts.Add(slice.SliceStart != null ? Content() : "null");

                string target = Content();

                Content("{0}.slice({1})", target, String.Join(", ", parts));
            }
            else
            {
                string index = Content();
                string target = Content();

                Content("{0}[{1}]", target, index);
            }

            CommonPostWalk(node);
        }


        public override bool Walk(LambdaExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(LambdaExpression node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(ListComprehension node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(ListComprehension node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(ListComprehensionFor node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(ListComprehensionFor node)
        {
            string list = Content();
            string variable = Content();
            string predicate = Content();

            Content(String.Format("Python.comprehensionFor({0}, function({1}) {{ return {2}; }})",
                list,
                variable,
                predicate
            ));

            CommonPostWalk(node, true);
        }


        public override bool Walk(ListComprehensionIf node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(ListComprehensionIf node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(ListExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(ListExpression node)
        {
            List<string> items = new List<string>();
            for (int i = 0; i < node.Items.Count; i++)
            {
                items.Add(Content());
            }
            items.Reverse();

            Content("[{0}]", String.Join(", ", items));

            CommonPostWalk(node, true);
        }


        public override bool Walk(MemberExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(MemberExpression node)
        {
            Content("{0}.{1}", Content(), node.Name);

            CommonPostWalk(node);
        }


        public override bool Walk(ModuleName node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(ModuleName node)
        {
            CommonPostWalk(node);
        }

        private static readonly string[] jsReservedWords = 
        {
            "abstract",
            "as",
            "boolean",
            "break",
            "byte",
            "case",
            "catch",
            "char",
            "class",
            "continue",
            "const",
            "debugger",
            "default",
            "delete",
            "do",
            "double",
            "else",
            "enum",
            "export",
            "extends",
            "false",
            "final",
            "finally",
            "float",
            "for",
            "function",
            "goto",
            "if",
            "implements",
            "import",
            "in",
            "instanceof",
            "int",
            "interface",
            "is",
            "long",
            "namespace",
            "native",
            "new",
            "null",
            "package",
            "private",
            "protected",
            "public",
            "return",
            "short",
            "static",
            "super",
            "switch",
            "synchronized",
            "this",
            "throw",
            "throws",
            "transient",
            "true",
            "try",
            "typeof",
            "use",
            "var",
            "void",
            "volatile",
            "while",
            "with"
        };

        private void CheckForIllegalWords(Node node, string word)
        {
            if (Array.IndexOf<string>(jsReservedWords, word) >= 0)
            {
                sink.Add(src, String.Format("\"{0}\" is reserved word in JavaScript and cannot be used.", word), node.Span, RESERVED_WORD, Severity.Error);
            }
        }

        public override bool Walk(NameExpression node)
        {
            if (!(Parent(0) is CallExpression) || !functionNameMapping.ContainsKey(node.Name))
            {
                CheckForIllegalWords(node, node.Name);
            }
            Content(node.Name);

            return false;
        }

        public override void PostWalk(NameExpression node)
        {
        }


        public override bool Walk(OrExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(OrExpression node)
        {
            string right = Content();
            string left = Content();

            Content("{0} || {1}", left, right);

            CommonPostWalk(node, true);
        }


        public override bool Walk(Parameter node)
        {
            Content(node.Name);
            Define(node.Name);

            return false;
        }

        public override void PostWalk(Parameter node)
        {
        }


        public override bool Walk(ParenthesisExpression node)
        {
            CommonWalk(node);

            return true;
        }

        public override void PostWalk(ParenthesisExpression node)
        {
            Content("({0})", Content());

            CommonPostWalk(node);
        }


        public override bool Walk(PrintStatement node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(PrintStatement node)
        {
            List<string> statements = new List<string>();
            for (int i = 0; i < node.Expressions.Count; i++)
            {
                statements.Add(Content());
            }
            statements.Reverse();

            Content("print([{0}])", String.Join(", ", statements));

            CommonPostWalk(node);
        }


        public override bool Walk(PythonAst node)
        {
            CommonWalk(node);
            BeginScope();

            return true;
        }

        public override void PostWalk(PythonAst node)
        {
            EndScope();
            CommonPostWalk(node, true);
        }


        public override bool Walk(RaiseStatement node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(RaiseStatement node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(RelativeModuleName node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(RelativeModuleName node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(ReturnStatement node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(ReturnStatement node)
        {
            var tmp = tree.Pop();
            var parent = tree.Peek() as FunctionDefinition;
            tree.Push(tmp);

            string ind = parent == null || !parent.IsLambda
                ? String.Empty
                : Indent();

            string terminator = parent == null || !parent.IsLambda
                ? String.Empty
                : ";";

            if (node.Expression != null)
            {
                Content("{0}return {1}{2}", ind, Content(), terminator);
            }
            else
            {
                Content("return");
            }

            CommonPostWalk(node);
        }


        public override bool Walk(SliceExpression node)
        {
            var parent = tree.Peek();
            if (parent is IndexExpression)
            {
                return true;
            }

            CommonWalk(node);
            return true;
        }

        public override void PostWalk(SliceExpression node)
        {
            var parent = tree.Peek();
            if (parent is IndexExpression)
            {
                return;
            }

            List<string> parts = new List<string>();
            if (node.SliceStop != null)
            {
                parts.Add(Content());
            }

            if (node.SliceStep != null)
            {
                parts.Add(Content());
            }

            if (node.SliceStart != null)
            {
                parts.Add(Content());
            }

            parts.Reverse();

            Content(String.Join(": ", parts));

            CommonPostWalk(node);
        }


        public override bool Walk(SublistParameter node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(SublistParameter node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(SuiteStatement node)
        {
            var parent = Parent();

            CommonWalk(node);
            BeginScopeLevel();

            if (!(parent is ClassDefinition))
            {
                BeginIndent(); 
            }

            if (parent is ForStatement)
            {
                ForStatement fors = (ForStatement)parent;
                if (fors.Else == node)
                {
                    BeginIndent();
                }
            }

            if (parent is WhileStatement)
            {
                WhileStatement whiles = (WhileStatement)parent;
                if (whiles.ElseStatement == node)
                {
                    BeginIndent();
                }
            }

            return true;
        }

        public override void PostWalk(SuiteStatement node)
        {
            var tmp = tree.Pop();
            bool isClassSuite = tree.Peek() is ClassDefinition;
            tree.Push(tmp);

            if (!isClassSuite)
            {
                List<string> statements = new List<string>();
                for (int i = 0; i < node.Statements.Count; i++)
                {
                    if (node.Statements[i] is ImportStatement)
                    {
                        continue;
                    }

                    string c = Content();
                    if (!String.IsNullOrWhiteSpace(c))
                    {
                        statements.Add(c);
                    }
                }
                statements.Reverse();

                if (statements.Count > 0)
                {
                    Content(String.Join("\n", statements));
                }
                else
                {
                    Content("");
                } 
            }

            var parent = Parent();
            if (!(parent is ClassDefinition))
            {
                EndIndent();
            }
            EndScopeLevel();
            CommonPostWalk(node, true);

            if (parent is ForStatement)
            {
                ForStatement fors = (ForStatement)parent;
                if (fors.Else == node)
                {
                    EndIndent();
                }
            }

            if (parent is WhileStatement)
            {
                WhileStatement whiles = (WhileStatement)parent;
                if (whiles.ElseStatement == node)
                {
                    EndIndent();
                }
            }
        }


        public override bool Walk(TryStatement node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(TryStatement node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(TryStatementHandler node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(TryStatementHandler node)
        {
            CommonPostWalk(node);
        }


        public override bool Walk(TupleExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(TupleExpression node)
        {
            var tmp = tree.Pop();
            var parent = tree.Peek() as AssignmentStatement;
            tree.Push(tmp);

            if (parent == null || !parent.Left.Contains(node))
            {
                List<string> statements = new List<string>();
                for (int i = 0; i < node.Items.Count; i++)
                {
                    statements.Add(Content());
                }
                statements.Reverse();

                Content("[{0}]", String.Join(", ", statements));
            }


            CommonPostWalk(node);
        }


        public override bool Walk(UnaryExpression node)
        {
            CommonWalk(node);
            return true;
        }

        public override void PostWalk(UnaryExpression node)
        {
            string statement = Content();

            switch (node.Op)
            {
                case IronPython.Compiler.PythonOperator.Negate:
                    Content("-{0}", statement);
                    break;

                case IronPython.Compiler.PythonOperator.Not:
                    Content("!{0}", statement);
                    break;

                case IronPython.Compiler.PythonOperator.In:
                case IronPython.Compiler.PythonOperator.Invert:
                case IronPython.Compiler.PythonOperator.Is:
                case IronPython.Compiler.PythonOperator.IsNot:
                case IronPython.Compiler.PythonOperator.None:
                case IronPython.Compiler.PythonOperator.NotIn:
                case IronPython.Compiler.PythonOperator.Pos:
                case IronPython.Compiler.PythonOperator.TrueDivide:
                default:
                    sink.Add(src, String.Format("Operator {0} is not supported.", node.Op), node.Span, UNSUPPORTED_OPERATOR, Severity.FatalError);
                    break;
            }

            CommonPostWalk(node);
        }


        public override bool Walk(WhileStatement node)
        {
            CommonWalk(node);

            return true;
        }

        public override void PostWalk(WhileStatement node)
        {
            string elseSt = node.ElseStatement != null
                ? Content()
                : null;
            string body = Content();
            string test = Content();

            StringBuilder sb = new StringBuilder();

            sb.Append(Indent());
            sb.AppendFormat("while ({0}) {{", test);
            sb.AppendLine();

            sb.AppendLine(body);

            if (elseSt != null)
            {
                sb.AppendLine();

                sb.Append(Indent(indent + 1));
                sb.AppendFormat("if (!({0})) {{", test);
                sb.AppendLine();

                sb.AppendLine(elseSt);

                sb.Append(Indent(indent + 1));
                sb.AppendLine("}");
            }

            sb.Append(Indent());
            sb.AppendLine("}");

            Content(sb.ToString());

            CommonPostWalk(node, true);
        }


        public override bool Walk(WithStatement node)
        {
            sink.Add(src, "WITH statement is not supported.", node.Span, UNSUPPORTED_STATEMENT, Severity.FatalError);
            return false;
        }

        public override void PostWalk(WithStatement node)
        {
        }


        public override bool Walk(YieldExpression node)
        {
            sink.Add(src, "YIELD statement is not supported.", node.Span, UNSUPPORTED_STATEMENT, Severity.FatalError);
            return false;
        }

        public override void PostWalk(YieldExpression node)
        {
        }

        public string ToJavaScript(PythonAst ast)
        {
            content.Clear();
            tree.Clear();
            scope.Clear();
            indent = 0;

            ast.Walk(this);

            return content.Pop();
        }

        public override string ToString()
        {
            return content.Peek();
        }
    }
}
