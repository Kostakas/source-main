using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Admin.Queries;
using DigitalWorldOnline.Application.Separar.Commands.Create;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.Account;
using DigitalWorldOnline.Commons.Enums.Character;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.Digimon;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.GameServer.Combat;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Packets.MapServer;
using DigitalWorldOnline.Commons.Utils;
using DigitalWorldOnline.Commons.Writers;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using MediatR;
using Microsoft.Extensions.Configuration;
using Serilog;
using System.Text.RegularExpressions;

namespace DigitalWorldOnline.Game
{
    public sealed class PlayerCommandsProcessor : IDisposable
    {
        private const string GameServerAddress = "GameServer:Address";
        private const string GamerServerPublic = "GameServer:PublicAddress";
        private const string GameServerPort = "GameServer:Port";

        private readonly PartyManager _partyManager;
        private readonly StatusManager _statusManager;
        private readonly ExpManager _expManager;
        private readonly AssetsLoader _assets;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly PvpServer _pvpServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;
        private readonly IConfiguration _configuration;

        public PlayerCommandsProcessor(
            PartyManager partyManager,
            StatusManager statusManager,
            ExpManager expManager,
            AssetsLoader assets,
            MapServer mapServer,
            DungeonsServer dungeonsServer,
            PvpServer pvpServer,
            ILogger logger,
            ISender sender,
            IConfiguration configuration)
        {
            _partyManager = partyManager;
            _expManager = expManager;
            _statusManager = statusManager;
            _assets = assets;
            _mapServer = mapServer;
            _dungeonServer = dungeonsServer;
            _pvpServer = pvpServer;
            _logger = logger;
            _sender = sender;
            _configuration = configuration;
        }

        public async Task ExecuteCommand(GameClient client, string message)
        {
            var command = Regex.Replace(message.Trim().ToLower(), @"\s+", " ").Split(' ');
            _logger.Information($"Account {client.AccountId} {client.Tamer.Name} used !{message}.");

            switch (command[0])
            {

                case "pvp":
                    {
                        var regex = @"(pvp\son){1}|(pvp\soff){1}";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Voce deve digitar !pvp on/off"));
                            break;
                        }
                        if (client.Tamer.InBattle)
                        {
                            client.Send(new SystemMessagePacket($"Voce nao pode desativar o PVP em Batalha!"));
                            break;
                        }

                        switch (command[1])
                        {
                            case "on":
                                {
                                    if (client.Tamer.PvpMap == false)
                                    {
                                        client.Tamer.PvpMap = true;
                                        client.Send(new NoticeMessagePacket($"PVP do seu Personagem foi ativado!"));
                                    }
                                    else client.Send(new NoticeMessagePacket($"Seu PVP ja esta ativado!"));
                                }
                                break;

                            case "off":
                                {
                                    if (client.Tamer.PvpMap == true)
                                    {
                                        client.Tamer.PvpMap = false;
                                        client.Send(new NoticeMessagePacket($"PVP do seu Personagem foi desativado!"));
                                    }
                                    else client.Send(new NoticeMessagePacket($"Seu PVP ja esta desativado!"));
                                }
                                break;
                        }
                    }
                    break;

                default:
                    client.Send(new SystemMessagePacket($"Comando invalido! Verifique e tente novamente."));
                    break;
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
