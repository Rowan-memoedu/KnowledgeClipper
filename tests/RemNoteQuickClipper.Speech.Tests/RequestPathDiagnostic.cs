using System;
using System.Reflection;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Speech.Tests
{
    // Read-only .NET Framework probe: do not call Program.Run or launch any tool.
    internal static class RequestPathDiagnostic
    {
        private static int Main(string[] args)
        {
            try
            {
                var assembly = Assembly.LoadFile(args[0]);
                var cli = assembly.GetType("KnowledgeClipper.Speech.Worker.WorkerCli", true);
                var context = cli.GetMethod("DeriveRunContext").Invoke(null, new object[] { args[1] });
                var contextType = context.GetType();
                var jobRoot = (string)contextType.GetProperty("JobRoot").GetValue(context, null);
                var readerType = assembly.GetType("KnowledgeClipper.Speech.Worker.SpeechRequestReader", true);
                readerType.GetMethod("ReadAndValidate").Invoke(Activator.CreateInstance(readerType), new object[] { jobRoot, args[1] });
                Console.WriteLine(new JavaScriptSerializer().Serialize(new { status = "passed", requestPathLength = args[1].Length, jobRoot = jobRoot, toolInvocations = 0 }));
                return 0;
            }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                Console.WriteLine(new JavaScriptSerializer().Serialize(new { status = "failed", exception = error.ToString(), toolInvocations = 0 }));
                return 1;
            }
        }
    }
}
