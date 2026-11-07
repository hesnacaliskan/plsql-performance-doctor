using System.Text;
using PlsqlDoctor.Cli;

Console.OutputEncoding = Encoding.UTF8;
return DoctorCli.Run(args, Console.Out, Console.Error, Console.In);
