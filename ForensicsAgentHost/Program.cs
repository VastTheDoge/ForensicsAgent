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

Console.WriteLine("Enter VM IP: ");
var ip  = Console.ReadLine() ?? string.Empty;

var outDir = Path.Join(Directory.GetCurrentDirectory(), "/output");

// our flags to check:
var flagDict = new Dictionary<string, (bool, string)>();

// Figure out the output file location
if (!Directory.Exists(outDir))
	Directory.CreateDirectory(outDir);

// API setup for reaching the worker in the VM
var http = new HttpClient { BaseAddress = new Uri($"http://{ip}:6202/") };

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
		You are a fully autonomous agent who is an expert in Digital Forensics.
		You goal is to do an in-depth investigation of a system and its files including any anomalies or hidden data.
		You are to start by gathering system information, then directory information, then looking at each and every file in detail (including file content) starting with common user folders.
		You are expected to take your time to find any and all leads.
		
		You will think through steps picking the best action at a time and never end your investigation with further steps as you are never lazy.
		
		ForensicsAgentWorker.exe is an expected file in downloads as you use it to run commands on the machine.
		
		In order to achieve your goal you will have two tools available:
		The first is a tool to run any command on the system to gather information and modify files with the purpose of extracting information. 
		When you are working with potentially large files, read only the first few bytes, enough to gather what the file is based on a signature and some of the content.
		
		The second tool you can use is a flag tool which let's you flag a file as evidence with a description to provide information on why it is evidence or how to extract the file or to flag a file as not being evidence to look at.
		"""),
	new(ChatRole.User, 
		"""
		You have been given access via your worker, you are to do an in-depth review of the user's files in the following folders: Desktop, Downloads, Documents, Pictures, Music, Videos. 
		This is a very focused review, so I expect a good bit of work without mistakes.
		"""),
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