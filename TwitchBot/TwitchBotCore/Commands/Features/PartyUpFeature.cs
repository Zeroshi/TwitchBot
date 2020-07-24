﻿using System;
using System.Threading.Tasks;

using TwitchBotConsoleApp.Libraries;
using TwitchBotConsoleApp.Models;
using TwitchBotConsoleApp.Models.JSON;

using TwitchBotDb.Models;
using TwitchBotDb.Services;

using TwitchBotUtil.Config;
using TwitchBotUtil.Enums;

namespace TwitchBotConsoleApp.Commands.Features
{
    /// <summary>
    /// The "Command Subsystem" for the "Party Up" feature
    /// </summary>
    public sealed class PartyUpFeature : BaseFeature
    {
        private readonly TwitchInfoService _twitchInfo;
        private readonly GameDirectoryService _gameDirectory;
        private readonly PartyUpService _partyUp;
        private readonly BroadcasterSingleton _broadcasterInstance = BroadcasterSingleton.Instance;
        private readonly ErrorHandler _errHndlrInstance = ErrorHandler.Instance;

        public PartyUpFeature(IrcClient irc, TwitchBotConfigurationSection botConfig, TwitchInfoService twitchInfo,
            GameDirectoryService gameDirectory, PartyUpService partyUp) : base(irc, botConfig)
        {
            _twitchInfo = twitchInfo;
            _gameDirectory = gameDirectory;
            _partyUp = partyUp;
            _rolePermission.Add("!partyup", new CommandPermission { General = ChatterType.Viewer });
            _rolePermission.Add("!partyuprequestlist", new CommandPermission { General = ChatterType.Viewer });
            _rolePermission.Add("!partyuplist", new CommandPermission { General = ChatterType.Viewer });
            _rolePermission.Add("!poppartyup", new CommandPermission { General = ChatterType.VIP });
            _rolePermission.Add("!poppartyuprequest", new CommandPermission { General = ChatterType.VIP });
        }

        public override async Task<(bool, DateTime)> ExecCommand(TwitchChatter chatter, string requestedCommand)
        {
            try
            {
                switch (requestedCommand)
                {
                    case "!partyup":
                        return (true, await PartyUp(chatter));
                    case "!partyuprequestlist":
                        return (true, await PartyUpRequestList());
                    case "!partyuplist":
                        return (true, await PartyUpList());
                    case "!poppartyup":
                    case "!poppartyuprequest":
                        return (true, await PopPartyUpRequest());
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                await _errHndlrInstance.LogError(ex, "PartyUpFeature", "ExecCommand(TwitchChatter, string)", false, requestedCommand, chatter.Message);
            }

            return (false, DateTime.Now);
        }

        /// <summary>
        /// Request party member if game and character exists in party up system
        /// </summary>
        /// <param name="chatter">User that sent the message</param>
        public async Task<DateTime> PartyUp(TwitchChatter chatter)
        {
            try
            {
                int inputIndex = chatter.Message.IndexOf(" ") + 1;

                // check if user entered something
                if (chatter.Message.Length < inputIndex)
                {
                    _irc.SendPublicChatMessage($"Please enter a party member @{chatter.DisplayName}");
                    return DateTime.Now;
                }

                // get current game info
                ChannelJSON json = await _twitchInfo.GetBroadcasterChannelById();
                string gameTitle = json.Game;
                string partyMemberName = chatter.Message.Substring(inputIndex);
                TwitchGameCategory game = await _gameDirectory.GetGameId(gameTitle);

                // attempt to add requested party member into the queue
                if (string.IsNullOrEmpty(gameTitle))
                {
                    _irc.SendPublicChatMessage("I cannot see the name of the game. It's currently set to either NULL or EMPTY. "
                        + "Please have the chat verify that the game has been set for this stream. "
                        + $"If the error persists, please have @{_botConfig.Broadcaster.ToLower()} retype the game in their Twitch Live Dashboard. "
                        + "If this error shows up again and your chat can see the game set for the stream, please contact my master with !support in this chat");
                    return DateTime.Now;
                }
                else if (game == null || game.Id == 0)
                {
                    _irc.SendPublicChatMessage("This game is not part of the \"Party Up\" system");
                    return DateTime.Now;
                }

                PartyUp partyMember = await _partyUp.GetPartyMember(partyMemberName, game.Id, _broadcasterInstance.DatabaseId);

                if (partyMember == null)
                {
                    _irc.SendPublicChatMessage($"I couldn't find the requested party member \"{partyMemberName}\" @{chatter.DisplayName}. "
                        + "Please check with the broadcaster for possible spelling errors");
                    return DateTime.Now;
                }

                if (await _partyUp.HasUserAlreadyRequested(chatter.DisplayName, game.Id, _broadcasterInstance.DatabaseId))
                {
                    _irc.SendPublicChatMessage($"You have already requested a party member. "
                        + $"Please wait until your request has been completed @{chatter.DisplayName}");
                    return DateTime.Now;
                }

                await _partyUp.AddPartyMember(chatter.DisplayName, partyMember.Id);

                _irc.SendPublicChatMessage($"@{chatter.DisplayName}: {partyMemberName} has been added to the party queue");

            }
            catch (Exception ex)
            {
                await _errHndlrInstance.LogError(ex, "PartyUpFeature", "PartyUp(TwitchChatter)", false, "!partyup", chatter.Message);
            }

            return DateTime.Now;
        }

        /// <summary>
        /// Check what other user's have requested
        /// </summary>
        public async Task<DateTime> PartyUpRequestList()
        {
            try
            {
                // get current game info
                ChannelJSON json = await _twitchInfo.GetBroadcasterChannelById();
                string gameTitle = json.Game;
                TwitchGameCategory game = await _gameDirectory.GetGameId(gameTitle);

                if (string.IsNullOrEmpty(gameTitle))
                {
                    _irc.SendPublicChatMessage("I cannot see the name of the game. It's currently set to either NULL or EMPTY. "
                        + "Please have the chat verify that the game has been set for this stream. "
                        + $"If the error persists, please have @{_botConfig.Broadcaster.ToLower()} retype the game in their Twitch Live Dashboard. "
                        + "If this error shows up again and your chat can see the game set for the stream, please contact my master with !support in this chat");
                }
                else if (game == null || game.Id == 0)
                    _irc.SendPublicChatMessage("This game is currently not a part of the \"Party Up\" system");
                else
                    _irc.SendPublicChatMessage(await _partyUp.GetRequestList(game.Id, _broadcasterInstance.DatabaseId));
            }
            catch (Exception ex)
            {
                await _errHndlrInstance.LogError(ex, "PartyUpFeature", "PartyUpRequestList()", false, "!partyuprequestlist");
            }

            return DateTime.Now;
        }

        /// <summary>
        /// Check what party members are available (if game is part of the party up system)
        /// </summary>
        public async Task<DateTime> PartyUpList()
        {
            try
            {
                // get current game info
                ChannelJSON json = await _twitchInfo.GetBroadcasterChannelById();
                string gameTitle = json.Game;
                TwitchGameCategory game = await _gameDirectory.GetGameId(gameTitle);

                if (string.IsNullOrEmpty(gameTitle))
                {
                    _irc.SendPublicChatMessage("I cannot see the name of the game. It's currently set to either NULL or EMPTY. "
                        + "Please have the chat verify that the game has been set for this stream. "
                        + $"If the error persists, please have @{_botConfig.Broadcaster.ToLower()} retype the game in their Twitch Live Dashboard. "
                        + "If this error shows up again and your chat can see the game set for the stream, please contact my master with !support in this chat");
                    return DateTime.Now;
                }
                else if (game == null || game.Id == 0)
                    _irc.SendPublicChatMessage("This game is currently not a part of the \"Party Up\" system");
                else
                    _irc.SendPublicChatMessage(await _partyUp.GetPartyList(game.Id, _broadcasterInstance.DatabaseId));
            }
            catch (Exception ex)
            {
                await _errHndlrInstance.LogError(ex, "PartyUpFeature", "PartyUpList()", false, "!partyuplist");
            }

            return DateTime.Now;
        }

        /// <summary>
        /// Removes first party memeber in queue of party up requests
        /// </summary>
        public async Task<DateTime> PopPartyUpRequest()
        {
            try
            {
                // get current game info
                ChannelJSON json = await _twitchInfo.GetBroadcasterChannelById();
                string gameTitle = json.Game;
                TwitchGameCategory game = await _gameDirectory.GetGameId(gameTitle);

                if (string.IsNullOrEmpty(gameTitle))
                {
                    _irc.SendPublicChatMessage("I cannot see the name of the game. It's currently set to either NULL or EMPTY. "
                        + "Please have the chat verify that the game has been set for this stream. "
                        + $"If the error persists, please have @{_botConfig.Broadcaster.ToLower()} retype the game in their Twitch Live Dashboard. "
                        + "If this error shows up again and your chat can see the game set for the stream, please contact my master with !support in this chat");
                }
                else if (game?.Id > 0)
                    _irc.SendPublicChatMessage(await _partyUp.PopRequestedPartyMember(game.Id, _broadcasterInstance.DatabaseId));
                else
                    _irc.SendPublicChatMessage("This game is not part of the \"Party Up\" system");
            }
            catch (Exception ex)
            {
                await _errHndlrInstance.LogError(ex, "PartyUpFeature", "PopPartyUpRequest()", false, "!poppartyuprequest");
            }

            return DateTime.Now;
        }
    }
}
