using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using IronPython;
using IronPython.Compiler;
using IronPython.Compiler.Ast;
using IronPython.Hosting;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Providers;
using Microsoft.Scripting.Runtime;

namespace PyToJs
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ConcurrentQueue<string> inputText = new ConcurrentQueue<string>();

        private class ThreadContext : INotifyPropertyChanged
        {
            public Thread WorkerThread;
            public Dispatcher UIThread;
            public ConcurrentQueue<string> Queue;
            public TextEditor Output;
            public System.Windows.Controls.ListBox Errors;

            public bool Running;

            public void SetOutput(string text)
            {
                UIThread.Invoke((Action)delegate()
                {
                    Output.Text = text;
                });
            }

            public void SetErrors(IEnumerable<string> msg)
            {
                UIThread.Invoke((Action)delegate()
                {
                    Errors.ItemsSource = msg.ToArray();
                });
            }

            public void Suspend()
            {
                UIThread.Invoke((Action)delegate()
                {
                    WorkerThread.Suspend();
                });
            }

            protected void OnPropertyChanged(string name)
            {
                if (PropertyChanged != null)
                {
                    PropertyChanged(this, new PropertyChangedEventArgs(name));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private ThreadContext runningThread;

        public MainWindow()
        {
            runningThread = new ThreadContext
            {
                WorkerThread = new Thread(Transformator),
                UIThread = Dispatcher,
                Queue = inputText,
                Running = true
            };
            DataContext = runningThread;

            InitializeComponent();

            runningThread.Output = output;
            runningThread.Errors = errors;
            runningThread.WorkerThread.Start(runningThread);

            input.SyntaxHighlighting = HighlightingLoader.Load(
                new XmlTextReader("Python.xshd"), HighlightingManager.Instance
            );

            output.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("JavaScript");
        }

        private class LocalSink : ErrorSink
        {
            private List<string> messages = new List<string>();

            public bool IsError { get; private set; }

            public IEnumerable<string> Messages { get { return messages; } }

            public override void Add(SourceUnit source, string message, SourceSpan span, int errorCode, Severity severity)
            {
                IsError = true;

                messages.Add(String.Format(
                    "{0} [{1}]: #{2} {3}",
                    severity,
                    span,
                    errorCode,
                    message
                ));
            }

            public override void Add(string message, string path, string code, string line, SourceSpan span, int errorCode, Severity severity)
            {
                IsError = true;

                messages.Add(String.Format(
                    "{0} [{1}]: #{2} {3}",
                    severity,
                    span,
                    errorCode,
                    message
                ));
            }

            public override string ToString()
            {
                return String.Join("\n", messages);
            }
        }

        private static void Transformator(object context)
        {
            ThreadContext p = (ThreadContext)context;

            ScriptRuntime runtime = Python.CreateRuntime();
            ScriptEngine engine = runtime.GetEngine("py");
            LanguageContext languageContext = HostingHelpers.GetLanguageContext(engine);

            while (p.Running)
            {
                string pySrc;
                if (p.Queue.TryDequeue(out pySrc))
                {
                    LocalSink sink = new LocalSink();

                    try
                    {
                        SourceUnit src = HostingHelpers.GetSourceUnit(engine.CreateScriptSourceFromString(pySrc));

                        CompilerContext ctx = new CompilerContext(src, languageContext.GetCompilerOptions(), sink);
                        Parser parser = Parser.CreateParser(ctx, (PythonOptions)languageContext.Options);

                        PythonAst ast = parser.ParseFile(true);
                        JavascriptGenerator generator = new JavascriptGenerator(src, sink);

                        if (sink.IsError)
                        {
                            p.SetOutput(null);
                        }
                        else
                        {
                            p.SetOutput(generator.ToJavaScript(ast));
                        }
                    }
                    catch (Exception e)
                    {
                        if (!sink.IsError)
                        {
                            p.SetOutput(e.ToString()); 
                        }
                    }
                    finally
                    {
                        p.SetErrors(sink.Messages);
                    }
                }
                else
                {
                    Thread.Sleep(50);
                    if (p.Queue.Count == 0)
                    {
                        p.Suspend(); 
                    }
                }
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            runningThread.Running = false;
            try
            {
                runningThread.WorkerThread.Resume();
            }
            catch (Exception)
            {
                runningThread.WorkerThread.Abort();
            }
        }

        private void input_TextChanged(object sender, EventArgs e)
        {
            inputText.Enqueue(input.Text);
            try
            {
                runningThread.WorkerThread.Resume();
            }
            catch (Exception)
            {
            }
        }
    }
}
