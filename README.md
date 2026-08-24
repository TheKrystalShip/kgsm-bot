# KGSM-Bot

KGSM-Bot is a Discord bot designed to interact with [KGSM][1], providing an 
intuitive interface to manage and control game servers directly from Discord.

## Features

- Server management: Install, start, stop, uninstall game servers through 
Discord commands.
- Live notifications: Realtime notifications for game server startup and 
shutdown.
- Modern Architecture: Built following SOLID principles and Clean Architecture.
- Proper Dependency Injection: Uses Microsoft's built-in DI container.
- Advanced Error Handling: Result pattern for operation outcomes.
- Consistent Logging: Integrated with Microsoft.Extensions.Logging.

## Getting started

KGSM-Bot was designed to work on GNU/Linux systems. There is no compatibility 
with Windows, try at your own risk.

### Prerequisites

- Discord Bot Token: Create a bot via the [Discord Developer Portal][2]
- [KGSM][1], version 1.6+
- [.NET SDK][5] version 10.0 or higher

### Configuration

`src/KGSM.Bot.Discord/kgsm-bot.settings.json` declares the bot's whole configurable surface with
its defaults, and ships beside the binary. It holds no secret: the token belongs in the environment
file, which overrides one key of that file at a time by spelling the key's path with `__`.

Copy `deploy/kgsm-bot.env.example` to `/etc/kgsm-bot/kgsm-bot.env` (chmod 600) and fill it in:

```sh
Discord__Token=YOUR_DISCORD_TOKEN_HERE_DO_NOT_SHARE
KGSM__Path=/usr/local/bin/kgsm
```

A variable naming a key the settings file does not declare binds to nothing, so check the spelling
against that file — a test fails the build if the template ever names one that is not declared.

**No Discord server is named in either file.** Invite the bot, then run `/setup announce` in the
Discord server that should hear about this host — anyone holding KGSM admin can, and it takes effect
at once. That one channel is a working setup; `/setup board` additionally gives each game server its
own channel under a category, and needs **Manage Channels**. The bot records all of it in its own
store at `/var/lib/kgsm-bot/bot.db`, outside the install prefix the deploy overwrites.

### Project Architecture

The project follows Clean Architecture principles with the following layers:

- **KGSM.Bot.Core**: Business logic interfaces and shared utilities
- **KGSM.Bot.Application**: Application services, commands, and queries
- **KGSM.Bot.Infrastructure**: External service implementations (Discord, KGSM)
- **KGSM.Bot.Discord**: Discord command modules and main application

### Building and Running

1. Clone the repository:
```sh
git clone https://github.com/TheKrystalShip/KGSM-Bot.git
```

2. Navigate to the project directory:
```sh
cd KGSM-Bot
```

3. Restore dependencies:
```sh
dotnet restore
```

4. Build the project:
```sh
dotnet build
```

5. Run the bot:
```sh
cd src/KGSM.Bot.Discord
dotnet run
```

## Usage

After inviting the bot to your Discord server, you can use commands such as:
- `/install [blueprint]`: Install a new game server
- `/start [instance]`: Start a game server
- `/status [instance]`: Check the status of a game server
- `/stop [instance]`: Stop a game server
- `/uninstall [instance]`: Uninstall a game server
- `/ping`: Check if the bot is responsive
- `/about`: Show information about the bot

## KGSM-Lib Integration

KGSM-Bot uses TheKrystalShip.KGSM.Lib to directly interact with the KGSM server. 
This integration provides:

- Direct access to KGSM functionality through a type-safe API
- Shared models and interfaces for consistency
- Reduced code duplication
- Native support for KGSM events and operations

By leveraging the KGSM-Lib's `Instance` class, KGSM-Bot maintains consistent data models 
throughout the application without requiring any custom mapping or transformation.

## Contributing

Contributions are welcome! Please open an issue or submit a pull request.

## License

This project is licensed under the GNU General Public License v3.0 or later. See the [LICENSE][3] file for 
details.

## Acknowledgements
- [Discord.Net][4] for the Discord API wrapper.
- [KGSM][1] for the game server management.
- [KGSM-Lib][6] for the C# interop library for KGSM.

[1]: https://github.com/TheKrystalShip/KGSM
[2]: https://discord.com/developers/applications
[3]: LICENSE
[4]: https://github.com/discord-net/Discord.Net
[5]: https://dotnet.microsoft.com/download
[6]: https://github.com/TheKrystalShip/KGSM-Lib
