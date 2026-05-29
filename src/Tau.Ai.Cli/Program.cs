using Tau.Ai.Cli;

return await AiCliRunner.CreateDefault()
    .RunAsync(args)
    .ConfigureAwait(false);
