using System.ClientModel;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

// Basic setup
var apikey = new ApiKeyCredential("noop");
var endpointOptions = new OpenAIClientOptions { Endpoint = new Uri("http://localhost:1234/v1"), NetworkTimeout = TimeSpan.FromMinutes(10)  };

var output = new StringBuilder();

Console.WriteLine("Enter seed: ");
var seed = Console.ReadLine() is { } line ? long.Parse(line) : 0;

Console.WriteLine("Enter model: ");
var model = Console.ReadLine() ?? string.Empty;

var outDir = Path.Join(Directory.GetCurrentDirectory(), "/output");

// our flags to check:
var flagDict = new Dictionary<string, (bool, string)>();

// Figure out the output file location
if (!Directory.Exists(outDir))
	Directory.CreateDirectory(outDir);

// API setup for reaching the worker in the VM
var http = new HttpClient { BaseAddress = new Uri("http://localhost:6202/") };

// LM Studio setup
var ai = new ChatClient(model, apikey, endpointOptions)
	.AsIChatClient()
	.AsBuilder()
	.UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 1000)
	.Build();

var chatOptions = new ChatOptions
{
	Tools = [
		AIFunctionFactory.Create(async (string cmd) =>
		{
			var log1 = $"\n{new string('-', 15)}\nrunning command:\n{cmd}";
			Console.WriteLine(log1);
			output.AppendLine(log1);
			
			var resp = await http.PostAsJsonAsync("run-command", cmd);
			var cmdOut = await resp.Content.ReadAsStringAsync();

			var log2 = $"output from command: {cmdOut}";
			Console.WriteLine(log2);

			return cmdOut;
		}, "run_command"),
		AIFunctionFactory.Create((string path, bool isEvidence, string? desc) =>
		{
			var log1 = $"\n{new string('-', 15)}\nFile flagged:\npath: {path}\nEvidence? {isEvidence}";
			Console.WriteLine(log1);
			output.AppendLine(log1);

			flagDict[path] = (isEvidence, desc ?? string.Empty);
			
			return $"file with path {path} has been updated to {(isEvidence ? string.Empty : "not " )}be included in evidence";
		},"flag_file"),
	],
	Seed = seed
};

// message history setup
List<ChatMessage> history =
[
	new(ChatRole.System, 
		"""
		You are a Digital Forensics AI agent designed to do an initial assessment and report of a system and its files including any anomalies or hidden data. You will have access to two tools:
		1. A tool to tag files and folders for review along with a description with any additional information when useful.
		2. A tool to run any command on the system that is recognized. Always verify that you are limiting the length of the output (when reading a file, only read the first few bytes as one of many examples), verify that the command should yield different data than what you have found before, and verify that you are looking at the right folder in the command as your location in the file system does not change  when ran as separate commands.
		
		
		During a review you are to look at each file in detail to determine if there could be hidden data. 
		Flagging files should be done with the original file paths even if a file is moved or renamed in your investigation.
		You will not have user input and will run autonomously, only stop when all possible evidence has been reviewed and tagged.
		"""),
	new(ChatRole.User, "This computer belonged to a former employee who attempted to gain access to other computers and to the company network remotely. Please review the common windows user folders and look for any anomalies."),
];

// stream output as agent works
List<ChatResponseUpdate> updates = [];
await foreach (var update in ai.GetStreamingResponseAsync(history, chatOptions))
{
	updates.Add(update);
	foreach (var content in update.Contents)
	{
		switch (content)
		{
			case TextReasoningContent thinking:
				Console.Write(thinking.Text);
				output.Append(thinking.Text);
				break;
			case TextContent text:
				Console.Write(text.Text);
				output.Append(text.Text);
				break;
		}
	}
}

var evidenceOutput = string.Concat(flagDict.Select(kv => $"{(kv.Value.Item1 ? 'Y' : 'N')}\t{kv.Key} \n {kv.Value.Item2}\n{new string('-', 15)}\n\n"));
Console.WriteLine(evidenceOutput);
output.AppendLine(evidenceOutput);


history.AddMessages(updates);

// write to output
var nextNum = Directory.EnumerateFiles(outDir, $"{model.Replace('/', '_')} - {seed} - *").Count();
var filePath = Path.Join(outDir, $"{model.Replace('/', '_')} - {seed} - {nextNum}.txt");

File.WriteAllText(filePath, $"model: {model}\nseed: {seed}\nrun: {nextNum}\nDateTime: {DateTime.Now.ToShortDateString()} {DateTime.Now.ToShortTimeString()}\n\noutput:\n{output}");

Console.WriteLine("DONE");
Console.WriteLine($"Wrote to {filePath}");

//pause rq
Console.ReadLine();