using DigitalWorldOnline.Commons.Enums.Map;
using DigitalWorldOnline.Commons.Models.Servers;
using DigitalWorldOnline.Commons.Writers;

namespace DigitalWorldOnline.Commons.Packets.MapServer
{
    public class ServerExperiencePacket : PacketWriter
    {
        private const int PacketNumber = 1054;

        /// <summary>
        /// Load the server experience bonus.
        /// </summary>
        /// <param name="server">The experience information to load</param>
        public ServerExperiencePacket(ServerObject server, ServerExperienceEnum serverExperience)
        {
            //TODO: mecanica do bonus por login consecutivo
            Type(PacketNumber);
            WriteInt(1);
            WriteInt(server.Experience);
            WriteByte((byte)serverExperience);
            WriteInt(1);
            WriteInt(0);
            WriteInt(server.Experience);
        }
    }
}