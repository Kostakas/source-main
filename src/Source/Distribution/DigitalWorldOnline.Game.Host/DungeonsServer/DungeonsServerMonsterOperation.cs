﻿using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.Map;
using DigitalWorldOnline.Commons.Extensions;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.Config;
using DigitalWorldOnline.Commons.Models.Map;
using DigitalWorldOnline.Commons.Models.Summon;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.GameServer.Arena;
using DigitalWorldOnline.Commons.Packets.GameServer.Combat;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Packets.MapServer;
using DigitalWorldOnline.Commons.Utils;
using DigitalWorldOnline.Commons.ViewModel.Players;
using DigitalWorldOnline.Commons.Writers;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.Infraestructure.Migrations;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DigitalWorldOnline.GameHost
{
    public sealed partial class DungeonsServer
    {
        private void MonsterOperation(GameMap map)
        {
            if (!map.ConnectedTamers.Any())
                return;

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            map.UpdateMapMobs(_assets.NpcColiseum);

            foreach (var mob in map.Mobs)
            {
                if (!mob.AwaitingKillSpawn && DateTime.Now > mob.ViewCheckTime)
                {
                    if (mob.CurrentAction == MobActionEnum.Destroy)
                        continue;

                    mob.SetViewCheckTime(2);

                    mob.TamersViewing.RemoveAll(x => !map.ConnectedTamers.Select(y => y.Id).Contains(x));

                    var nearTamers = map.NearestTamers(mob.Id);

                    if (!nearTamers.Any() && !mob.TamersViewing.Any())
                        continue;

                    if (!mob.Dead && mob.CurrentAction != MobActionEnum.Destroy)
                    {
                        nearTamers.ForEach(nearTamer =>
                        {
                            if (!mob.TamersViewing.Contains(nearTamer))
                            {
                                mob.TamersViewing.Add(nearTamer);

                                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == nearTamer);

                                targetClient?.Send(new LoadMobsPacket(mob));
                                targetClient?.Send(new LoadBuffsPacket(mob));
                            }
                        });
                    }

                    var farTamers = map.ConnectedTamers.Select(x => x.Id).Except(nearTamers).ToList();

                    farTamers.ForEach(farTamer =>
                    {
                        if (mob.TamersViewing.Contains(farTamer))
                        {
                            var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == farTamer);

                            mob.TamersViewing.Remove(farTamer);
                            targetClient?.Send(new UnloadMobsPacket(mob));
                        }
                    });
                }

                if (!mob.CanAct)
                    continue;

                MobsOperation(map, mob);

                mob.SetNextAction();
            }

            map.UpdateMapMobs(true);

            foreach (var mob in map.SummonMobs)
            {
                if (DateTime.Now > mob.ViewCheckTime)
                {
                    mob.SetViewCheckTime(2);

                    mob.TamersViewing.RemoveAll(x => !map.ConnectedTamers.Select(y => y.Id).Contains(x));

                    var nearTamers = map.NearestTamers(mob.Id);

                    if (!nearTamers.Any() && !mob.TamersViewing.Any())
                        continue;

                    if (!mob.Dead && mob.CurrentAction != MobActionEnum.Destroy)
                    {
                        nearTamers.ForEach(nearTamer =>
                        {
                            if (!mob.TamersViewing.Contains(nearTamer))
                            {
                                mob.TamersViewing.Add(nearTamer);

                                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == nearTamer);

                                targetClient?.Send(new LoadMobsPacket(mob,true));

                            }
                        });
                    }

                    var farTamers = map.ConnectedTamers.Select(x => x.Id).Except(nearTamers).ToList();

                    farTamers.ForEach(farTamer =>
                    {
                        if (mob.TamersViewing.Contains(farTamer))
                        {
                            var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == farTamer);

                            mob.TamersViewing.Remove(farTamer);
                            targetClient?.Send(new UnloadMobsPacket(mob));
                        }
                    });
                }

                if (!mob.CanAct)
                    continue;

                MobsOperation(map, mob);

                mob.SetNextAction();
            }
            stopwatch.Stop();

            var totalTime = stopwatch.Elapsed.TotalMilliseconds;

            if (totalTime >= 1000)
                Console.WriteLine($"MonstersOperation ({map.Mobs.Count}): {totalTime}.");
        }

        private void MobsOperation(GameMap map, MobConfigModel mob)
        {

            switch (mob.CurrentAction)
            {
                case MobActionEnum.CrowdControl:
                    {
                        var debuff = mob.DebuffList.ActiveBuffs.Where(buff =>
                                buff.BuffInfo.SkillInfo.Apply.Any(apply =>
                                    apply.Attribute == Commons.Enums.SkillCodeApplyAttributeEnum.CrowdControl
                                )
                            ).ToList();

                        if (debuff.Any())
                        {
                            CheckDebuff(map, mob, debuff);
                            break;
                        }
                    }
                    break;

                case MobActionEnum.Respawn:
                    {
                        mob.Reset();
                        mob.ResetLocation();
                    }
                    break;

                case MobActionEnum.Reward:
                    {
                        ItemsReward(map, mob);
                        QuestKillReward(map, mob);
                        ExperienceReward(map, mob);

                        SourceKillSpawn(map, mob);
                        TargetKillSpawn(map, mob);

                        ColiseumStageClear(map, mob);

                        mob.UpdateCurrentAction(MobActionEnum.Destroy);
                    }
                    break;

                case MobActionEnum.Wait:
                    {
                        if (mob.Respawn && DateTime.Now > mob.DieTime.AddSeconds(2))
                        {

                            mob.SetNextWalkTime(UtilitiesFunctions.RandomInt(7, 14));
                            mob.SetAgressiveCheckTime(5);
                            mob.SetRespawn();
                        }
                        else
                        {
                            map.AttackNearbyTamer(mob, mob.TamersViewing, _assets.NpcColiseum);
                        }
                    }
                    break;

                case MobActionEnum.Walk:
                    {
                        map.BroadcastForTargetTamers(mob.TamersViewing, new SyncConditionPacket(mob.GeneralHandler, ConditionEnum.Default).Serialize());
                        mob.Move();
                        map.BroadcastForTargetTamers(mob.TamersViewing, new MobWalkPacket(mob).Serialize());
                    }
                    break;

                case MobActionEnum.GiveUp:
                    {
                        map.BroadcastForTargetTamers(mob.TamersViewing, new SyncConditionPacket(mob.GeneralHandler, ConditionEnum.Immortal).Serialize());
                        mob.ResetLocation();
                        map.BroadcastForTargetTamers(mob.TamersViewing, new MobRunPacket(mob).Serialize());
                        map.BroadcastForTargetTamers(mob.TamersViewing, new SetCombatOffPacket(mob.GeneralHandler).Serialize());

                        foreach (var targetTamer in mob.TargetTamers)
                        {
                            if (targetTamer.TargetMobs.Count <= 1)
                            {
                                targetTamer.StopBattle();
                                map.BroadcastForTamerViewsAndSelf(targetTamer.Id, new SetCombatOffPacket(targetTamer.Partner.GeneralHandler).Serialize());
                            }
                        }

                        mob.Reset(true);
                        map.BroadcastForTargetTamers(mob.TamersViewing, new UpdateCurrentHPRatePacket(mob.GeneralHandler, mob.CurrentHpRate).Serialize());
                    }
                    break;

                case MobActionEnum.Attack:
                    {
                        var debuff = mob.DebuffList.ActiveBuffs.Where(buff =>
                                buff.BuffInfo.SkillInfo.Apply.Any(apply =>
                                    apply.Attribute == Commons.Enums.SkillCodeApplyAttributeEnum.CrowdControl
                                )
                            ).ToList();

                        if (debuff.Any())
                        {
                            CheckDebuff(map, mob, debuff);
                            break;
                        }
                        if (!mob.Dead && mob.SkillTime && !mob.CheckSkill && mob.IsPossibleSkill)
                        {
                            mob.UpdateCurrentAction(MobActionEnum.UseAttackSkill);
                            mob.SetNextAction();
                            break;
                        }

                        if (!mob.Dead && ((mob.TargetTamer == null || mob.TargetTamer.Hidden) || DateTime.Now > mob.LastHitTryTime.AddSeconds(15))) //Anti-kite
                        {
                            mob.GiveUp();
                            break;
                        }

                        if (!mob.Dead && !mob.Chasing && mob.TargetAlive)
                        {
                            var diff = UtilitiesFunctions.CalculateDistance(
                                mob.CurrentLocation.X,
                                mob.Target.Location.X,
                                mob.CurrentLocation.Y,
                                mob.Target.Location.Y);

                            var range = Math.Max(mob.ARValue, mob.Target.BaseInfo.ARValue);
                            if (diff <= range)
                            {
                                if (DateTime.Now < mob.LastHitTime.AddMilliseconds(mob.ASValue))
                                    break;

                                var missed = false;

                                if (mob.TargetTamer != null && mob.TargetTamer.GodMode)
                                    missed = true;
                                else if (mob.CanMissHit())
                                    missed = true;

                                if (missed)
                                {
                                    mob.UpdateLastHitTry();
                                    map.BroadcastForTargetTamers(mob.TamersViewing, new MissHitPacket(mob.GeneralHandler, mob.TargetHandler).Serialize());
                                    mob.UpdateLastHit();
                                    break;
                                }

                                map.AttackTarget(mob, _assets.NpcColiseum);
                            }
                            else
                            {
                                map.ChaseTarget(mob);
                            }
                        }

                        if (mob.Dead)
                        {
                            foreach (var targetTamer in mob.TargetTamers)
                            {

                                targetTamer.StopBattle();
                                map.BroadcastForTamerViewsAndSelf(targetTamer.Id, new SetCombatOffPacket(targetTamer.Partner.GeneralHandler).Serialize());

                            }


                        }
                    }
                    break;

                case MobActionEnum.UseAttackSkill:
                    {
                        var debuff = mob.DebuffList.ActiveBuffs.Where(buff =>
                                buff.BuffInfo.SkillInfo.Apply.Any(apply =>
                                    apply.Attribute == Commons.Enums.SkillCodeApplyAttributeEnum.CrowdControl
                                )
                            ).ToList();

                        if (debuff.Any())
                        {
                            CheckDebuff(map, mob, debuff);
                            break;
                        }

                        if (!mob.Dead && ((mob.TargetTamer == null || mob.TargetTamer.Hidden))) //Anti-kite
                        {
                            mob.GiveUp();
                            break;
                        }

                        var skillList = _assets.MonsterSkillInfo.Where(x => x.Type == mob.Type).ToList();

                        if (!skillList.Any())
                        {
                            mob.UpdateCheckSkill(true);
                            mob.UpdateCurrentAction(MobActionEnum.Wait);
                            mob.UpdateLastSkill();
                            mob.UpdateLastSkillTry();
                            mob.SetNextAction();
                            break;
                        }

                        Random random = new Random();

                        var targetSkill = skillList[random.Next(0, skillList.Count)];

                        if (!mob.Dead && !mob.Chasing && mob.TargetAlive)
                        {
                            var diff = UtilitiesFunctions.CalculateDistance(
                               mob.CurrentLocation.X,
                               mob.Target.Location.X,
                               mob.CurrentLocation.Y,
                               mob.Target.Location.Y);

                            if (diff <= 1900)
                            {
                                if (DateTime.Now < mob.LastSkillTime.AddMilliseconds(mob.Cooldown) && mob.Cooldown > 0)
                                    break;

                                map.SkillTarget(mob, targetSkill, _assets.NpcColiseum);



                                if (mob.Target != null)
                                {
                                    mob.UpdateCurrentAction(MobActionEnum.Wait);

                                    mob.SetNextAction();
                                }
                            }
                            else
                            {
                                map.ChaseTarget(mob);
                            }
                        }

                        if (mob.Dead)
                        {
                            foreach (var targetTamer in mob.TargetTamers)
                            {

                                targetTamer.StopBattle();
                                map.BroadcastForTamerViewsAndSelf(targetTamer.Id, new SetCombatOffPacket(targetTamer.Partner.GeneralHandler).Serialize());

                            }

                            break;
                        }
                    }
                    break;

            }
        }

        private static void CheckDebuff(GameMap map, MobConfigModel mob, List<MobDebuffModel> debuffs)
        {


            if (debuffs != null)
            {
                for (int i = 0; i < debuffs.Count; i++)
                {
                    var debuff = debuffs[i];

                    if (!debuff.Expired && mob.CurrentAction != MobActionEnum.CrowdControl)
                    {
                        mob.UpdateCurrentAction(MobActionEnum.CrowdControl);
                    }

                    if (debuff.Expired && mob.CurrentAction == MobActionEnum.CrowdControl)
                    {
                        debuffs.Remove(debuff);

                        if (debuffs.Count == 0)
                        {

                            map.BroadcastForTargetTamers(mob.TamersViewing, new RemoveBuffPacket(mob.GeneralHandler, debuff.BuffId, 1).Serialize());

                            mob.DebuffList.Buffs.Remove(debuff);

                            mob.UpdateCurrentAction(MobActionEnum.Wait);
                            mob.SetNextAction();

                        }
                        else
                        {
                            mob.DebuffList.Buffs.Remove(debuff);
                        }
                    }
                }

            }

        }

        private void ColiseumStageClear(GameMap map, MobConfigModel mob)
        {
            if (map.ColiseumMobs.Contains((int)mob.Id))
            {
                map.ColiseumMobs.Remove((int)mob.Id);

                if (map.ColiseumMobs.Count == 1)
                {
                    var npcInfo = _assets.NpcColiseum.FirstOrDefault(x => x.NpcId == map.ColiseumMobs.First());

                    if (npcInfo != null)
                    {
                        foreach (var player in map.Clients.Where(x => x.Tamer.Partner.Alive))
                        {
                            player.Tamer.Points.IncreaseAmount(npcInfo.MobInfo[player.Tamer.Points.CurrentStage - 1].WinPoints);

                            _sender.Send(new UpdateCharacterArenaPointsCommand(player.Tamer.Points));

                            player?.Send(new DungeonArenaStageClearPacket(mob.Type, mob.TargetTamer.Points.CurrentStage, mob.TargetTamer.Points.Amount, npcInfo.MobInfo[mob.TargetTamer.Points.CurrentStage - 1].WinPoints, map.ColiseumMobs.First()));

                        }

                    }
                }
            }
        }

        private static void TargetKillSpawn(GameMap map, MobConfigModel mob)
        {
            var targetKillSpawn = map.KillSpawns.FirstOrDefault(x => x.TargetMobs.Any(x => x.TargetMobType == mob.Type));

            if (targetKillSpawn != null)
            {
                mob.SetAwaitingKillSpawn();

                foreach (var targetMob in targetKillSpawn.TargetMobs.Where(x => x.TargetMobType == mob.Type).ToList())
                {
                    if (!map.Mobs.Exists(x => x.Type == targetMob.TargetMobType && !x.AwaitingKillSpawn))
                    {
                        targetKillSpawn.DecreaseTempMobs(targetMob);
                        targetKillSpawn.ResetCurrentSourceMobAmount();

                        map.BroadcastForMap(new KillSpawnEndChatNotifyPacket(targetMob.TargetMobType).Serialize());
                    }
                }
            }
        }

        private static void SourceKillSpawn(GameMap map, MobConfigModel mob)
        {
            var sourceMobKillSpawn = map.KillSpawns.FirstOrDefault(ks => ks.SourceMobs.Any(sm => sm.SourceMobType == mob.Type));

            if (sourceMobKillSpawn == null)
                return;

            var sourceKillSpawn = sourceMobKillSpawn.SourceMobs.FirstOrDefault(x => x.SourceMobType == mob.Type);

            if (sourceKillSpawn != null && sourceKillSpawn.CurrentSourceMobRequiredAmount <= sourceKillSpawn.SourceMobRequiredAmount)
            {
                sourceKillSpawn.DecreaseCurrentSourceMobAmount();

                if (sourceMobKillSpawn.ShowOnMinimap && sourceKillSpawn.CurrentSourceMobRequiredAmount <= 10)
                {

                    map.BroadcastForMap(new KillSpawnMinimapNotifyPacket(sourceKillSpawn.SourceMobType, sourceKillSpawn.CurrentSourceMobRequiredAmount).Serialize());

                }

                if (sourceMobKillSpawn.Spawn())
                {
                    foreach (var targetMob in sourceMobKillSpawn.TargetMobs)
                    {
                        //TODO: para todos os canais (apenas do mapa)
                        map.BroadcastForMap(new KillSpawnChatNotifyPacket(map.MapId, map.Channel, targetMob.TargetMobType).Serialize());

                        map.Mobs.Where(x => x.Type == targetMob.TargetMobType)?.ToList().ForEach(targetMob =>
                        {
                            targetMob.SetRespawn(true);
                            targetMob.SetAwaitingKillSpawn(false);
                        });
                    }
                }
            }
        }

        private void QuestKillReward(GameMap map, MobConfigModel mob)
        {
            var partyIdList = new List<int>();

            foreach (var tamer in mob.TargetTamers)
            {
                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == tamer?.Id);
                if (targetClient == null)
                    continue;

                var giveUpList = new List<short>();

                foreach (var questInProgress in tamer.Progress.InProgressQuestData)
                {
                    var questInfo = _assets.Quest.FirstOrDefault(x => x.QuestId == questInProgress.QuestId);
                    if (questInfo != null)
                    {
                        if (!questInfo.QuestGoals.Exists(x => x.GoalType == QuestGoalTypeEnum.KillMonster))
                            continue;

                        var goalIndex = -1;
                        foreach (var questGoal in questInfo.QuestGoals)
                        {
                            if (questGoal.GoalId == mob?.Type)
                            {
                                goalIndex = questInfo.QuestGoals.FindIndex(x => x == questGoal);
                                break;
                            }
                        }

                        if (goalIndex != -1)
                        {
                            var currentGoalValue = tamer.Progress.GetQuestGoalProgress(questInProgress.QuestId, goalIndex);
                            if (currentGoalValue < questInfo.QuestGoals[goalIndex].GoalAmount)
                            {
                                currentGoalValue++;
                                tamer.Progress.UpdateQuestInProgress(questInProgress.QuestId, goalIndex, currentGoalValue);

                                targetClient.Send(new QuestGoalUpdatePacket(questInProgress.QuestId, (byte)goalIndex, currentGoalValue));
                                var questToUpdate = targetClient.Tamer.Progress.InProgressQuestData.FirstOrDefault(x => x.QuestId == questInProgress.QuestId);
                                _sender.Send(new UpdateCharacterInProgressCommand(questToUpdate));
                            }
                        }
                    }
                    else
                    {
                        _logger.Error($"Unknown quest id {questInProgress.QuestId}.");
                        targetClient.Send(new SystemMessagePacket($"Unknown quest id {questInProgress.QuestId}."));
                        giveUpList.Add(questInProgress.QuestId);
                    }
                }

                giveUpList.ForEach(giveUp =>
                {
                    tamer.Progress.RemoveQuest(giveUp);
                });

                var party = _partyManager.FindParty(targetClient.TamerId);
                if (party != null && !partyIdList.Contains(party.Id))
                {
                    partyIdList.Add(party.Id);

                    foreach (var partyMemberId in party.Members.Values.Select(x => x.Id))
                    {
                        var partyMemberClient = map.Clients.FirstOrDefault(x => x.TamerId == partyMemberId);
                        if (partyMemberClient == null || partyMemberId == targetClient.TamerId)
                            continue;

                        giveUpList = new List<short>();

                        foreach (var questInProgress in partyMemberClient.Tamer.Progress.InProgressQuestData)
                        {
                            var questInfo = _assets.Quest.FirstOrDefault(x => x.QuestId == questInProgress.QuestId);
                            if (questInfo != null)
                            {
                                if (!questInfo.QuestGoals.Exists(x => x.GoalType == QuestGoalTypeEnum.KillMonster))
                                    continue;

                                var goalIndex = -1;
                                foreach (var questGoal in questInfo.QuestGoals)
                                {
                                    if (questGoal.GoalId == mob?.Type)
                                    {
                                        goalIndex = questInfo.QuestGoals.FindIndex(x => x == questGoal);
                                        break;
                                    }
                                }

                                if (goalIndex != -1)
                                {
                                    var currentGoalValue = partyMemberClient.Tamer.Progress.GetQuestGoalProgress(questInProgress.QuestId, goalIndex);
                                    if (currentGoalValue < questInfo.QuestGoals[goalIndex].GoalAmount)
                                    {
                                        currentGoalValue++;
                                        partyMemberClient.Tamer.Progress.UpdateQuestInProgress(questInProgress.QuestId, goalIndex, currentGoalValue);

                                        partyMemberClient.Send(new QuestGoalUpdatePacket(questInProgress.QuestId, (byte)goalIndex, currentGoalValue));
                                        var questToUpdate = partyMemberClient.Tamer.Progress.InProgressQuestData.FirstOrDefault(x => x.QuestId == questInProgress.QuestId);
                                        _sender.Send(new UpdateCharacterInProgressCommand(questToUpdate));
                                    }
                                }
                            }
                            else
                            {
                                _logger.Error($"Unknown quest id {questInProgress.QuestId}.");
                                partyMemberClient.Send(new SystemMessagePacket($"Unknown quest id {questInProgress.QuestId}."));
                                giveUpList.Add(questInProgress.QuestId);
                            }
                        }

                        giveUpList.ForEach(giveUp =>
                        {
                            partyMemberClient.Tamer.Progress.RemoveQuest(giveUp);
                        });
                    }
                }
            }

            partyIdList.Clear();
        }

        private void ItemsReward(GameMap map, MobConfigModel mob)
        {
            if (mob.DropReward == null)
                return;

            QuestDropReward(map, mob);

            if (mob.Class == 8)
                RaidReward(map, mob);
            else
                DropReward(map, mob);
        }

        private void ExperienceReward(GameMap map, MobConfigModel mob)
        {
            if (mob.ExpReward == null)
                return;

            var partyIdList = new List<int>();

            foreach (var tamer in mob.TargetTamers)
            {
                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == tamer?.Id);
                if (targetClient == null)
                    continue;
                double expBonusMultiplier = tamer.BonusEXP / 100.0 + targetClient.ServerExperience / 100.0;

                var tamerExpToReceive = (long)(CalculateExperience(tamer.Partner.Level, mob.Level, mob.ExpReward.TamerExperience) * expBonusMultiplier); //TODO: +bonus

                if (CalculateExperience(tamer.Partner.Level, mob.Level, mob.ExpReward.TamerExperience) == 0)
                    tamerExpToReceive = 0;

                if (tamerExpToReceive > 100) tamerExpToReceive += UtilitiesFunctions.RandomInt(-15, 15);
                var tamerResult = ReceiveTamerExp(targetClient.Tamer, tamerExpToReceive);

                var partnerExpToReceive = (long)(CalculateExperience(tamer.Partner.Level, mob.Level, mob.ExpReward.DigimonExperience) * expBonusMultiplier); //TODO: +bonus

                if (CalculateExperience(tamer.Partner.Level, mob.Level, mob.ExpReward.DigimonExperience) == 0)
                    partnerExpToReceive = 0;

                if (partnerExpToReceive > 100) partnerExpToReceive += UtilitiesFunctions.RandomInt(-15, 15);
                var partnerResult = ReceivePartnerExp(targetClient.Partner, mob, partnerExpToReceive);

                targetClient.Send(
                    new ReceiveExpPacket(
                        tamerExpToReceive,
                        0,//TODO: obter os bonus
                        targetClient.Tamer.CurrentExperience,
                        targetClient.Partner.GeneralHandler,
                        partnerExpToReceive,
                        0,//TODO: obter os bonus
                        targetClient.Partner.CurrentExperience,
                        targetClient.Partner.CurrentEvolution.SkillExperience
                    )
                );

                //TODO: importar o DMBase e tratar isso
                SkillExpReward(map, targetClient);

                if (tamerResult.LevelGain > 0 || partnerResult.LevelGain > 0)
                {
                    targetClient.Send(new UpdateStatusPacket(targetClient.Tamer));

                    map.BroadcastForTamerViewsAndSelf(targetClient.TamerId,
                        new UpdateMovementSpeedPacket(targetClient.Tamer).Serialize());
                }

                _sender.Send(new UpdateCharacterExperienceCommand(tamer));
                _sender.Send(new UpdateDigimonExperienceCommand(tamer.Partner));

                PartyExperienceReward(map, mob, partyIdList, targetClient, ref tamerExpToReceive, ref tamerResult, ref partnerExpToReceive, ref partnerResult);
            }

            partyIdList.Clear();
        }

        public long CalculateExperience(int tamerLevel, int mobLevel, long baseExperience)
        {
            int levelDifference = tamerLevel - mobLevel; // Invertido para verificar se o Tamer está 30 níveis acima do Mob

            if (levelDifference <= 30)
            {
                if (levelDifference > 0)
                {
                    return (long)(baseExperience * (1.0 - levelDifference * 0.03)); // 0.03 é o redutor por nível (3%)
                }
                // Se a diferença for 0 ou negativa, o Tamer não perde experiência.
            }
            else
            {
                return 0; // Se a diferença de níveis for maior que 30, o Tamer não recebe experiência
            }

            return baseExperience; // Se não houver redutor, a experiência base é mantida
        }


        private void SkillExpReward(GameMap map, GameClient? targetClient)
        {


            var ExpNeed = int.MaxValue;
            var evolutionType = _assets.DigimonBaseInfo.First(x => x.Type == targetClient.Partner.CurrentEvolution.Type).EvolutionType;


            ExpNeed = SkillExperienceTable(evolutionType, targetClient.Partner.CurrentEvolution.SkillMastery);

            if (targetClient.Partner.CurrentEvolution.SkillExperience >= ExpNeed)
            {
                targetClient.Partner.ReceiveSkillPoint();

                var evolutionIndex = targetClient.Partner.Evolutions.IndexOf(targetClient.Partner.CurrentEvolution);

                //TODO: Receive skill point packet
                var packet = new PacketWriter();
                packet.Type(1105);
                packet.WriteInt(targetClient.Partner.GeneralHandler);
                packet.WriteByte((byte)(evolutionIndex + 1));
                packet.WriteByte(targetClient.Partner.CurrentEvolution.SkillPoints);
                packet.WriteByte(targetClient.Partner.CurrentEvolution.SkillMastery);
                packet.WriteInt(targetClient.Partner.CurrentEvolution.SkillExperience);

                map.BroadcastForTamerViewsAndSelf(targetClient.TamerId, packet.Serialize());
            }
        }

        private void PartyExperienceReward(
            GameMap map,
            MobConfigModel mob,
            List<int> partyIdList,
            GameClient? targetClient,
            ref long tamerExpToReceive,
            ref ReceiveExpResult tamerResult,
            ref long partnerExpToReceive,
            ref ReceiveExpResult partnerResult)
        {
            var party = _partyManager.FindParty(targetClient.TamerId);
            if (party != null && !partyIdList.Contains(party.Id))
            {
                partyIdList.Add(party.Id);

                foreach (var partyMemberId in party.Members.Values.Select(x => x.Id))
                {
                    var partyMemberClient = map.Clients.FirstOrDefault(x => x.TamerId == partyMemberId);
                    if (partyMemberClient == null || partyMemberId == targetClient.TamerId)
                        continue;

                    tamerExpToReceive = (long)((double)(mob.ExpReward.TamerExperience * 0.80)); //TODO: +bonus
                    if (tamerExpToReceive > 100) tamerExpToReceive += UtilitiesFunctions.RandomInt(-15, 15);
                    tamerResult = ReceiveTamerExp(partyMemberClient.Tamer, tamerExpToReceive);

                    partnerExpToReceive = (long)((double)(mob.ExpReward.DigimonExperience) * 0.80); //TODO: +bonus
                    if (partnerExpToReceive > 100) partnerExpToReceive += UtilitiesFunctions.RandomInt(-15, 15);
                    partnerResult = ReceivePartnerExp(partyMemberClient.Partner, mob, partnerExpToReceive);

                    partyMemberClient.Send(
                        new PartyReceiveExpPacket(
                            tamerExpToReceive,
                            0,//TODO: obter os bonus
                            partyMemberClient.Tamer.CurrentExperience,
                            partyMemberClient.Partner.GeneralHandler,
                            partnerExpToReceive,
                            0,//TODO: obter os bonus
                            partyMemberClient.Partner.CurrentExperience,
                            partyMemberClient.Partner.CurrentEvolution.SkillExperience,
                            targetClient.Tamer.Name
                        ));

                    if (tamerResult.LevelGain > 0 || partnerResult.LevelGain > 0)
                    {
                        targetClient.Send(new UpdateStatusPacket(targetClient.Tamer));

                        map.BroadcastForTamerViewsAndSelf(targetClient.TamerId,
                            new UpdateMovementSpeedPacket(targetClient.Tamer).Serialize());
                    }

                    _sender.Send(new UpdateCharacterExperienceCommand(partyMemberClient.Tamer));
                    _sender.Send(new UpdateDigimonExperienceCommand(partyMemberClient.Partner));
                }
            }
        }

        private void PartyExperienceReward(
          GameMap map,
          SummonMobModel mob,
          List<int> partyIdList,
          GameClient? targetClient,
          ref long tamerExpToReceive,
          ref ReceiveExpResult tamerResult,
          ref long partnerExpToReceive,
          ref ReceiveExpResult partnerResult)
        {
            var party = _partyManager.FindParty(targetClient.TamerId);
            if (party != null && !partyIdList.Contains(party.Id))
            {
                partyIdList.Add(party.Id);

                foreach (var partyMemberId in party.Members.Values.Select(x => x.Id))
                {
                    var partyMemberClient = map.Clients.FirstOrDefault(x => x.TamerId == partyMemberId);
                    if (partyMemberClient == null || partyMemberId == targetClient.TamerId)
                        continue;

                    tamerExpToReceive = (long)((double)(mob.ExpReward.TamerExperience * 0.80)); //TODO: +bonus
                    if (tamerExpToReceive > 100) tamerExpToReceive += UtilitiesFunctions.RandomInt(-15, 15);
                    tamerResult = ReceiveTamerExp(partyMemberClient.Tamer, tamerExpToReceive);

                    partnerExpToReceive = (long)((double)(mob.ExpReward.DigimonExperience) * 0.80); //TODO: +bonus
                    if (partnerExpToReceive > 100) partnerExpToReceive += UtilitiesFunctions.RandomInt(-15, 15);
                    partnerResult = ReceivePartnerExp(partyMemberClient.Partner, mob, partnerExpToReceive);

                    partyMemberClient.Send(
                        new PartyReceiveExpPacket(
                            tamerExpToReceive,
                            0,//TODO: obter os bonus
                            partyMemberClient.Tamer.CurrentExperience,
                            partyMemberClient.Partner.GeneralHandler,
                            partnerExpToReceive,
                            0,//TODO: obter os bonus
                            partyMemberClient.Partner.CurrentExperience,
                            partyMemberClient.Partner.CurrentEvolution.SkillExperience,
                            targetClient.Tamer.Name
                        ));

                    if (tamerResult.LevelGain > 0 || partnerResult.LevelGain > 0)
                    {
                        targetClient.Send(new UpdateStatusPacket(targetClient.Tamer));

                        map.BroadcastForTamerViewsAndSelf(targetClient.TamerId,
                            new UpdateMovementSpeedPacket(targetClient.Tamer).Serialize());
                    }

                    _sender.Send(new UpdateCharacterExperienceCommand(partyMemberClient.Tamer));
                    _sender.Send(new UpdateDigimonExperienceCommand(partyMemberClient.Partner));
                }
            }
        }
        private void DropReward(GameMap map, MobConfigModel mob)
        {
            var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == mob.TargetTamer?.Id);
            if (targetClient == null)
                return;

            BitDropReward(map, mob, targetClient);

            ItemDropReward(map, mob, targetClient);
        }

        private void BitDropReward(GameMap map, MobConfigModel mob, GameClient? targetClient)
        {
            var bitsReward = mob.DropReward.BitsDrop;

            if (bitsReward != null && bitsReward.Chance >= UtilitiesFunctions.RandomDouble())
            {
                if (targetClient.Tamer.HasAura && targetClient.Tamer.Aura.ItemInfo.Section == 2100)
                {
                    var amount = UtilitiesFunctions.RandomInt(bitsReward.MinAmount, bitsReward.MaxAmount);

                    targetClient.Send(
                        new PickBitsPacket(
                            targetClient.Tamer.GeneralHandler,
                            amount
                        )
                    );

                    targetClient.Tamer.Inventory.AddBits(amount);
                    _sender.Send(new UpdateItemListBitsCommand(targetClient.Tamer.Inventory));
                    _sender.Send(new UpdateItemsCommand(targetClient.Tamer.Inventory));
                    _logger.Verbose($"Character {targetClient.TamerId} aquired {amount} bits from mob {mob.Id} with magnetic aura {targetClient.Tamer.Aura.ItemId}.");
                }
                else
                {
                    var drop = _dropManager.CreateBitDrop(
                        targetClient.TamerId,
                        targetClient.Tamer.GeneralHandler,
                        bitsReward.MinAmount,
                        bitsReward.MaxAmount,
                        mob.CurrentLocation.MapId,
                        mob.CurrentLocation.X,
                        mob.CurrentLocation.Y
                    );

                    map.AddMapDrop(drop);
                }
            }
        }

        private void ItemDropReward(GameMap map, MobConfigModel mob, GameClient? targetClient)
        {
            if (!mob.DropReward.Drops.Any())
                return;

            var itemsReward = new List<ItemDropConfigModel>();
            itemsReward.AddRange(mob.DropReward.Drops);
            itemsReward.RemoveAll(x => _assets.QuestItemList.Contains(x.ItemId));

            if (!itemsReward.Any())
                return;

            var dropped = 0;
            var totalDrops = UtilitiesFunctions.RandomInt(
                mob.DropReward.MinAmount,
                mob.DropReward.MaxAmount);

            while (dropped < totalDrops)
            {
                if (!itemsReward.Any())
                {
                    _logger.Warning($"Mob {mob.Id} has incorrect drops configuration.");
                    break;
                }

                var possibleDrops = itemsReward.OrderBy(x => Guid.NewGuid()).ToList();
                foreach (var itemDrop in possibleDrops)
                {
                    if (itemDrop.Chance >= UtilitiesFunctions.RandomDouble())
                    {
                        if (targetClient.Tamer.HasAura && targetClient.Tamer.Aura.ItemInfo.Section == 2100)
                        {
                            var newItem = new ItemModel();
                            newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == itemDrop.ItemId));

                            if (newItem.ItemInfo == null)
                            {
                                _logger.Warning($"No item info found with ID {itemDrop.ItemId} for tamer {targetClient.Tamer.Id}.");
                                targetClient.Send(new SystemMessagePacket($"No item info found with ID {itemDrop.ItemId}."));
                                continue;
                            }

                            newItem.ItemId = itemDrop.ItemId;
                            newItem.Amount = UtilitiesFunctions.RandomInt(itemDrop.MinAmount, itemDrop.MaxAmount);

                            var itemClone = (ItemModel)newItem.Clone();
                            if (targetClient.Tamer.Inventory.AddItem(newItem))
                            {
                                targetClient.Send(new ReceiveItemPacket(itemClone, InventoryTypeEnum.Inventory));
                                _sender.Send(new UpdateItemsCommand(targetClient.Tamer.Inventory));
                                _logger.Verbose($"Character {targetClient.TamerId} aquired {newItem.ItemId} x{newItem.Amount} from " +
                                    $"mob {mob.Id} with magnetic aura {targetClient.Tamer.Aura.ItemId}.");
                            }
                            else
                            {
                                targetClient.Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));

                                var drop = _dropManager.CreateItemDrop(
                                    targetClient.Tamer.Id,
                                    targetClient.Tamer.GeneralHandler,
                                    itemDrop.ItemId,
                                    itemDrop.MinAmount,
                                    itemDrop.MaxAmount,
                                    mob.CurrentLocation.MapId,
                                    mob.CurrentLocation.X,
                                    mob.CurrentLocation.Y
                                );

                                map.AddMapDrop(drop);
                            }

                            dropped++;
                        }
                        else
                        {
                            var drop = _dropManager.CreateItemDrop(
                                targetClient.Tamer.Id,
                                targetClient.Tamer.GeneralHandler,
                                itemDrop.ItemId,
                                itemDrop.MinAmount,
                                itemDrop.MaxAmount,
                                mob.CurrentLocation.MapId,
                                mob.CurrentLocation.X,
                                mob.CurrentLocation.Y
                            );

                            dropped++;

                            map.AddMapDrop(drop);
                        }

                        itemsReward.RemoveAll(x => x.Id == itemDrop.Id);
                        break;
                    }
                }
            }
        }

        private void QuestDropReward(GameMap map, MobConfigModel mob)
        {
            var itemsReward = new List<ItemDropConfigModel>();
            itemsReward.AddRange(mob.DropReward.Drops);
            itemsReward.RemoveAll(x => !_assets.QuestItemList.Contains(x.ItemId));

            if (!itemsReward.Any())
                return;

            foreach (var tamer in mob.TargetTamers)
            {
                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == tamer?.Id);
                if (targetClient == null)
                    continue;

                if (!tamer.Progress.InProgressQuestData.Any())
                    continue;

                var updateItemList = false;
                var possibleDrops = itemsReward.Randomize();
                foreach (var itemDrop in possibleDrops)
                {
                    if (itemDrop.Chance >= UtilitiesFunctions.RandomDouble())
                    {
                        foreach (var questInProgress in tamer.Progress.InProgressQuestData)
                        {
                            var questInfo = _assets.Quest.FirstOrDefault(x => x.QuestId == questInProgress.QuestId);
                            if (questInfo != null)
                            {
                                if (!questInfo.QuestGoals.Exists(x => x.GoalType == QuestGoalTypeEnum.LootItem))
                                    continue;

                                var goalIndex = -1;
                                foreach (var questGoal in questInfo.QuestGoals)
                                {
                                    if (questGoal.GoalId == itemDrop?.ItemId)
                                    {
                                        var inventoryItems = tamer.Inventory.FindItemsById(questGoal.GoalId);
                                        var goalAmount = questGoal.GoalAmount;

                                        foreach (var inventoryItem in inventoryItems)
                                        {
                                            goalAmount -= inventoryItem.Amount;
                                            if (goalAmount <= 0)
                                            {
                                                goalAmount = 0;
                                                break;
                                            }
                                        }

                                        if (goalAmount > 0)
                                        {
                                            goalIndex = questInfo.QuestGoals.FindIndex(x => x == questGoal);
                                            break;
                                        }
                                    }
                                }

                                if (goalIndex != -1)
                                {
                                    var newItem = new ItemModel();
                                    newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == itemDrop.ItemId));

                                    if (newItem.ItemInfo == null)
                                    {
                                        _logger.Warning($"No item info found with ID {itemDrop.ItemId} for tamer {tamer.Id}.");
                                        targetClient.Send(new SystemMessagePacket($"No item info found with ID {itemDrop.ItemId}."));
                                        continue;
                                    }

                                    newItem.ItemId = itemDrop.ItemId;
                                    newItem.Amount = UtilitiesFunctions.RandomInt(itemDrop.MinAmount, itemDrop.MaxAmount);

                                    var itemClone = (ItemModel)newItem.Clone();
                                    if (tamer.Inventory.AddItem(newItem))
                                    {
                                        updateItemList = true;
                                        targetClient.Send(new ReceiveItemPacket(itemClone, InventoryTypeEnum.Inventory));
                                    }
                                    else
                                    {
                                        targetClient.Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));
                                    }
                                }
                            }
                            else
                            {
                                _logger.Error($"Unknown quest id {questInProgress.QuestId}.");
                                targetClient.Send(new SystemMessagePacket($"Unknown quest id {questInProgress.QuestId}."));
                            }
                        }

                        if (updateItemList) _sender.Send(new UpdateItemsCommand(tamer.Inventory));

                        itemsReward.RemoveAll(x => x.Id == itemDrop.Id);
                    }
                }
            }
        }

        private void RaidReward(GameMap map, MobConfigModel mob)
        {
            var raidResult = mob.RaidDamage.Where(x => x.Key > 0).DistinctBy(x => x.Key);

            var writer = new PacketWriter();
            writer.Type(1604);
            writer.WriteInt(raidResult.Count());

            int i = 1;

            var updateItemList = new List<ItemListModel>();

            foreach (var raidTamer in raidResult.OrderByDescending(x => x.Value))
            {
                _logger.Verbose($"Character {raidTamer.Key} rank {i} on raid {mob.Id} - {mob.Name} with damage {raidTamer.Value}.");

                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == raidTamer.Key);

                if (i <= 10)
                {
                    writer.WriteInt(i);
                    writer.WriteString(targetClient?.Tamer?.Name ?? $"Tamer{i}");
                    writer.WriteString(targetClient?.Partner?.Name ?? $"Partner{i}");
                    writer.WriteInt(raidTamer.Value);
                }

                var bitsReward = mob.DropReward.BitsDrop;
                if (targetClient != null && bitsReward != null && bitsReward.Chance >= UtilitiesFunctions.RandomDouble())
                {
                    var drop = _dropManager.CreateBitDrop(
                        targetClient.Tamer.Id,
                        targetClient.Tamer.GeneralHandler,
                        bitsReward.MinAmount,
                        bitsReward.MaxAmount,
                        mob.CurrentLocation.MapId,
                        mob.CurrentLocation.X,
                        mob.CurrentLocation.Y
                    );

                    map.DropsToAdd.Add(drop);
                }

                var raidRewards = mob.DropReward.Drops;
                raidRewards.RemoveAll(x => _assets.QuestItemList.Contains(x.ItemId));

                if (targetClient != null && raidRewards != null && raidRewards.Any())
                {
                    var rewards = raidRewards.Where(x => x.Rank == i);

                    if (rewards == null || !rewards.Any())
                        rewards = raidRewards.Where(x => x.Rank == raidRewards.Max(x => x.Rank));

                    foreach (var reward in rewards)
                    {
                        if (reward.Chance >= UtilitiesFunctions.RandomDouble())
                        {
                            var newItem = new ItemModel();
                            newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == reward.ItemId));

                            if (newItem.ItemInfo == null)
                            {
                                _logger.Warning($"No item info found with ID {reward.ItemId} for tamer {targetClient.TamerId}.");
                                targetClient.Send(new SystemMessagePacket($"No item info found with ID {reward.ItemId}."));
                                break;
                            }

                            newItem.ItemId = reward.ItemId;
                            newItem.Amount = UtilitiesFunctions.RandomInt(reward.MinAmount, reward.MaxAmount);

                            if (newItem.IsTemporary)
                                newItem.SetRemainingTime((uint)newItem.ItemInfo.UsageTimeMinutes);

                            var itemClone = (ItemModel)newItem.Clone();
                            if (targetClient.Tamer.Inventory.AddItem(newItem))
                            {
                                targetClient.Send(new ReceiveItemPacket(itemClone, InventoryTypeEnum.Inventory));
                                updateItemList.Add(targetClient.Tamer.Inventory);
                            }
                            else
                            {
                                targetClient.Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));
                                targetClient.Tamer.GiftWarehouse.AddItem(newItem);
                                updateItemList.Add(targetClient.Tamer.GiftWarehouse);
                            }
                        }
                    }
                }

                i++;
            }

            map.BroadcastForTargetTamers(mob.RaidDamage.Select(x => x.Key).ToList(), writer.Serialize());
            updateItemList.ForEach(itemList => { _sender.Send(new UpdateItemsCommand(itemList)); });
        }
        private void RaidReward(GameMap map, SummonMobModel mob)
        {
            var raidResult = mob.RaidDamage.Where(x => x.Key > 0).DistinctBy(x => x.Key);

            var writer = new PacketWriter();
            writer.Type(1604);
            writer.WriteInt(raidResult.Count());

            int i = 1;

            var updateItemList = new List<ItemListModel>();

            foreach (var raidTamer in raidResult.OrderByDescending(x => x.Value))
            {
                _logger.Verbose($"Character {raidTamer.Key} rank {i} on raid {mob.Id} - {mob.Name} with damage {raidTamer.Value}.");

                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == raidTamer.Key);

                if (i <= 10)
                {
                    writer.WriteInt(i);
                    writer.WriteString(targetClient?.Tamer?.Name ?? $"Tamer{i}");
                    writer.WriteString(targetClient?.Partner?.Name ?? $"Partner{i}");
                    writer.WriteInt(raidTamer.Value);
                }

                var bitsReward = mob.DropReward.BitsDrop;
                if (targetClient != null && bitsReward != null && bitsReward.Chance >= UtilitiesFunctions.RandomDouble())
                {
                    var drop = _dropManager.CreateBitDrop(
                        targetClient.Tamer.Id,
                        targetClient.Tamer.GeneralHandler,
                        bitsReward.MinAmount,
                        bitsReward.MaxAmount,
                        mob.CurrentLocation.MapId,
                        mob.CurrentLocation.X,
                        mob.CurrentLocation.Y
                    );

                    map.DropsToAdd.Add(drop);
                }

                var raidRewards = mob.DropReward.Drops;
                raidRewards.RemoveAll(x => _assets.QuestItemList.Contains(x.ItemId));

                if (targetClient != null && raidRewards != null && raidRewards.Any())
                {
                    foreach (var reward in raidRewards)
                    {
                        if (reward.Chance >= UtilitiesFunctions.RandomDouble())
                        {
                            var newItem = new ItemModel();
                            newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == reward.ItemId));

                            if (newItem.ItemInfo == null)
                            {
                                _logger.Warning($"No item info found with ID {reward.ItemId} for tamer {targetClient.TamerId}.");
                                targetClient.Send(new SystemMessagePacket($"No item info found with ID {reward.ItemId}."));
                                continue; // Continue para a próxima recompensa se não houver informações sobre o item.
                            }

                            newItem.ItemId = reward.ItemId;
                            newItem.Amount = UtilitiesFunctions.RandomInt(reward.MinAmount, reward.MaxAmount);

                            if (newItem.IsTemporary)
                                newItem.SetRemainingTime((uint)newItem.ItemInfo.UsageTimeMinutes);

                            var itemClone = (ItemModel)newItem.Clone();
                            if (targetClient.Tamer.Inventory.AddItem(newItem))
                            {
                                targetClient.Send(new ReceiveItemPacket(itemClone, InventoryTypeEnum.Inventory));
                                updateItemList.Add(targetClient.Tamer.Inventory);
                            }
                            else
                            {
                                targetClient.Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));
                                targetClient.Tamer.GiftWarehouse.AddItem(newItem);
                                updateItemList.Add(targetClient.Tamer.GiftWarehouse);
                            }
                        }
                    }
                }


                i++;
            }

            map.BroadcastForTargetTamers(mob.RaidDamage.Select(x => x.Key).ToList(), writer.Serialize());
            updateItemList.ForEach(itemList => { _sender.Send(new UpdateItemsCommand(itemList)); });
        }

        private void MobsOperation(GameMap map, SummonMobModel mob)
        {

            switch (mob.CurrentAction)
            {
                case MobActionEnum.Respawn:
                    {

                        mob.Reset();
                        mob.ResetLocation();
                    }
                    break;

                case MobActionEnum.Reward:
                    {
                        ItemsReward(map, mob);
                        QuestKillReward(map, mob);
                        ExperienceReward(map, mob);
                    }
                    break;

                case MobActionEnum.Wait:
                    {

                        if (mob.Respawn && DateTime.Now > mob.DieTime.AddSeconds(2))
                        {

                            mob.SetNextWalkTime(UtilitiesFunctions.RandomInt(7, 14));
                            mob.SetAgressiveCheckTime(5);
                            mob.SetRespawn();
                        }
                        else
                        {
                            map.AttackNearbyTamer(mob, mob.TamersViewing,_assets.NpcColiseum);
                        }
                    }
                    break;

                case MobActionEnum.Walk:
                    {
                        map.BroadcastForTargetTamers(mob.TamersViewing, new SyncConditionPacket(mob.GeneralHandler, ConditionEnum.Default).Serialize());
                        mob.Move();
                        map.BroadcastForTargetTamers(mob.TamersViewing, new MobWalkPacket(mob).Serialize());
                    }
                    break;

                case MobActionEnum.GiveUp:
                    {
                        map.BroadcastForTargetTamers(mob.TamersViewing, new SyncConditionPacket(mob.GeneralHandler, ConditionEnum.Immortal).Serialize());
                        mob.ResetLocation();
                        map.BroadcastForTargetTamers(mob.TamersViewing, new MobRunPacket(mob).Serialize());
                        map.BroadcastForTargetTamers(mob.TamersViewing, new SetCombatOffPacket(mob.GeneralHandler).Serialize());

                        foreach (var targetTamer in mob.TargetTamers)
                        {
                            if (targetTamer.TargetMobs.Count <= 1)
                            {
                                targetTamer.StopBattle(true);
                                map.BroadcastForTamerViewsAndSelf(targetTamer.Id, new SetCombatOffPacket(targetTamer.Partner.GeneralHandler).Serialize());
                            }
                        }

                        mob.Reset(true);
                        map.BroadcastForTargetTamers(mob.TamersViewing, new UpdateCurrentHPRatePacket(mob.GeneralHandler, mob.CurrentHpRate).Serialize());
                    }
                    break;

                case MobActionEnum.Attack:
                    {
                        if (!mob.Dead && mob.SkillTime && !mob.CheckSkill && mob.IsPossibleSkill)
                        {
                            mob.UpdateCurrentAction(MobActionEnum.UseAttackSkill);
                            mob.SetNextAction();
                            break;
                        }

                        if (!mob.Dead && ((mob.TargetTamer == null || mob.TargetTamer.Hidden) || DateTime.Now > mob.LastHitTryTime.AddSeconds(15))) //Anti-kite
                        {
                            mob.GiveUp();
                            break;
                        }

                        if (!mob.Dead && !mob.Chasing && mob.TargetAlive)
                        {
                            var diff = UtilitiesFunctions.CalculateDistance(
                                mob.CurrentLocation.X,
                                mob.Target.Location.X,
                                mob.CurrentLocation.Y,
                                mob.Target.Location.Y);

                            var range = Math.Max(mob.ARValue, mob.Target.BaseInfo.ARValue);
                            if (diff <= range)
                            {
                                if (DateTime.Now < mob.LastHitTime.AddMilliseconds(mob.ASValue))
                                    break;

                                var missed = false;

                                if (mob.TargetTamer != null && mob.TargetTamer.GodMode)
                                    missed = true;
                                else if (mob.CanMissHit())
                                    missed = true;

                                if (missed)
                                {
                                    mob.UpdateLastHitTry();
                                    map.BroadcastForTargetTamers(mob.TamersViewing, new MissHitPacket(mob.GeneralHandler, mob.TargetHandler).Serialize());
                                    mob.UpdateLastHit();
                                    break;
                                }

                                map.AttackTarget(mob);
                            }
                            else
                            {
                                map.ChaseTarget(mob);
                            }
                        }

                        if (mob.Dead)
                        {
                            foreach (var targetTamer in mob.TargetTamers)
                            {

                                targetTamer.StopBattle(true);
                                map.BroadcastForTamerViewsAndSelf(targetTamer.Id, new SetCombatOffPacket(targetTamer.Partner.GeneralHandler).Serialize());

                            }
                        }
                    }
                    break;

                case MobActionEnum.UseAttackSkill:
                    {
                        if (!mob.Dead && ((mob.TargetTamer == null || mob.TargetTamer.Hidden))) //Anti-kite
                        {
                            mob.GiveUp();
                            break;
                        }

                        var skillList = _assets.MonsterSkillInfo.Where(x => x.Type == mob.Type).ToList();

                        if (!skillList.Any())
                        {
                            mob.UpdateCheckSkill(true);
                            mob.UpdateCurrentAction(MobActionEnum.Wait);
                            mob.UpdateLastSkill();
                            mob.UpdateLastSkillTry();
                            mob.SetNextAction();
                            break;
                        }

                        Random random = new Random();

                        var targetSkill = skillList[random.Next(0, skillList.Count)];

                        if (!mob.Dead && !mob.Chasing && mob.TargetAlive)
                        {
                            var diff = UtilitiesFunctions.CalculateDistance(
                               mob.CurrentLocation.X,
                               mob.Target.Location.X,
                               mob.CurrentLocation.Y,
                               mob.Target.Location.Y);

                            if (diff <= 1900)
                            {
                                if (DateTime.Now < mob.LastSkillTime.AddMilliseconds(mob.Cooldown) && mob.Cooldown > 0)
                                    break;

                                map.SkillTarget(mob, targetSkill);



                                if (mob.Target != null)
                                {
                                    mob.UpdateCurrentAction(MobActionEnum.Wait);

                                    mob.SetNextAction();
                                }
                            }
                            else
                            {
                                map.ChaseTarget(mob);
                            }
                        }

                        if (mob.Dead)
                        {
                            foreach (var targetTamer in mob.TargetTamers)
                            {

                                targetTamer.StopBattle(true);
                                map.BroadcastForTamerViewsAndSelf(targetTamer.Id, new SetCombatOffPacket(targetTamer.Partner.GeneralHandler).Serialize());

                            }

                            break;
                        }
                    }
                    break;
            }
        }

        private void QuestKillReward(GameMap map, SummonMobModel mob)
        {
            var partyIdList = new List<int>();

            foreach (var tamer in mob.TargetTamers)
            {
                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == tamer?.Id);
                if (targetClient == null)
                    continue;

                var giveUpList = new List<short>();

                foreach (var questInProgress in tamer.Progress.InProgressQuestData)
                {
                    var questInfo = _assets.Quest.FirstOrDefault(x => x.QuestId == questInProgress.QuestId);
                    if (questInfo != null)
                    {
                        if (!questInfo.QuestGoals.Exists(x => x.GoalType == QuestGoalTypeEnum.KillMonster))
                            continue;

                        var goalIndex = -1;
                        foreach (var questGoal in questInfo.QuestGoals)
                        {
                            if (questGoal.GoalId == mob?.Type)
                            {
                                goalIndex = questInfo.QuestGoals.FindIndex(x => x == questGoal);
                                break;
                            }
                        }

                        if (goalIndex != -1)
                        {
                            var currentGoalValue = tamer.Progress.GetQuestGoalProgress(questInProgress.QuestId, goalIndex);
                            if (currentGoalValue < questInfo.QuestGoals[goalIndex].GoalAmount)
                            {
                                currentGoalValue++;
                                tamer.Progress.UpdateQuestInProgress(questInProgress.QuestId, goalIndex, currentGoalValue);

                                targetClient.Send(new QuestGoalUpdatePacket(questInProgress.QuestId, (byte)goalIndex, currentGoalValue));
                                var questToUpdate = targetClient.Tamer.Progress.InProgressQuestData.FirstOrDefault(x => x.QuestId == questInProgress.QuestId);
                                _sender.Send(new UpdateCharacterInProgressCommand(questToUpdate));
                            }
                        }
                    }
                    else
                    {
                        _logger.Error($"Unknown quest id {questInProgress.QuestId}.");
                        targetClient.Send(new SystemMessagePacket($"Unknown quest id {questInProgress.QuestId}."));
                        giveUpList.Add(questInProgress.QuestId);
                    }
                }

                giveUpList.ForEach(giveUp =>
                {
                    tamer.Progress.RemoveQuest(giveUp);
                });

                var party = _partyManager.FindParty(targetClient.TamerId);
                if (party != null && !partyIdList.Contains(party.Id))
                {
                    partyIdList.Add(party.Id);

                    foreach (var partyMemberId in party.Members.Values.Select(x => x.Id))
                    {
                        var partyMemberClient = map.Clients.FirstOrDefault(x => x.TamerId == partyMemberId);
                        if (partyMemberClient == null || partyMemberId == targetClient.TamerId)
                            continue;

                        giveUpList = new List<short>();

                        foreach (var questInProgress in partyMemberClient.Tamer.Progress.InProgressQuestData)
                        {
                            var questInfo = _assets.Quest.FirstOrDefault(x => x.QuestId == questInProgress.QuestId);
                            if (questInfo != null)
                            {
                                if (!questInfo.QuestGoals.Exists(x => x.GoalType == QuestGoalTypeEnum.KillMonster))
                                    continue;

                                var goalIndex = -1;
                                foreach (var questGoal in questInfo.QuestGoals)
                                {
                                    if (questGoal.GoalId == mob?.Type)
                                    {
                                        goalIndex = questInfo.QuestGoals.FindIndex(x => x == questGoal);
                                        break;
                                    }
                                }

                                if (goalIndex != -1)
                                {
                                    var currentGoalValue = partyMemberClient.Tamer.Progress.GetQuestGoalProgress(questInProgress.QuestId, goalIndex);
                                    if (currentGoalValue < questInfo.QuestGoals[goalIndex].GoalAmount)
                                    {
                                        currentGoalValue++;
                                        partyMemberClient.Tamer.Progress.UpdateQuestInProgress(questInProgress.QuestId, goalIndex, currentGoalValue);

                                        partyMemberClient.Send(new QuestGoalUpdatePacket(questInProgress.QuestId, (byte)goalIndex, currentGoalValue));
                                        var questToUpdate = partyMemberClient.Tamer.Progress.InProgressQuestData.FirstOrDefault(x => x.QuestId == questInProgress.QuestId);
                                        _sender.Send(new UpdateCharacterInProgressCommand(questToUpdate));
                                    }
                                }
                            }
                            else
                            {
                                _logger.Error($"Unknown quest id {questInProgress.QuestId}.");
                                partyMemberClient.Send(new SystemMessagePacket($"Unknown quest id {questInProgress.QuestId}."));
                                giveUpList.Add(questInProgress.QuestId);
                            }
                        }

                        giveUpList.ForEach(giveUp =>
                        {
                            partyMemberClient.Tamer.Progress.RemoveQuest(giveUp);
                        });
                    }
                }
            }

            partyIdList.Clear();
        }

        private void ItemsReward(GameMap map, SummonMobModel mob)
        {
            if (mob.DropReward == null)
                return;

            QuestDropReward(map, mob);

            if (mob.Class == 8)
                RaidReward(map, mob);
            else
                DropReward(map, mob);
        }

        private void ExperienceReward(GameMap map, SummonMobModel mob)
        {
            if (mob.ExpReward == null)
                return;

            var partyIdList = new List<int>();

            foreach (var tamer in mob.TargetTamers)
            {
                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == tamer?.Id);
                if (targetClient == null)
                    continue;
                double expBonusMultiplier = tamer.BonusEXP / 100.0 + targetClient.ServerExperience / 100.0;

                var tamerExpToReceive = (long)(CalculateExperience(tamer.Partner.Level, mob.Level, mob.ExpReward.TamerExperience) * expBonusMultiplier); //TODO: +bonus

                if (CalculateExperience(tamer.Partner.Level, mob.Level, mob.ExpReward.TamerExperience) == 0)
                    tamerExpToReceive = 0;

                if (tamerExpToReceive > 100) tamerExpToReceive += UtilitiesFunctions.RandomInt(-15, 15);
                var tamerResult = ReceiveTamerExp(targetClient.Tamer, tamerExpToReceive);

                var partnerExpToReceive = (long)(CalculateExperience(tamer.Partner.Level, mob.Level, mob.ExpReward.DigimonExperience) * expBonusMultiplier); //TODO: +bonus

                if (CalculateExperience(tamer.Partner.Level, mob.Level, mob.ExpReward.DigimonExperience) == 0)
                    partnerExpToReceive = 0;

                if (partnerExpToReceive > 100) partnerExpToReceive += UtilitiesFunctions.RandomInt(-15, 15);
                var partnerResult = ReceivePartnerExp(targetClient.Partner, mob, partnerExpToReceive);

                targetClient.Send(
                    new ReceiveExpPacket(
                        tamerExpToReceive,
                        0,//TODO: obter os bonus
                        targetClient.Tamer.CurrentExperience,
                        targetClient.Partner.GeneralHandler,
                        partnerExpToReceive,
                        0,//TODO: obter os bonus
                        targetClient.Partner.CurrentExperience,
                        targetClient.Partner.CurrentEvolution.SkillExperience
                    )
                );

                //TODO: importar o DMBase e tratar isso
                SkillExpReward(map, targetClient);

                if (tamerResult.LevelGain > 0 || partnerResult.LevelGain > 0)
                {
                    targetClient.Send(new UpdateStatusPacket(targetClient.Tamer));

                    map.BroadcastForTamerViewsAndSelf(targetClient.TamerId,
                        new UpdateMovementSpeedPacket(targetClient.Tamer).Serialize());
                }

                _sender.Send(new UpdateCharacterExperienceCommand(tamer));
                _sender.Send(new UpdateDigimonExperienceCommand(tamer.Partner));

                PartyExperienceReward(map, mob, partyIdList, targetClient, ref tamerExpToReceive, ref tamerResult, ref partnerExpToReceive, ref partnerResult);
            }

            partyIdList.Clear();
        }
        private void DropReward(GameMap map, SummonMobModel mob)
        {
            var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == mob.TargetTamer?.Id);
            if (targetClient == null)
                return;

            BitDropReward(map, mob, targetClient);

            ItemDropReward(map, mob, targetClient);
        }

        private void BitDropReward(GameMap map, SummonMobModel mob, GameClient? targetClient)
        {
            var bitsReward = mob.DropReward.BitsDrop;

            if (bitsReward != null && bitsReward.Chance >= UtilitiesFunctions.RandomDouble())
            {
                if (targetClient.Tamer.HasAura && targetClient.Tamer.Aura.ItemInfo.Section == 2100)
                {
                    var amount = UtilitiesFunctions.RandomInt(bitsReward.MinAmount, bitsReward.MaxAmount);

                    targetClient.Send(
                        new PickBitsPacket(
                            targetClient.Tamer.GeneralHandler,
                            amount
                        )
                    );

                    targetClient.Tamer.Inventory.AddBits(amount);
                    _sender.Send(new UpdateItemListBitsCommand(targetClient.Tamer.Inventory));

                    _sender.Send(new UpdateItemsCommand(targetClient.Tamer.Inventory));
                    _logger.Verbose($"Character {targetClient.TamerId} aquired {amount} bits from mob {mob.Id} with magnetic aura {targetClient.Tamer.Aura.ItemId}.");
                }
                else
                {
                    var drop = _dropManager.CreateBitDrop(
                        targetClient.TamerId,
                        targetClient.Tamer.GeneralHandler,
                        bitsReward.MinAmount,
                        bitsReward.MaxAmount,
                        mob.CurrentLocation.MapId,
                        mob.CurrentLocation.X,
                        mob.CurrentLocation.Y
                    );

                    map.AddMapDrop(drop);
                }
            }
        }

        private void ItemDropReward(GameMap map, SummonMobModel mob, GameClient? targetClient)
        {
            if (!mob.DropReward.Drops.Any())
                return;

            var itemsReward = new List<SummonMobItemDropModel>();
            itemsReward.AddRange(mob.DropReward.Drops);
            itemsReward.RemoveAll(x => _assets.QuestItemList.Contains(x.ItemId));

            if (!itemsReward.Any())
                return;

            var dropped = 0;
            var totalDrops = UtilitiesFunctions.RandomInt(
                mob.DropReward.MinAmount,
                mob.DropReward.MaxAmount);

            while (dropped < totalDrops)
            {
                if (!itemsReward.Any())
                {
                    _logger.Warning($"Mob {mob.Id} has incorrect drops configuration.");
                    break;
                }

                var possibleDrops = itemsReward.OrderBy(x => Guid.NewGuid()).ToList();
                foreach (var itemDrop in possibleDrops)
                {
                    if (itemDrop.Chance >= UtilitiesFunctions.RandomDouble())
                    {
                        if (targetClient.Tamer.HasAura && targetClient.Tamer.Aura.ItemInfo.Section == 2100)
                        {
                            var newItem = new ItemModel();
                            newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == itemDrop.ItemId));

                            if (newItem.ItemInfo == null)
                            {
                                _logger.Warning($"No item info found with ID {itemDrop.ItemId} for tamer {targetClient.Tamer.Id}.");
                                targetClient.Send(new SystemMessagePacket($"No item info found with ID {itemDrop.ItemId}."));
                                continue;
                            }

                            newItem.ItemId = itemDrop.ItemId;
                            newItem.Amount = UtilitiesFunctions.RandomInt(itemDrop.MinAmount, itemDrop.MaxAmount);

                            var itemClone = (ItemModel)newItem.Clone();
                            if (targetClient.Tamer.Inventory.AddItem(newItem))
                            {
                                targetClient.Send(new ReceiveItemPacket(itemClone, InventoryTypeEnum.Inventory));
                                _sender.Send(new UpdateItemsCommand(targetClient.Tamer.Inventory));
                                _logger.Verbose($"Character {targetClient.TamerId} aquired {newItem.ItemId} x{newItem.Amount} from " +
                                    $"mob {mob.Id} with magnetic aura {targetClient.Tamer.Aura.ItemId}.");
                            }
                            else
                            {
                                targetClient.Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));

                                var drop = _dropManager.CreateItemDrop(
                                    targetClient.Tamer.Id,
                                    targetClient.Tamer.GeneralHandler,
                                    itemDrop.ItemId,
                                    itemDrop.MinAmount,
                                    itemDrop.MaxAmount,
                                    mob.CurrentLocation.MapId,
                                    mob.CurrentLocation.X,
                                    mob.CurrentLocation.Y
                                );

                                map.AddMapDrop(drop);
                            }

                            dropped++;
                        }
                        else
                        {
                            var drop = _dropManager.CreateItemDrop(
                                targetClient.Tamer.Id,
                                targetClient.Tamer.GeneralHandler,
                                itemDrop.ItemId,
                                itemDrop.MinAmount,
                                itemDrop.MaxAmount,
                                mob.CurrentLocation.MapId,
                                mob.CurrentLocation.X,
                                mob.CurrentLocation.Y
                            );

                            dropped++;

                            map.AddMapDrop(drop);
                        }

                        itemsReward.RemoveAll(x => x.Id == itemDrop.Id);
                        break;
                    }
                }
            }
        }

        private void QuestDropReward(GameMap map, SummonMobModel mob)
        {
            var itemsReward = new List<SummonMobItemDropModel>();
            itemsReward.AddRange(mob.DropReward.Drops);
            itemsReward.RemoveAll(x => !_assets.QuestItemList.Contains(x.ItemId));

            if (!itemsReward.Any())
                return;

            foreach (var tamer in mob.TargetTamers)
            {
                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == tamer?.Id);
                if (targetClient == null)
                    continue;

                if (!tamer.Progress.InProgressQuestData.Any())
                    continue;

                var updateItemList = false;
                var possibleDrops = itemsReward.Randomize();
                foreach (var itemDrop in possibleDrops)
                {
                    if (itemDrop.Chance >= UtilitiesFunctions.RandomDouble())
                    {
                        foreach (var questInProgress in tamer.Progress.InProgressQuestData)
                        {
                            var questInfo = _assets.Quest.FirstOrDefault(x => x.QuestId == questInProgress.QuestId);
                            if (questInfo != null)
                            {
                                if (!questInfo.QuestGoals.Exists(x => x.GoalType == QuestGoalTypeEnum.LootItem))
                                    continue;

                                var goalIndex = -1;
                                foreach (var questGoal in questInfo.QuestGoals)
                                {
                                    if (questGoal.GoalId == itemDrop?.ItemId)
                                    {
                                        var inventoryItems = tamer.Inventory.FindItemsById(questGoal.GoalId);
                                        var goalAmount = questGoal.GoalAmount;

                                        foreach (var inventoryItem in inventoryItems)
                                        {
                                            goalAmount -= inventoryItem.Amount;
                                            if (goalAmount <= 0)
                                            {
                                                goalAmount = 0;
                                                break;
                                            }
                                        }

                                        if (goalAmount > 0)
                                        {
                                            goalIndex = questInfo.QuestGoals.FindIndex(x => x == questGoal);
                                            break;
                                        }
                                    }
                                }

                                if (goalIndex != -1)
                                {
                                    var newItem = new ItemModel();
                                    newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == itemDrop.ItemId));

                                    if (newItem.ItemInfo == null)
                                    {
                                        _logger.Warning($"No item info found with ID {itemDrop.ItemId} for tamer {tamer.Id}.");
                                        targetClient.Send(new SystemMessagePacket($"No item info found with ID {itemDrop.ItemId}."));
                                        continue;
                                    }

                                    newItem.ItemId = itemDrop.ItemId;
                                    newItem.Amount = UtilitiesFunctions.RandomInt(itemDrop.MinAmount, itemDrop.MaxAmount);

                                    var itemClone = (ItemModel)newItem.Clone();
                                    if (tamer.Inventory.AddItem(newItem))
                                    {
                                        updateItemList = true;
                                        targetClient.Send(new ReceiveItemPacket(itemClone, InventoryTypeEnum.Inventory));
                                    }
                                    else
                                    {
                                        targetClient.Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));
                                    }
                                }
                            }
                            else
                            {
                                _logger.Error($"Unknown quest id {questInProgress.QuestId}.");
                                targetClient.Send(new SystemMessagePacket($"Unknown quest id {questInProgress.QuestId}."));
                            }
                        }

                        if (updateItemList) _sender.Send(new UpdateItemsCommand(tamer.Inventory));

                        itemsReward.RemoveAll(x => x.Id == itemDrop.Id);
                    }
                }
            }
        }

        private int SkillExperienceTable(int evolutionType, int SkillMastery)
        {
            List<Tuple<int, int>> RockieExperienceTemp = new List<Tuple<int, int>>
                      {
                          new Tuple<int, int>(0, 179),
                          new Tuple<int, int>(1, 283),
                          new Tuple<int, int>(2, 413),
                          new Tuple<int, int>(3, 568),
                          new Tuple<int, int>(4, 751),
                          new Tuple<int, int>(5, 962),
                          new Tuple<int, int>(6, 1201),
                          new Tuple<int, int>(7, 1470),
                          new Tuple<int, int>(8, 1769),
                          new Tuple<int, int>(9, 2098),
                          new Tuple<int, int>(10, 2460),
                          new Tuple<int, int>(11, 2855),
                          new Tuple<int, int>(12, 3283),
                          new Tuple<int, int>(13, 3745),
                          new Tuple<int, int>(14, 13496),
                          new Tuple<int, int>(15, 1349999997)
            };

            var ChampionExperienceTemp = new List<Tuple<int, int>>
                {
                    new Tuple<int, int>(0, 1470),
                    new Tuple<int, int>(1, 1769),
                    new Tuple<int, int>(2, 2098),
                    new Tuple<int, int>(3, 2460),
                    new Tuple<int, int>(4, 2855),
                    new Tuple<int, int>(5, 3283),
                    new Tuple<int, int>(6, 3745),
                    new Tuple<int, int>(7, 4242),
                    new Tuple<int, int>(8, 4776),
                    new Tuple<int, int>(9, 5340),
                    new Tuple<int, int>(10, 6608),
                    new Tuple<int, int>(11, 7318),
                    new Tuple<int, int>(12, 8069),
                    new Tuple<int, int>(13, 8864),
                    new Tuple<int, int>(14, 13496),
                    new Tuple<int, int>(15, 1349999997)

                };

            var UltimateExperienceTemp = new List<Tuple<int, int>>
                {
                    new Tuple<int, int>(0, 970),
                    new Tuple<int, int>(1, 1058),
                    new Tuple<int, int>(2, 1151),
                    new Tuple<int, int>(3, 1248),
                    new Tuple<int, int>(4, 1351),
                    new Tuple<int, int>(5, 2187),
                    new Tuple<int, int>(6, 2854),
                    new Tuple<int, int>(7, 3594),
                    new Tuple<int, int>(8, 4410),
                    new Tuple<int, int>(9, 5462),
                    new Tuple<int, int>(10, 6615),
                    new Tuple<int, int>(11, 7873),
                    new Tuple<int, int>(12, 9240),
                    new Tuple<int, int>(13, 10721),
                    new Tuple<int, int>(14, 13496),
                    new Tuple<int, int>(15, 1349999997)
                };

            var MegaExperienceTemp = new List<Tuple<int, int>>
                {
                    new Tuple<int, int>(0, 1588),
                    new Tuple<int, int>(1, 1786),
                    new Tuple<int, int>(2, 1996),
                    new Tuple<int, int>(3, 2221),
                    new Tuple<int, int>(4, 2460),
                    new Tuple<int, int>(5, 2713),
                    new Tuple<int, int>(6, 2982),
                    new Tuple<int, int>(7, 3265),
                    new Tuple<int, int>(8, 3565),
                    new Tuple<int, int>(9, 3881),
                    new Tuple<int, int>(10, 4213),
                    new Tuple<int, int>(11, 4563),
                    new Tuple<int, int>(12, 4929),
                    new Tuple<int, int>(13, 5314),
                    new Tuple<int, int>(14, 13496),
                    new Tuple<int, int>(15, 1349999997)
                };

            var JogressExperienceTemp = new List<Tuple<int, int>>
                {
                    new Tuple<int, int>(0, 4000),
                    new Tuple<int, int>(1, 6138),
                    new Tuple<int, int>(2, 6579),
                    new Tuple<int, int>(3, 7039),
                    new Tuple<int, int>(4, 7518),
                    new Tuple<int, int>(5, 8019),
                    new Tuple<int, int>(6, 8539),
                    new Tuple<int, int>(7, 9081),
                    new Tuple<int, int>(8, 9644),
                    new Tuple<int, int>(9, 10229),
                    new Tuple<int, int>(10, 10837),
                    new Tuple<int, int>(11, 11467),
                    new Tuple<int, int>(12, 12120),
                    new Tuple<int, int>(13, 12796),
                    new Tuple<int, int>(14, 13496),
                    new Tuple<int, int>(15, 1349999997)
                };

            var BurstModeExperienceTemp = new List<Tuple<int, int>>
               {
                   new Tuple<int, int>(0, 4213),
                   new Tuple<int, int>(1, 9971),
                   new Tuple<int, int>(2, 12746),
                   new Tuple<int, int>(3, 13546),
                   new Tuple<int, int>(4, 15372),
                   new Tuple<int, int>(5, 18225),
                   new Tuple<int, int>(6, 19104),
                   new Tuple<int, int>(7, 20010),
                   new Tuple<int, int>(8, 20944),
                   new Tuple<int, int>(9, 21474),                            
                   new Tuple<int, int>(10, 22474),
                   new Tuple<int, int>(11, 23474),
                   new Tuple<int, int>(12, 24474),
                   new Tuple<int, int>(13, 25474),
                   new Tuple<int, int>(14, 13496),
                   new Tuple<int, int>(15, 1349999997)
               };

            var CapsuleExperienceTemp = new List<Tuple<int, int>>
                {
                    new Tuple<int, int>(0, 1470),
                    new Tuple<int, int>(1, 1769),
                    new Tuple<int, int>(2, 2098),
                    new Tuple<int, int>(3, 2460),
                    new Tuple<int, int>(4, 2855),
                    new Tuple<int, int>(5, 3283),
                    new Tuple<int, int>(6, 3745),
                    new Tuple<int, int>(7, 4242),
                    new Tuple<int, int>(8, 4776),
                    new Tuple<int, int>(9, 5340),
                    new Tuple<int, int>(10, 6608),
                    new Tuple<int, int>(11, 7318),
                    new Tuple<int, int>(12, 8069),
                    new Tuple<int, int>(13, 8864),
                    new Tuple<int, int>(14, 13496),
                    new Tuple<int, int>(15, 1349999997)
                };

            switch ((EvolutionRankEnum)evolutionType)
            {

                case EvolutionRankEnum.RookieX:
                case EvolutionRankEnum.Rookie:
                    return RockieExperienceTemp.FirstOrDefault(x => x.Item1 == SkillMastery)?.Item2 ?? -1;

                case EvolutionRankEnum.ChampionX:
                case EvolutionRankEnum.Champion:
                    return ChampionExperienceTemp.FirstOrDefault(x => x.Item1 == SkillMastery)?.Item2 ?? -1;

                case EvolutionRankEnum.UltimateX:
                case EvolutionRankEnum.Ultimate:
                    return UltimateExperienceTemp.FirstOrDefault(x => x.Item1 == SkillMastery)?.Item2 ?? -1;

                case EvolutionRankEnum.MegaX:
                case EvolutionRankEnum.Mega:
                    return MegaExperienceTemp.FirstOrDefault(x => x.Item1 == SkillMastery)?.Item2 ?? -1;

                case EvolutionRankEnum.BurstModeX:
                case EvolutionRankEnum.BurstMode:
                    return BurstModeExperienceTemp.FirstOrDefault(x => x.Item1 == SkillMastery)?.Item2 ?? -1;

                case EvolutionRankEnum.JogressX:
                case EvolutionRankEnum.Jogress:
                    return JogressExperienceTemp.FirstOrDefault(x => x.Item1 == SkillMastery)?.Item2 ?? -1;

                case EvolutionRankEnum.Capsule:
                    return CapsuleExperienceTemp.FirstOrDefault(x => x.Item1 == SkillMastery)?.Item2 ?? -1;

                case EvolutionRankEnum.Spirit:
                    break;

                case EvolutionRankEnum.Extra:
                    break;
                default:
                    break;
            }

            return -1;

        }
    }
}