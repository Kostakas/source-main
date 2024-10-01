using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MediatR;
using System.Threading.Tasks;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.Items;
using MediatR;
using DigitalWorldOnline.Application;
using Serilog;
using Microsoft.Data.SqlClient;

namespace DigitalWorldOnline.Game.Models.Configuration
{
    public class TimeRewardService
    {
        private readonly AssetsLoader _assets;
        private readonly ILogger _logger;
        private readonly ISender _sender;


        public TimeRewardService(AssetsLoader assets,ILogger logger,ISender sender)
        {
            _assets = assets;
            _logger = logger;
            _sender = sender;
        }

        private void SaveTimeRewardState(GameClient client)
        {
            string connectionString = "Server=DESKTOP-M5N9KGG;Database=DSO;User Id=sa;Password=12345678;TrustServerCertificate=True";
            string query = @"
IF EXISTS (SELECT 1 FROM TimeReward WHERE TamerId = @TamerId)
BEGIN
    UPDATE TimeReward 
    SET RewardIndex = @RewardIndex, 
        CurrentTime = @CurrentTime, 
        LastTimeRewardUpdate = @LastTimeRewardUpdate 
    WHERE TamerId = @TamerId
END
ELSE
BEGIN
    INSERT INTO TimeReward (TamerId, RewardIndex, CurrentTime, LastTimeRewardUpdate)
    VALUES (@TamerId, @RewardIndex, @CurrentTime, @LastTimeRewardUpdate)
END";

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                SqlCommand command = new SqlCommand(query,connection);
                command.Parameters.AddWithValue("@RewardIndex",(int)client.Tamer.TimeReward.RewardIndex);
                command.Parameters.AddWithValue("@CurrentTime",client.Tamer.TimeReward.CurrentTime);
                command.Parameters.AddWithValue("@LastTimeRewardUpdate",client.Tamer.TimeReward.LastTimeRewardUpdate);
                command.Parameters.AddWithValue("@TamerId",client.TamerId);

                connection.Open();
                command.ExecuteNonQuery();
            }
        }

        // Método para cargar el estado de la recompensa por tiempo.
        public void LoadTimeRewardState(GameClient client)
        {
            string connectionString = "Server=DESKTOP-M5N9KGG;Database=DSO;User Id=sa;Password=12345678;TrustServerCertificate=True";
            string query = "SELECT RewardIndex, CurrentTime, LastTimeRewardUpdate FROM TimeReward WHERE TamerId = @TamerId";

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                SqlCommand command = new SqlCommand(query,connection);
                command.Parameters.AddWithValue("@TamerId",client.TamerId);

                connection.Open();
                SqlDataReader reader = command.ExecuteReader();
                if (reader.Read())
                {
                    client.Tamer.TimeReward.RewardIndex = (TimeRewardIndexEnum)reader.GetInt32(0);
                    client.Tamer.TimeReward.CurrentTime = reader.GetInt32(1);
                    client.Tamer.TimeReward.LastTimeRewardUpdate = reader.GetDateTime(2);
                }
                else
                {
                    client.Tamer.TimeReward.RewardIndex = TimeRewardIndexEnum.First;
                    client.Tamer.TimeReward.CurrentTime = 0;
                    client.Tamer.TimeReward.LastTimeRewardUpdate = DateTime.Now;

                    SaveTimeRewardState(client);
                }
            }
        }

        // Método para inicializar el cliente al conectarse.
        private void OnClientConnected(GameClient client)
        {
            LoadTimeRewardState(client);
        }

        // Método para verificar la recompensa por tiempo.
        public void CheckTimeReward(GameClient client)
        {
            if (client.Tamer.TimeReward.RewardIndex == TimeRewardIndexEnum.Ended)
            {
                if (DateTime.Now >= client.Tamer.TimeReward.LastTimeRewardUpdate.AddMinutes(1440))
                {
                    client.Tamer.TimeReward.RewardIndex = TimeRewardIndexEnum.First;
                    client.Tamer.TimeReward.CurrentTime = 0;
                    client.Tamer.TimeReward.LastTimeRewardUpdate = DateTime.Now;
                }
                else
                {
                    return;
                }
            }
            else
            {
                if (DateTime.Now >= client.Tamer.TimeReward.LastTimeRewardUpdate)
                {
                    client.Tamer.TimeReward.CurrentTime += 1;

                    if (client.Tamer.TimeReward.TimeCompleted())
                    {
                        if (client.Tamer.TimeReward.RewardIndex < TimeRewardIndexEnum.Fourth)
                        {
                            RedeemTimeReward(client);
                            client.Tamer.UpdateTimeReward();
                        }
                        else
                        {
                            RedeemTimeReward(client);
                            client.Tamer.TimeReward.RewardIndex = TimeRewardIndexEnum.Ended;
                        }
                    }
                    client.Send(new TimeRewardPacket(client.Tamer.TimeReward));
                }
                client.Tamer.TimeReward.SetLastTimeRewardDate();
            }

            SaveTimeRewardState(client);
        }

        // Método para redimir la recompensa por tiempo.
        private void RedeemTimeReward(GameClient client)
        {
            if (client.Tamer.TimeReward.RewardIndex == TimeRewardIndexEnum.Ended)
            {
                return;
            }

            void AddReward(ItemModel reward)
            {
                if (client.Tamer.GiftWarehouse.AddItem(reward))
                {
                    client.Send(new ReceiveItemPacket(reward,InventoryTypeEnum.GiftWarehouse));
                    _sender.Send(new UpdateItemsCommand(client.Tamer.GiftWarehouse));
                }
            }

            switch (client.Tamer.TimeReward.RewardIndex)
            {
                case TimeRewardIndexEnum.First:
                    {
                        var rewards = new[]
                        {
                    new ItemModel() { ItemId = 71120, Amount = 10 },
                    new ItemModel() { ItemId = 71121, Amount = 10 },
                    new ItemModel() { ItemId = 6001, Amount = 10 }
                };
                        foreach (var reward in rewards)
                        {
                            reward.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == reward.ItemId));
                            AddReward(reward);
                        }
                    }
                    break;
                case TimeRewardIndexEnum.Second:
                    {
                        var rewards = new[]
                        {
                    new ItemModel() { ItemId = 9202, Amount = 10 },
                    new ItemModel() { ItemId = 41002, Amount = 10 },
                    new ItemModel() { ItemId = 9768, Amount = 10 }
                };
                        foreach (var reward in rewards)
                        {
                            reward.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == reward.ItemId));
                            AddReward(reward);
                        }
                    }
                    break;
                case TimeRewardIndexEnum.Third:
                    {
                        var rewards = new[]
                        {
                    new ItemModel() { ItemId = 75404, Amount = 2 },
                    new ItemModel() { ItemId = 5735, Amount = 3 },
                    new ItemModel() { ItemId = 5888, Amount = 2 }
                };
                        foreach (var reward in rewards)
                        {
                            reward.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == reward.ItemId));
                            AddReward(reward);
                        }
                    }
                    break;
                case TimeRewardIndexEnum.Fourth:
                    {
                        var rewards = new[]
                        {
                    new ItemModel() { ItemId = 128589, Amount = 1 },
                    new ItemModel() { ItemId = 10243, Amount = 1 },
                    new ItemModel() { ItemId = 79049, Amount = 1 }
                };
                        foreach (var reward in rewards)
                        {
                            reward.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == reward.ItemId));
                            AddReward(reward);
                        }
                    }
                    break;
                default:
                    {
                        _logger.Information($"No Reward Defined !!");
                    }
                    break;
            }
        }

    }
}
