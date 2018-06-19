﻿using System.Collections.Generic;
using System.Threading.Tasks;

using TwitchBot.Libraries;

using TwitchBotDb.Models;

namespace TwitchBot.Repositories
{
    public class QuoteRepository
    {
        private readonly string _connStr;
        private readonly string _twitchBotApiLink;

        public QuoteRepository(string connStr, string twitchBotApiLink)
        {
            _connStr = connStr;
            _twitchBotApiLink = twitchBotApiLink;
        }

        public async Task<List<Quote>> GetQuotes(int broadcasterId)
        {
            return await ApiBotRequest.GetExecuteTaskAsync<List<Quote>>(_twitchBotApiLink + $"quotes/get/{broadcasterId}");
        }

        public async Task AddQuote(string quote, string username, int broadcasterId)
        {
            Quote freshQuote = new Quote
            {
                Username = username,
                UserQuote = quote,
                Broadcaster = broadcasterId
            };

            await ApiBotRequest.PostExecuteTaskAsync(_twitchBotApiLink + $"quotes/create", freshQuote);
        }
    }
}
