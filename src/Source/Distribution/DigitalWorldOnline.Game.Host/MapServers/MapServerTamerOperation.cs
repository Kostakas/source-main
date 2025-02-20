﻿using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.Character;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Models.Config;
using DigitalWorldOnline.Commons.Models.Digimon;
using DigitalWorldOnline.Commons.Models.Map;
using DigitalWorldOnline.Commons.Models.Summon;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.GameServer.Combat;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Packets.MapServer;
using DigitalWorldOnline.Commons.Utils;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.Game.Models.Configuration;
using Microsoft.Extensions.Configuration;
using System;
using System.Diagnostics;
using System.Text;

namespace DigitalWorldOnline.GameHost
{
    public sealed partial class MapServer
    {
        public void TamerOperation(GameMap map)
        {
            if (!map.ConnectedTamers.Any())
            {
                map.SetNoTamers();
                return;
            }

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            foreach (var tamer in map.ConnectedTamers)
            {
                var client = map.Clients.FirstOrDefault(x => x.TamerId == tamer.Id);

                if (client == null || !client.IsConnected || client.Partner == null)
                    continue;

                GetInViewMobs(map, tamer);
                GetInViewMobs(map, tamer, true);

                ShowOrHideTamer(map, tamer);

                ShowOrHideConsignedShop(map, tamer);
                var packet = new UpdateStatusPacket(client.Tamer);
                client.Send(packet);
                PartnerAutoAttack(tamer, client);

                _timeRewardService.LoadTimeRewardState(client);
                _timeRewardService.CheckTimeReward(client);



                CheckMonthlyReward(client);

                CheckEvolution(client);

                tamer.AutoRegen();
                tamer.ActiveEvolutionReduction();

                if (tamer.BreakEvolution)
                {
                    tamer.ActiveEvolution.SetDs(0);
                    tamer.ActiveEvolution.SetXg(0);

                    if (tamer.Riding)
                    {
                        tamer.StopRideMode();

                        BroadcastForTamerViewsAndSelf(tamer.Id,
                            new UpdateMovementSpeedPacket(tamer).Serialize());

                        BroadcastForTamerViewsAndSelf(tamer.Id,
                            new RideModeStopPacket(tamer.GeneralHandler, tamer.Partner.GeneralHandler).Serialize());
                    }

                    var buffToRemove = client.Tamer.Partner.BuffList.TamerBaseSkill();
                    if (buffToRemove != null)
                    {
                        BroadcastForTamerViewsAndSelf(client.TamerId, new RemoveBuffPacket(client.Partner.GeneralHandler, buffToRemove.BuffId).Serialize());
                    }

                    client.Tamer.RemovePartnerPassiveBuff();

                    map.BroadcastForTamerViewsAndSelf(tamer.Id,
                        new DigimonEvolutionSucessPacket(tamer.GeneralHandler,
                            tamer.Partner.GeneralHandler,
                            tamer.Partner.BaseType,
                            DigimonEvolutionEffectEnum.Back).Serialize());

                    var currentHp = client.Partner.CurrentHp;
                    var currentMaxHp = client.Partner.HP;
                    var currentDs = client.Partner.CurrentDs;
                    var currentMaxDs = client.Partner.DS;

                    tamer.Partner.UpdateCurrentType(tamer.Partner.BaseType);

                    tamer.Partner.SetBaseInfo(
                        _statusManager.GetDigimonBaseInfo(
                            tamer.Partner.CurrentType
                        )
                    );

                    tamer.Partner.SetBaseStatus(
                        _statusManager.GetDigimonBaseStatus(
                            tamer.Partner.CurrentType,
                            tamer.Partner.Level,
                            tamer.Partner.Size
                        )
                    );

                    client.Tamer.SetPartnerPassiveBuff();

                    client.Partner.AdjustHpAndDs(currentHp, currentMaxHp, currentDs, currentMaxDs);

                    foreach (var buff in client.Tamer.Partner.BuffList.ActiveBuffs)
                        buff.SetBuffInfo(_assets.BuffInfo.FirstOrDefault(x => x.SkillCode == buff.SkillId && buff.BuffInfo == null || x.DigimonSkillCode == buff.SkillId && buff.BuffInfo == null));

                    client.Send(new UpdateStatusPacket(tamer));

                    if (client.Tamer.Partner.BuffList.TamerBaseSkill() != null)
                    {
                        var buffToApply = client.Tamer.Partner.BuffList.Buffs
                                    .Where(x => x.Duration == 0)
                                    .ToList();

                        buffToApply.ForEach(buffToApply =>
                        {

                            BroadcastForTamerViewsAndSelf(client.Tamer.Id, new AddBuffPacket(client.Tamer.Partner.GeneralHandler, buffToApply.BuffId, buffToApply.SkillId, (short)buffToApply.TypeN, 0).Serialize());
                        });

                    }

                    var party = _partyManager.FindParty(client.TamerId);
                    if (party != null)
                    {
                        party.UpdateMember(party[client.TamerId]);

                        BroadcastForTargetTamers(party.GetMembersIdList(),
                            new PartyMemberInfoPacket(party[client.TamerId]).Serialize());
                    }

                    _sender.Send(new UpdatePartnerCurrentTypeCommand(client.Partner));
                    _sender.Send(new UpdateCharacterActiveEvolutionCommand(tamer.ActiveEvolution));
                    _sender.Send(new UpdateDigimonBuffListCommand(client.Partner.BuffList));
                }

         
                if (tamer.CheckExpiredItemsTime)
                {
                    tamer.SetLastExpiredItemsCheck();

                    foreach (var item in tamer.Inventory.EquippedItems)
                    {
                        if (item.ItemInfo != null && item.IsTemporary && item.Expired)
                        {
                           
                            if (item.ItemInfo.UseTimeType == 1 || item.ItemInfo.UseTimeType == 2 || item.ItemInfo.UseTimeType == 3 )
                            {
                                item.SetFirstExpired(false);

                                client.Send(new ItemExpiredPacket(InventorySlotTypeEnum.TabInven, item.Slot, item.ItemId, ExpiredTypeEnum.Remove));
                                tamer.Inventory.RemoveOrReduceItem(item, item.Amount);

                            }
                            else
                            {

                                client.Send(new ItemExpiredPacket(InventorySlotTypeEnum.TabInven, item.Slot, item.ItemId, ExpiredTypeEnum.Remove));
                                tamer.Inventory.RemoveOrReduceItem(item, item.Amount);
                            }
                        }
                    }

                    foreach (var item in tamer.Warehouse.EquippedItems)
                    {
                        if (item.ItemInfo != null && item.IsTemporary && item.Expired)
                        {

                            if (item.ItemInfo.UseTimeType == 1 || item.ItemInfo.UseTimeType == 2 || item.ItemInfo.UseTimeType == 3)
                            {
                                item.SetFirstExpired(false);

                                client.Send(new ItemExpiredPacket(InventorySlotTypeEnum.TabWarehouse, item.Slot, item.ItemId, ExpiredTypeEnum.Remove));
                                tamer.Inventory.RemoveOrReduceItem(item, item.Amount);

                            }
                            else
                            {

                                client.Send(new ItemExpiredPacket(InventorySlotTypeEnum.TabWarehouse, item.Slot, item.ItemId, ExpiredTypeEnum.Remove));
                                tamer.Inventory.RemoveOrReduceItem(item, item.Amount);
                            }
                        }
                    }

                    foreach (var item in tamer.AccountWarehouse.EquippedItems)
                    {
                        if (item.ItemInfo != null && item.IsTemporary && item.Expired)
                        {

                            if (item.ItemInfo.UseTimeType == 1 || item.ItemInfo.UseTimeType == 2 || item.ItemInfo.UseTimeType == 3)
                            {
                                item.SetFirstExpired(false);

                                client.Send(new ItemExpiredPacket(InventorySlotTypeEnum.TabShareStash, item.Slot, item.ItemId, ExpiredTypeEnum.Remove));
                                tamer.Inventory.RemoveOrReduceItem(item, item.Amount);
                            }
                            else
                            {

                                client.Send(new ItemExpiredPacket(InventorySlotTypeEnum.TabShareStash, item.Slot, item.ItemId, ExpiredTypeEnum.Remove));
                                tamer.Inventory.RemoveOrReduceItem(item, item.Amount);
                            }
                        }
                    }

                    foreach (var item in tamer.Equipment.EquippedItems)
                    {
                        if (item.ItemInfo != null && item.IsTemporary && item.Expired)
                        {
                           
                            if (item.ItemInfo.UseTimeType == 1 || item.ItemInfo.UseTimeType == 2)
                            {
                                item.SetFirstExpired(false);

                                client.Send(new ItemExpiredPacket(InventorySlotTypeEnum.TabEquip, item.Slot, item.ItemId, ExpiredTypeEnum.Remove));
                                tamer.Equipment.RemoveOrReduceItem(item, item.Amount);
                            }
                            else
                            {

                                client.Send(new ItemExpiredPacket(InventorySlotTypeEnum.TabEquip, item.Slot, item.ItemId, ExpiredTypeEnum.Remove));
                                tamer.Equipment.RemoveOrReduceItem(item, item.Amount);
                            }
                        }
                    }

                    foreach (var item in tamer.ChipSets.EquippedItems)
                    {
                        if (item.ItemInfo != null && item.IsTemporary && item.Expired)
                        {

                            if (item.ItemInfo.UseTimeType == 1 || item.ItemInfo.UseTimeType == 2 || item.ItemInfo.UseTimeType == 3)
                            {
                                item.SetFirstExpired(false);

                                client.Send(new ItemExpiredPacket(InventorySlotTypeEnum.TabChipset, item.Slot, item.ItemId, ExpiredTypeEnum.Remove));
                                tamer.Equipment.RemoveOrReduceItem(item,item.Amount);
                            }
                            else
                            {

                                client.Send(new ItemExpiredPacket(InventorySlotTypeEnum.TabChipset, item.Slot, item.ItemId, ExpiredTypeEnum.Remove));
                                tamer.Equipment.RemoveOrReduceItem(item, item.Amount);
                            }
                        }
                    }

                   

                    _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                    _sender.Send(new UpdateItemsCommand(client.Tamer.Warehouse));
                    _sender.Send(new UpdateItemsCommand(client.Tamer.AccountWarehouse));
                    _sender.Send(new UpdateItemsCommand(client.Tamer.Equipment));
                    _sender.Send(new UpdateItemsCommand(client.Tamer.ChipSets));
                }

                if (tamer.CheckBuffsTime)
                {
                    tamer.UpdateBuffsCheckTime();

                    if (tamer.BuffList.HasActiveBuffs)
                    {
                        var buffsToRemove = tamer.BuffList.Buffs
                            .Where(x => x.Expired)
                            .ToList();

                        buffsToRemove.ForEach(buffToRemove =>
                        {
                            tamer.BuffList.Remove(buffToRemove.BuffId);
                            map.BroadcastForTamerViewsAndSelf(tamer.Id, new RemoveBuffPacket(tamer.GeneralHandler, buffToRemove.BuffId).Serialize());
                        });

                        if (buffsToRemove.Any())
                        {

                            client?.Send(new UpdateStatusPacket(tamer));
                            map.BroadcastForTargetTamers(tamer.Id, new UpdateCurrentHPRatePacket(tamer.GeneralHandler, tamer.HpRate).Serialize());
                            _sender.Send(new UpdateCharacterBuffListCommand(tamer.BuffList));

                        }
                    }

                    if (tamer.Partner.BuffList.HasActiveBuffs)
                    {
                        var buffsToRemove = tamer.Partner.BuffList.Buffs
                            .Where(x => x.Expired)
                            .ToList();



                        buffsToRemove.ForEach(buffToRemove =>
                        {
                            tamer.Partner.BuffList.Remove(buffToRemove.BuffId);
                            map.BroadcastForTamerViewsAndSelf(tamer.Id, new RemoveBuffPacket(tamer.Partner.GeneralHandler, buffToRemove.BuffId).Serialize());
                        });

                        if (buffsToRemove.Any())
                        {
                            client?.Send(new UpdateStatusPacket(tamer));
                            map.BroadcastForTargetTamers(tamer.Id, new UpdateCurrentHPRatePacket(tamer.Partner.GeneralHandler, tamer.Partner.HpRate).Serialize());
                            _sender.Send(new UpdateDigimonBuffListCommand(tamer.Partner.BuffList));
                        }
                    }

                    if (tamer.HaveActiveCashSkill)
                    {
                        var buffsToRemove = tamer.ActiveSkill
                            .Where(x => x.Expired && x.SkillId > 0 && x.Type == TamerSkillTypeEnum.Cash)
                            .ToList();

                        buffsToRemove.ForEach(buffToRemove =>
                        {
                            var activeSkill = tamer.ActiveSkill.FirstOrDefault(x => x.Id == buffToRemove.Id);

                            activeSkill.SetTamerSkill(0, 0, TamerSkillTypeEnum.Normal);

                            client?.Send(new ActiveTamerCashSkillExpire(activeSkill.SkillId));

                            _sender.Send(new UpdateTamerSkillCooldownByIdCommand(activeSkill));
                        });


                    }
                }


                if (tamer.SyncResourcesTime)
                {
                    tamer.UpdateSyncResourcesTime();

                    client?.Send(new UpdateCurrentResourcesPacket(tamer.GeneralHandler, (short)tamer.CurrentHp, (short)tamer.CurrentDs, 0));
                    client?.Send(new UpdateCurrentResourcesPacket(tamer.Partner.GeneralHandler, (short)tamer.Partner.CurrentHp, (short)tamer.Partner.CurrentDs, 0));
                    client?.Send(new TamerXaiResourcesPacket(client.Tamer.XGauge, client.Tamer.XCrystals));

                    map.BroadcastForTargetTamers(tamer.Id, new UpdateCurrentHPRatePacket(tamer.GeneralHandler, tamer.HpRate).Serialize());
                    map.BroadcastForTargetTamers(tamer.Id, new UpdateCurrentHPRatePacket(tamer.Partner.GeneralHandler, tamer.Partner.HpRate).Serialize());
                    map.BroadcastForTamerViewsAndSelf(tamer.Id, new SyncConditionPacket(tamer.GeneralHandler, tamer.CurrentCondition, tamer.ShopName).Serialize());

                    var party = _partyManager.FindParty(tamer.Id);
                    if (party != null)
                    {
                        party.UpdateMember(party[tamer.Id]);

                        map.BroadcastForTargetTamers(party.GetMembersIdList(),
                            new PartyMemberInfoPacket(party[tamer.Id]).Serialize());
                    }
                }

                if (tamer.SaveResourcesTime)
                {
                    tamer.UpdateSaveResourcesTime();

                    var subStopWatch = new Stopwatch();
                    subStopWatch.Start();


                    _sender.Send(new UpdateCharacterBasicInfoCommand(tamer));

                    _sender.Send(new UpdateEvolutionCommand(tamer.Partner.CurrentEvolution));
               
                    subStopWatch.Stop();

                    if (subStopWatch.ElapsedMilliseconds >= 1500)
                    {
                        Console.WriteLine($"Save resources elapsed time: {subStopWatch.ElapsedMilliseconds}");
                    }
                }

                //if (tamer.ResetDailyQuestsTime)
                //{
                //    tamer.UpdateDailyQuestsSyncTime();

                //    var dailyQuestResetTime = _sender.Send(new DailyQuestResetTimeQuery());

                //    if (DateTime.Now >= dailyQuestResetTime.Result)
                //    {
                //        client?.Send(new QuestDailyUpdatePacket());
                //    }
                //}
            }

            stopwatch.Stop();

            var totalTime = stopwatch.Elapsed.TotalMilliseconds;

            if (totalTime >= 1000)
                Console.WriteLine($"TamersOperation ({map.ConnectedTamers.Count}): {totalTime}.");
        }

        private void GetInViewMobs(GameMap map, CharacterModel tamer)
        {
            List<long> mobsToAdd = new List<long>();
            List<long> mobsToRemove = new List<long>();

            // Criar uma cópia da lista de Mobs
            List<MobConfigModel> mobsCopy = new List<MobConfigModel>(map.Mobs);

            // Iterar sobre a cópia da lista
            mobsCopy.ForEach(mob =>
            {
                if (tamer.TempShowFullMap)
                {
                    if (!tamer.MobsInView.Contains(mob.Id))
                        mobsToAdd.Add(mob.Id);
                }
                else
                {
                    var distanceDifference = UtilitiesFunctions.CalculateDistance(
                        tamer.Location.X,
                        mob.CurrentLocation.X,
                        tamer.Location.Y,
                        mob.CurrentLocation.Y);

                    if (distanceDifference <= _startToSee && !tamer.MobsInView.Contains(mob.Id))
                        mobsToAdd.Add(mob.Id);

                    if (distanceDifference >= _stopSeeing && tamer.MobsInView.Contains(mob.Id))
                        mobsToRemove.Add(mob.Id);
                }
            });

            // Adicionar e remover os IDs de Mob na lista tamer.MobsInView após a iteração
            mobsToAdd.ForEach(id => tamer.MobsInView.Add(id));
            mobsToRemove.ForEach(id => tamer.MobsInView.Remove(id));
        }

        private void GetInViewMobs(GameMap map, CharacterModel tamer, bool Summon)
        {
            List<long> mobsToAdd = new List<long>();
            List<long> mobsToRemove = new List<long>();

            // Criar uma cópia da lista de Mobs
            List<SummonMobModel> mobsCopy = new List<SummonMobModel>(map.SummonMobs);

            // Iterar sobre a cópia da lista
            mobsCopy.ForEach(mob =>
            {
                if (tamer.TempShowFullMap)
                {
                    if (!tamer.MobsInView.Contains(mob.Id))
                        mobsToAdd.Add(mob.Id);
                }
                else
                {
                    var distanceDifference = UtilitiesFunctions.CalculateDistance(
                        tamer.Location.X,
                        mob.CurrentLocation.X,
                        tamer.Location.Y,
                        mob.CurrentLocation.Y);

                    if (distanceDifference <= _startToSee && !tamer.MobsInView.Contains(mob.Id))
                        mobsToAdd.Add(mob.Id);

                    if (distanceDifference >= _stopSeeing && tamer.MobsInView.Contains(mob.Id))
                        mobsToRemove.Add(mob.Id);
                }
            });

            // Adicionar e remover os IDs de Mob na lista tamer.MobsInView após a iteração
            mobsToAdd.ForEach(id => tamer.MobsInView.Add(id));
            mobsToRemove.ForEach(id => tamer.MobsInView.Remove(id));
        }
        /// <summary>
        /// Updates the current partners handler values;
        /// </summary>
        /// <param name="mapId">Current map id</param>
        /// <param name="digimons">Current digimons</param>
        public void SetDigimonHandlers(int mapId, List<DigimonModel> digimons)
        {
            Maps.FirstOrDefault(x => x.MapId == mapId)?.SetDigimonHandlers(digimons);
        }

        /// <summary>
        /// Swaps the digimons current handler.
        /// </summary>
        /// <param name="mapId">Target map handler manager</param>
        /// <param name="oldPartnerId">Old partner identifier</param>
        /// <param name="newPartner">New partner</param>
        public void SwapDigimonHandlers(int mapId, DigimonModel oldPartner, DigimonModel newPartner)
        {
            Maps.FirstOrDefault(x => x.MapId == mapId)?.SwapDigimonHandlers(oldPartner, newPartner);
        }

        private void ShowOrHideTamer(GameMap map, CharacterModel tamer)
        {
            foreach (var connectedTamer in map.ConnectedTamers.Where(x => x.Id != tamer.Id))
            {
                var distanceDifference = UtilitiesFunctions.CalculateDistance(
                    tamer.Location.X,
                    connectedTamer.Location.X,
                    tamer.Location.Y,
                    connectedTamer.Location.Y);

                if (distanceDifference <= _startToSee)
                    ShowTamer(map, tamer, connectedTamer.Id);
                else if (distanceDifference >= _stopSeeing)
                    HideTamer(map, tamer, connectedTamer.Id);
            }
        }

        private void ShowTamer(GameMap map, CharacterModel tamerToShow, long tamerToSeeId)
        {
            if (!map.ViewingTamer(tamerToShow.Id, tamerToSeeId))
            {
                foreach (var item in tamerToShow.Equipment.EquippedItems.Where(x => x.ItemInfo == null))
                    item?.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == item?.ItemId));

                map.ShowTamer(tamerToShow.Id, tamerToSeeId);

                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == tamerToSeeId);
                if (targetClient != null)
                {
                    targetClient.Send(new LoadTamerPacket(tamerToShow));
                    targetClient.Send(new LoadBuffsPacket(tamerToShow));
                    if (tamerToShow.InBattle)
                    {
                        targetClient.Send(new SetCombatOnPacket(tamerToShow.GeneralHandler));
                        targetClient.Send(new SetCombatOnPacket(tamerToShow.Partner.GeneralHandler));
                    }
#if DEBUG
                    var serialized = SerializeShowTamer(tamerToShow);
                    //File.WriteAllText($"Shows\\Show{tamerToShow.Id}To{tamerToSeeId}_{DateTime.Now:dd_MM_yy_HH_mm_ss}.temp", serialized);
#endif
                }
            }
        }

        private void HideTamer(GameMap map, CharacterModel tamerToHide, long tamerToBlindId)
        {
            if (map.ViewingTamer(tamerToHide.Id, tamerToBlindId))
            {
                map.HideTamer(tamerToHide.Id, tamerToBlindId);

                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == tamerToBlindId);

                if (targetClient != null)
                {
                    targetClient.Send(new UnloadTamerPacket(tamerToHide));

#if DEBUG
                    var serialized = SerializeHideTamer(tamerToHide);
                    //File.WriteAllText($"Hides\\Hide{tamerToHide.Id}To{tamerToBlindId}_{DateTime.Now:dd_MM_yy_HH_mm_ss}.temp", serialized);
#endif
                }
            }
        }

        private static string SerializeHideTamer(CharacterModel tamer)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Tamer{tamer.Id}{tamer.Name}");
            sb.AppendLine($"TamerHandler {tamer.GeneralHandler.ToString()}");
            sb.AppendLine($"TamerLocation {tamer.Location.X.ToString()}");
            sb.AppendLine($"TamerLocation {tamer.Location.Y.ToString()}");

            sb.AppendLine($"Partner{tamer.Partner.Id}{tamer.Partner.Name}");
            sb.AppendLine($"PartnerHandler {tamer.Partner.GeneralHandler.ToString()}");
            sb.AppendLine($"PartnerLocation {tamer.Partner.Location.X.ToString()}");
            sb.AppendLine($"PartnerLocation {tamer.Partner.Location.Y.ToString()}");

            return sb.ToString();
        }

        private static string SerializeShowTamer(CharacterModel tamer)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Partner{tamer.Partner.Id}");
            sb.AppendLine($"PartnerName {tamer.Partner.Name}");
            sb.AppendLine($"PartnerLocation {tamer.Partner.Location.X.ToString()}");
            sb.AppendLine($"PartnerLocation {tamer.Partner.Location.Y.ToString()}");
            sb.AppendLine($"PartnerHandler {tamer.Partner.GeneralHandler.ToString()}");
            sb.AppendLine($"PartnerCurrentType {tamer.Partner.CurrentType.ToString()}");
            sb.AppendLine($"PartnerSize {tamer.Partner.Size.ToString()}");
            sb.AppendLine($"PartnerLevel {tamer.Partner.Level.ToString()}");
            sb.AppendLine($"PartnerModel {tamer.Partner.Model.ToString()}");
            sb.AppendLine($"PartnerMS {tamer.Partner.MS.ToString()}");
            sb.AppendLine($"PartnerAS {tamer.Partner.AS.ToString()}");
            sb.AppendLine($"PartnerHPRate {tamer.Partner.HpRate.ToString()}");
            sb.AppendLine($"PartnerCloneTotalLv {tamer.Partner.Digiclone.CloneLevel.ToString()}");
            sb.AppendLine($"PartnerCloneAtLv {tamer.Partner.Digiclone.ATLevel.ToString()}");
            sb.AppendLine($"PartnerCloneBlLv {tamer.Partner.Digiclone.BLLevel.ToString()}");
            sb.AppendLine($"PartnerCloneCtLv {tamer.Partner.Digiclone.CTLevel.ToString()}");
            sb.AppendLine($"PartnerCloneEvLv {tamer.Partner.Digiclone.EVLevel.ToString()}");
            sb.AppendLine($"PartnerCloneHpLv {tamer.Partner.Digiclone.HPLevel.ToString()}");

            sb.AppendLine($"Tamer{tamer.Id}");
            sb.AppendLine($"TamerName {tamer.Name.ToString()}");
            sb.AppendLine($"TamerLocation {tamer.Location.X.ToString()}");
            sb.AppendLine($"TamerLocation {tamer.Location.Y.ToString()}");
            sb.AppendLine($"TamerHandler {tamer.GeneralHandler.ToString()}");
            sb.AppendLine($"TamerModel {tamer.Model.ToString()}");
            sb.AppendLine($"TamerLevel {tamer.Level.ToString()}");
            sb.AppendLine($"TamerMS {tamer.MS.ToString()}");
            sb.AppendLine($"TamerHpRate {tamer.HpRate.ToString()}");
            sb.AppendLine($"TamerEquipment {tamer.Equipment.ToString()}");
            sb.AppendLine($"TamerDigivice {tamer.Digivice.ToString()}");
            sb.AppendLine($"TamerCurrentCondition {tamer.CurrentCondition.ToString()}");
            sb.AppendLine($"TamerSize {tamer.Size.ToString()}");
            sb.AppendLine($"TamerCurrentTitle {tamer.CurrentTitle.ToString()}");
            sb.AppendLine($"TamerSealLeaderId {tamer.SealList.SealLeaderId.ToString()}");

            return sb.ToString();
        }

        private async void CheckEvolution(GameClient client)
        {
            var partner = client.Tamer.Partner;
            var evolutionType = _assets.DigimonBaseInfo
                .First(x => x.Type == partner.CurrentType)
                .EvolutionType;

            // Check if the evolution type is neither Rookie nor Capsule
            if ((EvolutionRankEnum)evolutionType != EvolutionRankEnum.Rookie &&
                (EvolutionRankEnum)evolutionType != EvolutionRankEnum.Capsule &&
                (EvolutionRankEnum)evolutionType != EvolutionRankEnum.Champion)
            {
                await Task.Delay(4000);
                // Activate special map condition only if not Rookie or Capsule
                client.Tamer.IsSpecialMapActive = client.Tamer.Location.MapId == 1109;

                // Determine if evolution should be broken
                if (client.Tamer.BreakEvolution)
                {
                    client.Send(new SystemMessagePacket("You can't digivolve in this Area"));
                }
            }
            else
            {
                client.Tamer.IsSpecialMapActive = false;
            }
        }





        private void PartnerAutoAttack(CharacterModel tamer, GameClient client)
        {
            if (!tamer.Partner.AutoAttack)
                return;

            if (!tamer.Partner.IsAttacking && tamer.TargetMob != null && tamer.TargetMob.Alive & tamer.Partner.Alive)
            {
                tamer.Partner.SetEndAttacking();
                tamer.SetHidden(false);

                if (!tamer.InBattle)
                {
                    _logger.Verbose($"Character {tamer.Id} engaged {tamer.TargetMob.Id} - {tamer.TargetMob.Name}.");
                    BroadcastForTamerViewsAndSelf(tamer.Id, new SetCombatOnPacket(tamer.Partner.GeneralHandler).Serialize());
                    tamer.StartBattle(tamer.TargetMob);
                }

                if (!tamer.TargetMob.InBattle)
                {
                    BroadcastForTamerViewsAndSelf(tamer.Id, new SetCombatOnPacket(tamer.TargetMob.GeneralHandler).Serialize());
                    tamer.TargetMob.StartBattle(tamer);
                }

                var missed = false;

                if (!tamer.GodMode)
                {
                    missed = tamer.CanMissHit();
                }

                if (missed)
                {
                    _logger.Verbose($"Partner {tamer.Partner.Id} missed hit on {tamer.TargetMob.Id} - {tamer.TargetMob.Name}.");
                    BroadcastForTamerViewsAndSelf(tamer.Id, new MissHitPacket(tamer.Partner.GeneralHandler, tamer.TargetMob.GeneralHandler).Serialize());
                }
                else
                {
                    #region Hit Damage
                    var critBonusMultiplier = 0.00;
                    var blocked = false;
                    var finalDmg = tamer.GodMode ? tamer.TargetMob.CurrentHP : CalculateDamage(tamer, client, out critBonusMultiplier, out blocked, _configuration);
                    #endregion

                    if (finalDmg <= 0) finalDmg = 1;
                    if (finalDmg > tamer.TargetMob.CurrentHP) finalDmg = tamer.TargetMob.CurrentHP;

                    var newHp = tamer.TargetMob.ReceiveDamage(finalDmg, tamer.Id);

                    var hitType = blocked ? 2 : critBonusMultiplier > 0 ? 1 : 0;

                    if (newHp > 0)
                    {
                        _logger.Verbose($"Partner {tamer.Partner.Id} inflicted {finalDmg} to mob {tamer.TargetMob?.Id} - {tamer.TargetMob?.Name}({tamer.TargetMob?.Type}).");

                        BroadcastForTamerViewsAndSelf(
                            tamer.Id,
                            new HitPacket(
                                tamer.Partner.GeneralHandler,
                                tamer.TargetMob.GeneralHandler,
                                finalDmg,
                                tamer.TargetMob.HPValue,
                                newHp,
                                hitType).Serialize());
                    }
                    else
                    {
                        _logger.Verbose($"Partner {tamer.Partner.Id} killed mob {tamer.TargetMob?.Id} - {tamer.TargetMob?.Name}({tamer.TargetMob?.Type}) with {finalDmg} damage.");

                        BroadcastForTamerViewsAndSelf(
                            tamer.Id,
                            new KillOnHitPacket(
                                tamer.Partner.GeneralHandler,
                                tamer.TargetMob.GeneralHandler,
                                finalDmg,
                                hitType).Serialize());

                        tamer.TargetMob?.Die();

                        if (!MobsAttacking(tamer.Location.MapId, tamer.Id))
                        {
                            tamer.StopBattle();

                            BroadcastForTamerViewsAndSelf(
                                tamer.Id,
                                new SetCombatOffPacket(tamer.Partner.GeneralHandler).Serialize());
                        }
                    }
                }

                tamer.Partner.UpdateLastHitTime();
            }

            if (!tamer.Partner.IsAttacking && tamer.TargetSummonMob != null && tamer.TargetSummonMob.Alive & tamer.Partner.Alive)
            {
                tamer.Partner.SetEndAttacking();
                tamer.SetHidden(false);

                if (!tamer.InBattle)
                {
                    _logger.Verbose($"Character {tamer.Id} engaged {tamer.TargetSummonMob.Id} - {tamer.TargetSummonMob.Name}.");
                    BroadcastForTamerViewsAndSelf(tamer.Id, new SetCombatOnPacket(tamer.Partner.GeneralHandler).Serialize());
                    tamer.StartBattle(tamer.TargetMob);
                }

                if (!tamer.TargetSummonMob.InBattle)
                {
                    BroadcastForTamerViewsAndSelf(tamer.Id, new SetCombatOnPacket(tamer.TargetSummonMob.GeneralHandler).Serialize());
                    tamer.TargetSummonMob.StartBattle(tamer);
                }

                var missed = false;

                if (!tamer.GodMode)
                {
                    missed = tamer.CanMissHit(true);
                }

                if (missed)
                {
                    _logger.Verbose($"Partner {tamer.Partner.Id} missed hit on {tamer.TargetSummonMob.Id} - {tamer.TargetSummonMob.Name}.");
                    BroadcastForTamerViewsAndSelf(tamer.Id, new MissHitPacket(tamer.Partner.GeneralHandler, tamer.TargetSummonMob.GeneralHandler).Serialize());
                }
                else
                {
                    #region Hit Damage
                    var critBonusMultiplier = 0.00;
                    var blocked = false;
                    var finalDmg = tamer.GodMode ? tamer.TargetSummonMob.CurrentHP : CalculateDamageSummon (tamer, client, out critBonusMultiplier, out blocked, _configuration);
                    #endregion

                    if (finalDmg <= 0) finalDmg = 1;
                    if (finalDmg > tamer.TargetSummonMob.CurrentHP) finalDmg = tamer.TargetSummonMob.CurrentHP;

                    var newHp = tamer.TargetSummonMob.ReceiveDamage(finalDmg, tamer.Id);

                    var hitType = blocked ? 2 : critBonusMultiplier > 0 ? 1 : 0;

                    if (newHp > 0)
                    {
                        _logger.Verbose($"Partner {tamer.Partner.Id} inflicted {finalDmg} to mob {tamer.TargetSummonMob?.Id} - {tamer.TargetSummonMob?.Name}({tamer.TargetSummonMob?.Type}).");

                        BroadcastForTamerViewsAndSelf(
                            tamer.Id,
                            new HitPacket(
                                tamer.Partner.GeneralHandler,
                                tamer.TargetSummonMob.GeneralHandler,
                                finalDmg,
                                tamer.TargetSummonMob.HPValue,
                                newHp,
                                hitType).Serialize());
                    }
                    else
                    {
                        _logger.Verbose($"Partner {tamer.Partner.Id} killed mob {tamer.TargetSummonMob?.Id} - {tamer.TargetSummonMob?.Name}({tamer.TargetSummonMob?.Type}) with {finalDmg} damage.");

                        BroadcastForTamerViewsAndSelf(
                            tamer.Id,
                            new KillOnHitPacket(
                                tamer.Partner.GeneralHandler,
                                tamer.TargetSummonMob.GeneralHandler,
                                finalDmg,
                                hitType).Serialize());

                        tamer.TargetSummonMob?.Die();

                        if (!MobsAttacking(tamer.Location.MapId, tamer.Id))
                        {
                            tamer.StopBattle(true);

                            BroadcastForTamerViewsAndSelf(
                                tamer.Id,
                                new SetCombatOffPacket(tamer.Partner.GeneralHandler).Serialize());
                        }
                    }
                }

                tamer.Partner.UpdateLastHitTime();
            }

            bool StopAttack = tamer.TargetSummonMob == null || !tamer.TargetSummonMob.Alive;
            bool StopAttack2 = tamer.TargetMob == null || !tamer.TargetMob.Alive;

            if (StopAttack && StopAttack2)
            {
                tamer.Partner?.StopAutoAttack();
                tamer.StopBattle();
            }

        }

        private ReceiveExpResult ReceiveTamerExp(CharacterModel tamer, long tamerExpToReceive)
        {
            var tamerResult = _expManager.ReceiveTamerExperience(tamerExpToReceive, tamer);

            if (tamerResult.LevelGain > 0)
            {
                BroadcastForTamerViewsAndSelf(tamer.Id,
                    new LevelUpPacket(tamer.GeneralHandler, tamer.Level).Serialize());

                tamer.SetLevelStatus(
                    _statusManager.GetTamerLevelStatus(
                        tamer.Model,
                        tamer.Level
                    )
                );

                tamer.FullHeal();
            }

            return tamerResult;
        }

        private ReceiveExpResult ReceiveBonusTamerExp(CharacterModel tamer, long totalTamerExp)
        {
            var tamerResult = _expManager.ReceiveTamerExperience(totalTamerExp, tamer);

            if (tamerResult.LevelGain > 0)
            {
                BroadcastForTamerViewsAndSelf(tamer.Id,
                    new LevelUpPacket(tamer.GeneralHandler, tamer.Level).Serialize());

                tamer.SetLevelStatus(
                    _statusManager.GetTamerLevelStatus(
                        tamer.Model,
                        tamer.Level
                    )
                );

                tamer.FullHeal();
            }

            return tamerResult;
        }

        private ReceiveExpResult ReceivePartnerExp(DigimonModel partner, MobConfigModel targetMob, long partnerExpToReceive)
        {
            var partnerResult = _expManager.ReceiveDigimonExperience(partnerExpToReceive, partner);

            _expManager.ReceiveAttributeExperience(partner, targetMob.Attribute, targetMob.ExpReward);
            _expManager.ReceiveElementExperience(partner, targetMob.Element, targetMob.ExpReward);

            partner.ReceiveSkillExp(targetMob.ExpReward.SkillExperience);

            if (partnerResult.LevelGain > 0)
            {
                partner.SetBaseStatus(
                    _statusManager.GetDigimonBaseStatus(
                        partner.CurrentType,
                        partner.Level,
                        partner.Size
                    )
                );

                BroadcastForTamerViewsAndSelf(partner.Character.Id,
                    new LevelUpPacket(partner.GeneralHandler, partner.Level).Serialize());

                partner.FullHeal();
            }
            return partnerResult;
        }
        private ReceiveExpResult ReceiveBonusPartnerExp(DigimonModel partner, MobConfigModel targetMob, long totalPartnerExp)
        {
            var partnerResult = _expManager.ReceiveDigimonExperience(totalPartnerExp, partner);

            if (partnerResult.LevelGain > 0)
            {
                partner.SetBaseStatus(
                    _statusManager.GetDigimonBaseStatus(
                        partner.CurrentType,
                        partner.Level,
                        partner.Size
                    )
                );

                BroadcastForTamerViewsAndSelf(partner.Character.Id,
                    new LevelUpPacket(partner.GeneralHandler, partner.Level).Serialize());

                partner.FullHeal();
            }
            return partnerResult;
        }
        private ReceiveExpResult ReceivePartnerExp(DigimonModel partner, SummonMobModel targetMob, long partnerExpToReceive)
        {
            var partnerResult = _expManager.ReceiveDigimonExperience(partnerExpToReceive, partner);

            _expManager.ReceiveAttributeExperience(partner, targetMob.Attribute, targetMob.ExpReward);
            _expManager.ReceiveElementExperience(partner, targetMob.Element, targetMob.ExpReward);

            partner.ReceiveSkillExp(targetMob.ExpReward.SkillExperience);

            if (partnerResult.LevelGain > 0)
            {
                partner.SetBaseStatus(
                    _statusManager.GetDigimonBaseStatus(
                        partner.CurrentType,
                        partner.Level,
                        partner.Size
                    )
                );

                BroadcastForTamerViewsAndSelf(partner.Character.Id,
                    new LevelUpPacket(partner.GeneralHandler, partner.Level).Serialize());

                partner.FullHeal();
            }

            return partnerResult;
        }

        private static double GetAttributeDamage(CharacterModel tamer, IConfiguration configuration)
        {
            double multiplier = 0;
            double partnerAT = tamer.Partner.AT;
            var gameConfig = new GameConfigurationModel();
            configuration.GetSection("GameConfigs").Bind(gameConfig);


            // Check if the tamer's partner has an attribute advantage over the target mob
            if (tamer.Partner.BaseInfo.Attribute.HasAttributeAdvantage(tamer.TargetMob.Attribute))
            {
                double currentExperience = tamer.Partner.GetAttributeExperience();
                const double maxExperience = 10000;

                // Calculate the bonus multiplier based on experience, ensuring it does not exceed 0.5
                double bonusMultiplier = currentExperience / maxExperience;
                multiplier += Math.Min(bonusMultiplier, Double.Parse(configuration["GameConfigs:Attribute:AdvantageMultiplier"] ?? "0.1"));
            }
            // Check if the target mob has an attribute advantage over the tamer's partner
            else if (tamer.TargetMob.Attribute.HasAttributeAdvantage(tamer.Partner.BaseInfo.Attribute))
            {
                multiplier = Double.Parse(configuration["GameConfigs:Attribute:DisAdvantageMultiplier"] ?? "0.1");
            }

            double attributeDamage = partnerAT * multiplier;
            return attributeDamage;
        }



        private static double GetElementDamage(CharacterModel tamer, IConfiguration configuration)
        {
            double multiplier = 0;
            double partnerAT = tamer.Partner.AT;
            var gameConfig = new GameConfigurationModel();
            configuration.GetSection("GameConfigs").Bind(gameConfig);

            // Check if the tamer's partner has an element advantage over the target mob
            if (tamer.Partner.BaseInfo.Element.HasElementAdvantage(tamer.TargetMob.Element))
            {
                double currentExperience = tamer.Partner.GetElementExperience();
                const double maxExperience = 10000;

                // Calculate the bonus multiplier based on experience, ensuring it does not exceed 0.5
                double bonusMultiplier = currentExperience / maxExperience;
                multiplier += Math.Min(bonusMultiplier, Double.Parse(configuration["GameConfigs:Element:AdvantageMultiplier"] ?? "0.1"));
            }
            // Check if the target mob has an element advantage over the tamer's partner
            else if (tamer.TargetMob.Element.HasElementAdvantage(tamer.Partner.BaseInfo.Element))
            {
                multiplier = Double.Parse(configuration["GameConfigs:Element:DisAdvantageMultiplier"] ?? "0.1");
            }

            double elementDamage = partnerAT * multiplier;
            return elementDamage;
        }
        //critBonusMultiplier = 0;
        //    blocked = false; maybe for later??
        public int CalculateDamage(CharacterModel tamer, GameClient client, out double critBonusMultiplier, out bool blocked, IConfiguration configuration = null)
        {
            double baseDamage = tamer.Partner.AT;

            var random = new Random();
            // Generate a random bonus between 0% and 5% of the original value
            double percentageBonus = random.NextDouble() * 0.05;
            // Calculate the final base damage with the random bonus
            baseDamage *= (1.0 + percentageBonus);

            // Ensure baseDamage is non-negative
            if (baseDamage < 0) baseDamage = 0;

            // Level-based bonus damage calculation
            double levelBonusMultiplier = tamer.Partner.Level > tamer.TargetMob.Level ? (0.01 * (tamer.Partner.Level - tamer.TargetMob.Level)) : 0;
            int levelBonusDamage = (int)(baseDamage * levelBonusMultiplier);
            double enemyDefence = ((client.Tamer.TargetMob.DEValue / 2) + (tamer.TargetMob.Level * 20));

            // Attribute and element damage calculations
            double attributeDamage = GetAttributeDamage(tamer, configuration);
            double elementDamage = GetElementDamage(tamer, configuration);

            // Determine if the attack is blocked
            blocked = tamer.TargetMob.BLValue >= UtilitiesFunctions.RandomDouble();
            if (blocked)
            {
                baseDamage /= 2;
            }

            // Check for a critical hit
            double critChance = tamer.Partner.CC / 100.0;
            bool isCriticalHit = critChance >= UtilitiesFunctions.RandomDouble() && tamer.Partner.CD > 0;
            if (isCriticalHit)
            {
                blocked = false;  // Critical hits can't be blocked
                critBonusMultiplier = 1.0;
                double criticalDamage = baseDamage * (1.0 + tamer.Partner.CD / 100.0);
                baseDamage = criticalDamage;  // Apply critical damage as the new base damage
            }
            else
            {
                critBonusMultiplier = 0;
            }

            double totalDamage = baseDamage + attributeDamage + elementDamage + levelBonusDamage - enemyDefence;

            // Broadcast attribute damage message if applicable
            if (attributeDamage < 0)
            {
                string attributeMessage = $"{Math.Floor(attributeDamage)} Attribute DMG!";
                BroadcastForUniqueTamer(client.TamerId, new GuildMessagePacket(client.Tamer.Partner.Name, attributeMessage).Serialize());
            }
            else if (attributeDamage > 0)
            {
                string attributeMessage = $"+{Math.Floor(attributeDamage)} Attribute DMG!";
                BroadcastForUniqueTamer(client.TamerId,new GuildMessagePacket(client.Tamer.Partner.Name,attributeMessage).Serialize());
            }

            // Broadcast element damage message if applicable
            if (elementDamage < 0)
            {
                string elementMessage = $"{Math.Floor(elementDamage)} Element DMG!";
                string receiverName = client.Tamer.Partner.Name;
                client.Send(new ChatMessagePacket(elementMessage, ChatTypeEnum.Whisper, WhisperResultEnum.Success, client.Tamer.Partner.Name, receiverName));
            }
            else if (elementDamage > 0)
            {
                string elementMessage = $"+{Math.Floor(elementDamage)} Element DMG!";
                string receiverName = client.Tamer.Partner.Name;
                client.Send(new ChatMessagePacket(elementMessage,ChatTypeEnum.Whisper,WhisperResultEnum.Success,client.Tamer.Partner.Name,receiverName));
            }


            // Broadcast total damage message if applicable
            if (totalDamage < 0)
            {
                string message = $"Enemy Digimon's defence is way too high";
                BroadcastForUniqueTamer(client.TamerId, new ChatMessagePacket(message, ChatTypeEnum.Shout, client.Tamer.Partner.Name).Serialize());
            }
            else if (totalDamage > 0)
            {
                string message = isCriticalHit
                    ? $"{Math.Floor(totalDamage)} Crit DMG @{enemyDefence} enemy DE"
                    : $"{Math.Floor(totalDamage)} DMG @{enemyDefence} enemy DE";

                BroadcastForUniqueTamer(client.TamerId, new ChatMessagePacket(message, ChatTypeEnum.Shout, client.Tamer.Partner.Name).Serialize());
            }
            return (int)totalDamage ;
        }

        private static double GetAttributeDamageSummon(CharacterModel tamer,IConfiguration configuration)
        {
            double multiplier = 0;
            double partnerAT = tamer.Partner.AT;
            var gameConfig = new GameConfigurationModel();
            configuration.GetSection("GameConfigs").Bind(gameConfig);


            // Check if the tamer's partner has an attribute advantage over the target mob
            if (tamer.Partner.BaseInfo.Attribute.HasAttributeAdvantage(tamer.TargetSummonMob.Attribute))
            {
                double currentExperience = tamer.Partner.GetAttributeExperience();
                const double maxExperience = 10000;

                // Calculate the bonus multiplier based on experience, ensuring it does not exceed 0.5
                double bonusMultiplier = currentExperience / maxExperience;
                multiplier += Math.Min(bonusMultiplier,Double.Parse(configuration["GameConfigs:Attribute:AdvantageMultiplier"] ?? "0.1"));
            }
            // Check if the target mob has an attribute advantage over the tamer's partner
            else if (tamer.TargetSummonMob.Attribute.HasAttributeAdvantage(tamer.Partner.BaseInfo.Attribute))
            {
                multiplier = Double.Parse(configuration["GameConfigs:Attribute:DisAdvantageMultiplier"] ?? "0.1");
            }

            double attributeDamage = partnerAT * multiplier;
            return attributeDamage;
        }



        private static double GetElementDamageSummon(CharacterModel tamer,IConfiguration configuration)
        {
            double multiplier = 0;
            double partnerAT = tamer.Partner.AT;
            var gameConfig = new GameConfigurationModel();
            configuration.GetSection("GameConfigs").Bind(gameConfig);

            // Check if the tamer's partner has an element advantage over the target mob
            if (tamer.Partner.BaseInfo.Element.HasElementAdvantage(tamer.TargetSummonMob.Element))
            {
                double currentExperience = tamer.Partner.GetElementExperience();
                const double maxExperience = 10000;

                // Calculate the bonus multiplier based on experience, ensuring it does not exceed 0.5
                double bonusMultiplier = currentExperience / maxExperience;
                multiplier += Math.Min(bonusMultiplier,Double.Parse(configuration["GameConfigs:Element:AdvantageMultiplier"] ?? "0.1"));
            }
            // Check if the target mob has an element advantage over the tamer's partner
            else if (tamer.TargetSummonMob.Element.HasElementAdvantage(tamer.Partner.BaseInfo.Element))
            {
                multiplier = Double.Parse(configuration["GameConfigs:Element:DisAdvantageMultiplier"] ?? "0.1");
            }

            double elementDamage = partnerAT * multiplier;
            return elementDamage;
        }
        //critBonusMultiplier = 0;
        //    blocked = false; maybe for later??
        public int CalculateDamageSummon(CharacterModel tamer,GameClient client,out double critBonusMultiplier,out bool blocked,IConfiguration configuration = null)
        {
            double baseDamage = tamer.Partner.AT;

            var random = new Random();
            // Generate a random bonus between 0% and 5% of the original value
            double percentageBonus = random.NextDouble() * 0.05;
            // Calculate the final base damage with the random bonus
            baseDamage *= (1.0 + percentageBonus);

            // Ensure baseDamage is non-negative
            if (baseDamage < 0) baseDamage = 0;

            // Level-based bonus damage calculation
            double levelBonusMultiplier = tamer.Partner.Level > tamer.TargetSummonMob.Level ? (0.01 * (tamer.Partner.Level - tamer.TargetSummonMob.Level)) : 0;
            int levelBonusDamage = (int)(baseDamage * levelBonusMultiplier);
            double enemyDefence = ((client.Tamer.TargetSummonMob.DEValue / 2) + (tamer.TargetSummonMob.Level * 20));

            // Attribute and element damage calculations
            double attributeDamage = GetAttributeDamageSummon(tamer,configuration);
            double elementDamage = GetElementDamageSummon(tamer,configuration);

            // Determine if the attack is blocked
            blocked = tamer.TargetSummonMob.BLValue >= UtilitiesFunctions.RandomDouble();
            if (blocked)
            {
                baseDamage /= 2;
            }

            // Check for a critical hit
            double critChance = tamer.Partner.CC / 100.0;
            bool isCriticalHit = critChance >= UtilitiesFunctions.RandomDouble() && tamer.Partner.CD > 0;
            if (isCriticalHit)
            {
                blocked = false;  // Critical hits can't be blocked
                critBonusMultiplier = 1.0;
                double criticalDamage = baseDamage * (1.0 + tamer.Partner.CD / 100.0);
                baseDamage = criticalDamage;  // Apply critical damage as the new base damage
            }
            else
            {
                critBonusMultiplier = 0;
            }

            double totalDamage = baseDamage + attributeDamage + elementDamage + levelBonusDamage - enemyDefence;

            // Broadcast attribute damage message if applicable
            if (attributeDamage < 0)
            {
                string attributeMessage = $"{Math.Floor(attributeDamage)} Attribute DMG!";
                BroadcastForUniqueTamer(client.TamerId,new GuildMessagePacket(client.Tamer.Partner.Name,attributeMessage).Serialize());
            }
            else if (attributeDamage > 0)
            {
                string attributeMessage = $"+{Math.Floor(attributeDamage)} Attribute DMG!";
                BroadcastForUniqueTamer(client.TamerId,new GuildMessagePacket(client.Tamer.Partner.Name,attributeMessage).Serialize());
            }

            // Broadcast element damage message if applicable
            if (elementDamage < 0)
            {
                string elementMessage = $"{Math.Floor(elementDamage)} Element DMG!";
                string receiverName = client.Tamer.Partner.Name;
                client.Send(new ChatMessagePacket(elementMessage,ChatTypeEnum.Whisper,WhisperResultEnum.Success,client.Tamer.Partner.Name,receiverName));
            }
            else if (elementDamage > 0)
            {
                string elementMessage = $"+{Math.Floor(elementDamage)} Element DMG!";
                string receiverName = client.Tamer.Partner.Name;
                client.Send(new ChatMessagePacket(elementMessage,ChatTypeEnum.Whisper,WhisperResultEnum.Success,client.Tamer.Partner.Name,receiverName));
            }


            // Broadcast total damage message if applicable
            if (totalDamage < 0)
            {
                string message = $"Enemy Digimon's defence is way too high";
                BroadcastForUniqueTamer(client.TamerId,new ChatMessagePacket(message,ChatTypeEnum.Shout,client.Tamer.Partner.Name).Serialize());
            }
            else if (totalDamage > 0)
            {
                string message = isCriticalHit
                    ? $"{Math.Floor(totalDamage)} Crit DMG @{enemyDefence} enemy DE"
                    : $"{Math.Floor(totalDamage)} DMG @{enemyDefence} enemy DE";

                BroadcastForUniqueTamer(client.TamerId,new ChatMessagePacket(message,ChatTypeEnum.Shout,client.Tamer.Partner.Name).Serialize());
            }
            return (int)totalDamage;
        }


        private void CheckMonthlyReward(GameClient client)
        {
            if (client.Tamer.AttendanceReward.ReedemRewards)
            {
                client.Tamer.AttendanceReward.SetLastRewardDate();
                client.Tamer.AttendanceReward.IncreaseTotalDays();
                ReedemReward(client);
            }

            client.Send(new TamerAttendancePacket(client.Tamer.AttendanceReward.TotalDays));
        }

        private void ReedemReward(GameClient client)
        {
            var rewardInfo = _assets.MonthlyEvents.FirstOrDefault(x => x.CurrentDay == client.Tamer.AttendanceReward.TotalDays);

            if (rewardInfo != null)
            {
                var newItem = new ItemModel();
                newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == rewardInfo.ItemId));

                if (newItem.ItemInfo == null)
                {
                    return;
                }

                newItem.ItemId = rewardInfo.ItemId;
                newItem.Amount = rewardInfo.ItemCount;

                if (newItem.IsTemporary)
                    newItem.SetRemainingTime((uint)newItem.ItemInfo.UsageTimeMinutes);

                var itemClone = (ItemModel)newItem.Clone();

                if (client.Tamer.GiftWarehouse.AddItem(newItem))
                {
                    _sender.Send(new UpdateItemsCommand(client.Tamer.GiftWarehouse));
                }

                _sender.Send(new UpdateTamerAttendanceRewardCommand(client.Tamer.AttendanceReward));
            }
        }

    }
}