using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using Microsoft.Extensions.Logging;

using System.Reflection;

namespace KGSM.Bot.Discord;

/// <summary>
/// Handler for Discord interactions
/// </summary>
public class InteractionHandler
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactionService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InteractionHandler> _logger;

    public InteractionHandler(
        DiscordSocketClient client,
        InteractionService interactionService,
        IServiceProvider serviceProvider,
        ILogger<InteractionHandler> logger)
    {
        _client = client;
        _interactionService = interactionService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        try
        {
            // Add the event handlers
            _client.Ready += OnReadyAsync;
            _client.InteractionCreated += OnInteractionCreatedAsync;
            _interactionService.Log += OnInteractionServiceLogAsync;

            // Add the public modules that inherit InteractionModuleBase<T> to the InteractionService
            await _interactionService.AddModulesAsync(Assembly.GetExecutingAssembly(), _serviceProvider);

            // Process the result of the command execution
            _interactionService.InteractionExecuted += OnInteractionExecutedAsync;

            _logger.LogInformation("Interaction handler initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing interaction handler");
        }
    }

    private async Task OnReadyAsync()
    {
        try
        {
            // When the client is ready, we want to register our commands globally
            _logger.LogInformation("Registering interaction commands globally");

            await _interactionService.RegisterCommandsGloballyAsync();

            _logger.LogInformation("Interaction commands registered globally");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering interaction commands");
        }
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        try
        {
            // Create an execution context that matches the generic type parameter of your InteractionModuleBase<T> modules
            var context = new SocketInteractionContext(_client, interaction);

            // Execute the incoming command
            await _interactionService.ExecuteCommandAsync(context, _serviceProvider);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing interaction command");

            // If Slash Command execution fails it is most likely that the original interaction acknowledgement will persist.
            if (interaction.Type is InteractionType.ApplicationCommand)
            {
                try
                {
                    // Try to delete the original response
                    await interaction.GetOriginalResponseAsync()
                        .ContinueWith(async (msg) => await msg.Result.DeleteAsync());
                }
                catch
                {
                    // Ignore any errors here
                }
            }
        }
    }

    private Task OnInteractionServiceLogAsync(LogMessage log)
    {
        var logLevel = log.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Debug,
            _ => LogLevel.Information
        };

        _logger.Log(logLevel, log.Exception, "{Source}: {Message}", log.Source, log.Message);

        return Task.CompletedTask;
    }

    private async Task OnInteractionExecutedAsync(ICommandInfo commandInfo, IInteractionContext context, IResult result)
    {
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Command {CommandName} execution failed: {Error}",
                commandInfo.Name, result.ErrorReason);

            switch (result.Error)
            {
                // Printed verbatim. The precondition writes a whole sentence saying which refusal
                // this is — no account connected, an account switched off, a tier too low, or a
                // store that could not be read — and prefixing "you don't have permission" onto any
                // of the last three states the one thing that is not true about it.
                case InteractionCommandError.UnmetPrecondition:
                    await context.Interaction.RespondAsync(result.ErrorReason, ephemeral: true);
                    break;
                case InteractionCommandError.UnknownCommand:
                    _logger.LogWarning("Unknown command was invoked");
                    break;
                case InteractionCommandError.BadArgs:
                    await context.Interaction.RespondAsync(
                        $"Invalid command arguments: {result.ErrorReason}",
                        ephemeral: true);
                    break;
                case InteractionCommandError.Exception:
                    await context.Interaction.RespondAsync(
                        $"An error occurred while executing the command: {result.ErrorReason}",
                        ephemeral: true);
                    break;
                case InteractionCommandError.Unsuccessful:
                    await context.Interaction.RespondAsync(
                        $"Command could not be executed: {result.ErrorReason}",
                        ephemeral: true);
                    break;
                default:
                    await context.Interaction.RespondAsync(
                        $"Something went wrong: {result.ErrorReason ?? "Unknown error"}",
                        ephemeral: true);
                    break;
            }
        }
    }
}
