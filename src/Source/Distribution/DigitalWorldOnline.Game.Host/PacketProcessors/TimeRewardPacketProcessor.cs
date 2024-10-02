using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models;
using DigitalWorldOnline.Commons.Packets.Items;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class TimeRewardPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.RewardStorage;

        private readonly ILogger _logger;
        private readonly ISender _sender;

        public TimeRewardPacketProcessor(
            ILogger logger,
            ISender sender)
        {
            _logger = logger;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            _logger.Information($"TimeReward Packet 16001");

            byte m_rewardIndex = packet.ReadByte();
            byte m_EventNo = packet.ReadByte();
            byte m_RemainTime = packet.ReadByte();
            byte m_TotalTime = packet.ReadByte();

            _logger.Information($"m_rewardIndex: {m_rewardIndex}");
            _logger.Information($"m_EventNo: {m_EventNo}");
            _logger.Information($"m_RemainTime: {m_RemainTime}");
            _logger.Information($"m_TotalTime: {m_TotalTime}");

            await _sender.Send(new UpdateItemsCommand(client.Tamer.GiftWarehouse));
            await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));

        }
    }
}