using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Create;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Digimon;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class HatchFinishPacketProcessor :IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.HatchFinish;

        private readonly StatusManager _statusManager;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly AssetsLoader _assets;
        private readonly ILogger _logger;
        private readonly ISender _sender;

        public HatchFinishPacketProcessor(
            StatusManager statusManager,
            MapServer mapServer,
            AssetsLoader assets,
            ILogger logger,
            ISender sender,
            DungeonsServer dungeonsServer
        )
        {
            _statusManager = statusManager;
            _mapServer = mapServer;
            _assets = assets;
            _logger = logger;
            _sender = sender;
            _dungeonServer = dungeonsServer;
        }

        public async Task Process(GameClient client,byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            packet.Skip(5);
            var digiName = packet.ReadString();

            var hatchInfo = _assets.Hatchs.FirstOrDefault(x => x.ItemId == client.Tamer.Incubator.EggId);
            if (hatchInfo == null)
            {
                _logger.Warning($"Unknown hatch info for egg {client.Tamer.Incubator.EggId}.");
                client.Send(new SystemMessagePacket($"Unknown hatch info for egg {client.Tamer.Incubator.EggId}."));
                return;
            }

            // Find an empty slot
            var slotIndex = FindEmptySlot(client);
            if (slotIndex == -1 || slotIndex > byte.MaxValue)
            {
                _logger.Warning("No valid empty slot available for new Digimon.");
                client.Send(new SystemMessagePacket("No valid empty slot available for the new Digimon."));
                return;
            }

            var newDigimon = DigimonModel.Create(
                digiName,
                hatchInfo.HatchType,
                hatchInfo.HatchType,
                (DigimonHatchGradeEnum)client.Tamer.Incubator.HatchLevel,
                client.Tamer.Incubator.GetLevelSize(),
                (byte)slotIndex // Cast to byte
            );

            newDigimon.NewLocation(
                client.Tamer.Location.MapId,
                client.Tamer.Location.X,
                client.Tamer.Location.Y
            );

            newDigimon.SetBaseInfo(
                _statusManager.GetDigimonBaseInfo(newDigimon.BaseType)
            );

            newDigimon.SetBaseStatus(
                _statusManager.GetDigimonBaseStatus(newDigimon.BaseType,newDigimon.Level,newDigimon.Size)
            );

            newDigimon.AddEvolutions(
                _assets.EvolutionInfo.First(x => x.Type == newDigimon.BaseType)
            );

            if (newDigimon.BaseInfo == null || newDigimon.BaseStatus == null || !newDigimon.Evolutions.Any())
            {
                _logger.Warning($"Unknown digimon info for {newDigimon.BaseType}.");
                client.Send(new SystemMessagePacket($"Unknown digimon info for {newDigimon.BaseType}."));
                return;
            }

            newDigimon.SetTamer(client.Tamer);

            client.Tamer.AddDigimon(newDigimon);

            client.Send(new HatchFinishPacket(newDigimon,(ushort)(client.Partner.GeneralHandler + 1000),(byte)slotIndex));

            if (client.Tamer.Incubator.PerfectSize(newDigimon.HatchGrade,newDigimon.Size))
            {
                var neonMessagePacket = new NeonMessagePacket(NeonMessageTypeEnum.Scale,client.Tamer.Name,newDigimon.BaseType,newDigimon.Size).Serialize();
                _mapServer.BroadcastGlobal(neonMessagePacket);
                _dungeonServer.BroadcastGlobal(neonMessagePacket);
            }

            var digimonInfo = await _sender.Send(new CreateDigimonCommand(newDigimon));

            if (digimonInfo != null)
            {
                newDigimon.SetId(digimonInfo.Id);
                var slot = -1;

                foreach (var digimon in newDigimon.Evolutions)
                {
                    slot++;
                    var evolution = digimonInfo.Evolutions[slot];

                    if (evolution != null)
                    {
                        digimon.SetId(evolution.Id);
                        var skillSlot = -1;

                        foreach (var skill in digimon.Skills)
                        {
                            skillSlot++;
                            var dtoSkill = evolution.Skills[skillSlot];
                            skill.SetId(dtoSkill.Id);
                        }
                    }
                }
            }

            _logger.Verbose($"Character {client.TamerId} hatched {newDigimon.Id}({newDigimon.BaseType}) with grade {newDigimon.HatchGrade} and size {newDigimon.Size}.");

            client.Tamer.Incubator.RemoveEgg();

            await _sender.Send(new UpdateIncubatorCommand(client.Tamer.Incubator));
        }

        private byte FindEmptySlot(GameClient client)
        {
            for (byte i = 0;i < client.Tamer.DigimonSlots;i++)
            {
                if (client.Tamer.Digimons.FirstOrDefault(x => x.Slot == i) == null)
                {
                    return i;
                }
            }
            return byte.MaxValue; // No empty slot found
        }
    }
}
