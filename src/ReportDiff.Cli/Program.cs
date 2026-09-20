using System.Text;
using ReportDiff.Cli;

Console.OutputEncoding = new UTF8Encoding(false);
return CliApplication.Run(args, Console.Out, Console.Error);
