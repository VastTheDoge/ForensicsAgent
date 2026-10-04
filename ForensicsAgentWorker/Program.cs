using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

Console.WriteLine("Starting worker...");

var app = WebApplication.CreateSlimBuilder(args).Build();

app.MapPost("/run-command", async ([FromBody] string cmd) =>
{
	Console.WriteLine($"running command: {cmd}\n");
	
	var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
	{
		RedirectStandardOutput = true,
		RedirectStandardError = true,
	};
	
	using var process = Process.Start(psi);
	var stdout = process.StandardOutput.ReadToEndAsync();
	var err = process.StandardError.ReadToEndAsync();
	await process.WaitForExitAsync();
	
	// put together command output, truncate to a max of 15k chars just in case the agent runs something it should not run
	var output = string.Concat($"exit code {process.ExitCode}\ncommand output:\n{await stdout}{await err}".Take(15000));
	
	return output;
});

app.Run("http://0.0.0.0:6202");