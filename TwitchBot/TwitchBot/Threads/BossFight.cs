﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using TwitchBot.Configuration;
using TwitchBot.Libraries;
using TwitchBot.Models;
using TwitchBot.Services;

namespace TwitchBot.Threads
{
    public class BossFight
    {
        private IrcClient _irc;
        private string _connStr;
        private int _broadcasterId;
        private Thread _thread;
        private BankService _bank;
        private TwitchBotConfigurationSection _botConfig;
        private string _resultMessage;
        private BossFightSettings _bossSettings = BossFightSettings.Instance;

        public BossFight() { }

        public BossFight(string connStr, BankService bank, TwitchBotConfigurationSection botConfig)
        {
            _connStr = connStr;
            _thread = new Thread(new ThreadStart(this.Run));
            _bank = bank;
            _botConfig = botConfig;
        }

        public void Start(IrcClient irc, int broadcasterId)
        {
            _irc = irc;
            _broadcasterId = broadcasterId;
            _bossSettings.CooldownTimePeriod = DateTime.Now;
            _bossSettings.Fighters = new BlockingCollection<BossFighter>();
            _resultMessage = _bossSettings.ResultsMessage;

            _thread.IsBackground = true;
            _thread.Start();
        }

        private void Run()
        {
            while (true)
            {
                if (_bossSettings.IsBossFightOnCooldown())
                {
                    double cooldownTime = (_bossSettings.CooldownTimePeriod.Subtract(DateTime.Now)).TotalMilliseconds;
                    Thread.Sleep((int)cooldownTime);
                    _irc.SendPublicChatMessage(_bossSettings.CooldownOver);
                }
                else if (_bossSettings.Fighters.Count > 0 && _bossSettings.IsEntryPeriodOver())
                {
                    _bossSettings.Fighters.CompleteAdding();
                    Consume();

                    // refresh the list and reset the cooldown time period
                    _bossSettings.Fighters = new BlockingCollection<BossFighter>();
                    _bossSettings.CooldownTimePeriod = DateTime.Now.AddMinutes(_bossSettings.CooldownTimePeriodMinutes);
                    _resultMessage = _bossSettings.ResultsMessage;
                }

                Thread.Sleep(200);
            }
        }

        public void Produce(BossFighter fighter)
        {
            _bossSettings.Fighters.Add(fighter);
        }

        public void Consume()
        {
            Boss boss = _bossSettings.Bosses[BossLevel() - 1];

            _irc.SendPublicChatMessage(_bossSettings.GameStart
                .Replace("@bossname@", boss.Name));

            Thread.Sleep(5000); // wait in anticipation

            // Raid the boss
            bool isBossAlive = true;
            string lastAttackFighter = "";

            for (int turn = 0; turn < boss.TurnLimit; turn++)
            {
                foreach (BossFighter fighter in _bossSettings.Fighters)
                {
                    if (fighter.FighterClass.Health <= 0)
                        continue;

                    if (fighter.FighterClass.Attack > boss.Defense)
                        boss.Health = fighter.FighterClass.Attack - boss.Defense;

                    if (boss.Health <= 0)
                    {
                        lastAttackFighter = fighter.Username;
                        isBossAlive = false;
                        break;
                    }

                    if (boss.Attack > fighter.FighterClass.Defense)
                    {
                        Random rnd = new Random(DateTime.Now.Millisecond);
                        int chance = rnd.Next(1, 101); // 1 - 100

                        // check if fighter dodged the attack
                        if (chance <= fighter.FighterClass.Evasion)
                            continue;

                        fighter.FighterClass.Health = boss.Attack - fighter.FighterClass.Defense;
                    }
                }

                if (!isBossAlive) break;
            }

            // Evaluate the fight
            if (isBossAlive)
            {
                if (_bossSettings.Fighters.Count == 1)
                {
                    _irc.SendPublicChatMessage(_bossSettings.SingleUserFail
                        .Replace("user@", _bossSettings.Fighters.First().Username)
                        .Replace("@bossname@", boss.Name));
                }
                else
                {
                    _irc.SendPublicChatMessage(_bossSettings.Success0);
                }

                return;
            }

            // Calculate surviving raid party earnings
            IEnumerable<BossFighter> survivors = _bossSettings.Fighters.Where(f => f.FighterClass.Health > 0);
            int numSurvivors = survivors.Count();
            foreach (BossFighter champion in survivors)
            {
                int funds = _bank.CheckBalance(champion.Username.ToLower(), _broadcasterId);

                decimal earnings = Math.Ceiling(boss.Loot / (decimal)numSurvivors);

                // give last attack bonus to specified fighter
                if (champion.Equals(lastAttackFighter)) 
                    earnings += boss.LastAttackBonus;

                _bank.UpdateFunds(champion.Username.ToLower(), _broadcasterId, (int)earnings + funds);

                _resultMessage += $" {champion.Username} ({(int)earnings} {_botConfig.CurrencyType}),";
            }

            // remove extra ","
            _resultMessage = _resultMessage.Remove(_resultMessage.LastIndexOf(','), 1);

            decimal survivorsPercentage = numSurvivors / (decimal)_bossSettings.Fighters.Count;

            // Display success outcome
            if (numSurvivors == 1)
            {
                BossFighter onlyWinner = _bossSettings.Fighters.First();
                int earnings = boss.Loot;

                _irc.SendPublicChatMessage(_bossSettings.SingleUserSuccess
                    .Replace("user@", onlyWinner.Username)
                    .Replace("@bossname@", boss.Name)
                    .Replace("@winamount@", earnings.ToString())
                    .Replace("@pointsname@", _botConfig.CurrencyType));
            }
            else if (survivorsPercentage == 1.0m)
            {
                _irc.SendPublicChatMessage(_bossSettings.Success100 + " " + _resultMessage);
            }
            else if (survivorsPercentage >= 0.34m)
            {
                _irc.SendPublicChatMessage(_bossSettings.Success34 + " " + _resultMessage);
            }
            else if (survivorsPercentage > 0)
            {
                _irc.SendPublicChatMessage(_bossSettings.Success1 + " " + _resultMessage);
            }

            // show in case Twitch deletes the message because of exceeding character length
            Console.WriteLine("\n" + _resultMessage + "\n");
        }

        public bool HasFighterAlreadyEntered(string username)
        {
            return _bossSettings.Fighters.Any(u => u.Username == username) ? true : false;
        }

        public bool IsEntryPeriodOver()
        {
            return _bossSettings.Fighters.IsAddingCompleted ? true : false;
        }

        public int BossLevel()
        {
            if (_bossSettings.Fighters.Count <= _bossSettings.Bosses[0].MaxUsers)
                return 1;
            else if (_bossSettings.Fighters.Count <= _bossSettings.Bosses[1].MaxUsers)
                return 2;
            else if (_bossSettings.Fighters.Count <= _bossSettings.Bosses[2].MaxUsers)
                return 3;
            else if (_bossSettings.Fighters.Count <= _bossSettings.Bosses[3].MaxUsers)
                return 4;
            else
                return 5;
        }

        public string NextLevelMessage()
        {
            if (_bossSettings.Fighters.Count == _bossSettings.Bosses[0].MaxUsers + 1)
                return _bossSettings.NextLevelMessages[0];
            else if (_bossSettings.Fighters.Count == _bossSettings.Bosses[1].MaxUsers + 1)
                return _bossSettings.NextLevelMessages[1];
            else if (_bossSettings.Fighters.Count == _bossSettings.Bosses[2].MaxUsers + 1)
                return _bossSettings.NextLevelMessages[2];
            else if (_bossSettings.Fighters.Count == _bossSettings.Bosses[3].MaxUsers + 1)
                return _bossSettings.NextLevelMessages[3];

            return "";
        }
    }
}