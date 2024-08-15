using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.Character;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.Map;
using DigitalWorldOnline.Commons.Extensions;
using DigitalWorldOnline.Commons.Models;
using DigitalWorldOnline.Commons.Models.Base;
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
using DigitalWorldOnline.Commons.Writers;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.Game.Models.Configuration;
using DigitalWorldOnline.Infraestructure.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Linq;

namespace DigitalWorldOnline.GameHost
{
    public sealed partial class MapServer
    {
        private void MonsterOperation(GameMap map)
        {
            if (!map.ConnectedTamers.Any())
                return;

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            map.UpdateMapMobs();

            foreach (var mob in map.Mobs)
            {
                if (!mob.AwaitingKillSpawn && DateTime.Now > mob.ViewCheckTime)
                {
                    mob.SetViewCheckTime(2);

                    mob.TamersViewing.RemoveAll(x => !map.ConnectedTamers.Select(y => y.Id).Contains(x));

                    var nearTamers = map.NearestTamers(mob.Id);

                    if (!nearTamers.Any() && !mob.TamersViewing.Any())
                        continue;

                    if (!mob.Dead)
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

                MobsOperation(map,mob);

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

                    if (!mob.Dead)
                    {
                        nearTamers.ForEach(nearTamer =>
                        {
                            if (!mob.TamersViewing.Contains(nearTamer))
                            {
                                mob.TamersViewing.Add(nearTamer);

                                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == nearTamer);

                                targetClient?.Send(new LoadMobsPacket(mob));

                            }
                            else
                            {
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

                MobsOperation(map,mob);

                mob.SetNextAction();
            }
            stopwatch.Stop();

            var totalTime = stopwatch.Elapsed.TotalMilliseconds;

            if (totalTime >= 1000)
                Console.WriteLine($"MonstersOperation ({map.Mobs.Count}): {totalTime}.");
        }

        private void MobsOperation(GameMap map,MobConfigModel mob)
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
                            CheckDebuff(map,mob,debuff);
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
                        ItemsReward(map,mob);
                        QuestKillReward(map,mob);
                        ExperienceReward(map,mob);

                        SourceKillSpawn(map,mob);
                        TargetKillSpawn(map,mob);
                    }
                    break;

                case MobActionEnum.Wait:
                    {
                        if (mob.Respawn && DateTime.Now > mob.DieTime.AddSeconds(2))
                        {
                            mob.SetNextWalkTime(UtilitiesFunctions.RandomInt(7,14));
                            mob.SetAgressiveCheckTime(5);
                            mob.SetRespawn();
                        }
                        else
                        {
                            //map.AttackNearbyTamer(mob, mob.TamersViewing);
                        }
                    }
                    break;

                case MobActionEnum.Walk:
                    {
                        if (mob.DebuffList.ActiveBuffs.Count > 0)
                        {
                            var debuff = mob.DebuffList.ActiveBuffs.Where(buff =>
                                 buff.BuffInfo.SkillInfo.Apply.Any(apply =>
                                     apply.Attribute == Commons.Enums.SkillCodeApplyAttributeEnum.CrowdControl
                                 )
                             ).ToList();

                            if (debuff.Any())
                            {
                                CheckDebuff(map,mob,debuff);
                                break;
                            }
                        }

                        map.BroadcastForTargetTamers(mob.TamersViewing,new SyncConditionPacket(mob.GeneralHandler,ConditionEnum.Default).Serialize());
                        mob.Move();
                        map.BroadcastForTargetTamers(mob.TamersViewing,new MobWalkPacket(mob).Serialize());
                    }
                    break;

                case MobActionEnum.GiveUp:
                    {
                        map.BroadcastForTargetTamers(mob.TamersViewing,new SyncConditionPacket(mob.GeneralHandler,ConditionEnum.Immortal).Serialize());
                        mob.ResetLocation();
                        map.BroadcastForTargetTamers(mob.TamersViewing,new MobRunPacket(mob).Serialize());
                        map.BroadcastForTargetTamers(mob.TamersViewing,new SetCombatOffPacket(mob.GeneralHandler).Serialize());

                        foreach (var targetTamer in mob.TargetTamers)
                        {
                            if (targetTamer.TargetMobs.Count <= 1)
                            {
                                targetTamer.StopBattle();
                                map.BroadcastForTamerViewsAndSelf(targetTamer.Id,new SetCombatOffPacket(targetTamer.Partner.GeneralHandler).Serialize());
                            }
                        }

                        mob.Reset(true);
                        map.BroadcastForTargetTamers(mob.TamersViewing,new UpdateCurrentHPRatePacket(mob.GeneralHandler,mob.CurrentHpRate).Serialize());
                    }
                    break;

                case MobActionEnum.Attack:
                    {
                        if (mob.DebuffList.ActiveBuffs.Count > 0)
                        {
                            var debuff = mob.DebuffList.ActiveBuffs.Where(buff =>
                                  buff.BuffInfo.SkillInfo.Apply.Any(apply =>
                                      apply.Attribute == Commons.Enums.SkillCodeApplyAttributeEnum.CrowdControl
                                  )
                              ).ToList();


                            if (debuff.Any())
                            {
                                CheckDebuff(map,mob,debuff);

                                break;
                            }
                        }

                        if (!mob.Dead && mob.SkillTime && !mob.CheckSkill && mob.IsPossibleSkill)
                        {
                            mob.UpdateCurrentAction(MobActionEnum.UseAttackSkill);
                            mob.SetNextAction();
                            break;
                        }

                        if (!mob.Dead && ((mob.TargetTamer == null || mob.TargetTamer.Hidden)))
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

                            var range = Math.Max(mob.ARValue,mob.Target.BaseInfo.ARValue);
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
                                    map.BroadcastForTargetTamers(mob.TamersViewing,new MissHitPacket(mob.GeneralHandler,mob.TargetHandler).Serialize());
                                    mob.UpdateLastHit();
                                    break;
                                }

                                map.AttackTarget(mob,_assets.NpcColiseum);
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
                                if (targetTamer.TargetMobs.Count <= 1)
                                {
                                    targetTamer.StopBattle();
                                    map.BroadcastForTamerViewsAndSelf(targetTamer.Id,new SetCombatOffPacket(targetTamer.Partner.GeneralHandler).Serialize());
                                }
                            }
                        }
                    }
                    break;

                case MobActionEnum.UseAttackSkill:
                    {
                        if (mob.DebuffList.ActiveBuffs.Count > 0)
                        {
                            var debuff = mob.DebuffList.ActiveBuffs.Where(buff =>
                                buff.BuffInfo.SkillInfo.Apply.Any(apply =>
                                    apply.Attribute == Commons.Enums.SkillCodeApplyAttributeEnum.CrowdControl
                                )
                            ).ToList();

                            if (debuff.Any())
                            {
                                CheckDebuff(map,mob,debuff);

                                break;
                            }
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

                        var targetSkill = skillList[random.Next(0,skillList.Count)];

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

                                map.SkillTarget(mob,targetSkill,_assets.NpcColiseum);



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
                                if (targetTamer.TargetMobs.Count <= 1)
                                {
                                    targetTamer.StopBattle();
                                    map.BroadcastForTamerViewsAndSelf(targetTamer.Id,new SetCombatOffPacket(targetTamer.Partner.GeneralHandler).Serialize());
                                }
                            }

                            break;
                        }
                    }
                    break;
            }
        }

        private static void CheckDebuff(GameMap map,MobConfigModel mob,List<MobDebuffModel> debuffs)
        {


            if (debuffs != null)
            {
                for (int i = 0;i < debuffs.Count;i++)
                {
                    var debuff = debuffs[i];

                    if (!debuff.DebuffExpired && mob.CurrentAction != MobActionEnum.CrowdControl)
                    {
                        mob.UpdateCurrentAction(MobActionEnum.CrowdControl);
                    }

                    if (debuff.DebuffExpired && mob.CurrentAction == MobActionEnum.CrowdControl)
                    {
                        debuffs.Remove(debuff);

                        if (debuffs.Count == 0)
                        {

                            map.BroadcastForTargetTamers(mob.TamersViewing,new RemoveBuffPacket(mob.GeneralHandler,debuff.BuffId,1).Serialize());

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

        private static void TargetKillSpawn(GameMap map,MobConfigModel mob)
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

        private static void SourceKillSpawn(GameMap map,MobConfigModel mob)
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

                    map.BroadcastForMap(new KillSpawnMinimapNotifyPacket(sourceKillSpawn.SourceMobType,sourceKillSpawn.CurrentSourceMobRequiredAmount).Serialize());

                }

                if (sourceMobKillSpawn.Spawn())
                {
                    foreach (var targetMob in sourceMobKillSpawn.TargetMobs)
                    {
                        //TODO: para todos os canais (apenas do mapa)
                        map.BroadcastForMap(new KillSpawnChatNotifyPacket(map.MapId,map.Channel,targetMob.TargetMobType).Serialize());

                        map.Mobs.Where(x => x.Type == targetMob.TargetMobType)?.ToList().ForEach(targetMob =>
                        {
                            targetMob.SetRespawn(true);
                            targetMob.SetAwaitingKillSpawn(false);
                        });
                    }
                }
            }
        }

        private void QuestKillReward(GameMap map,MobConfigModel mob)
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
                            var currentGoalValue = tamer.Progress.GetQuestGoalProgress(questInProgress.QuestId,goalIndex);
                            if (currentGoalValue < questInfo.QuestGoals[goalIndex].GoalAmount)
                            {
                                currentGoalValue++;
                                tamer.Progress.UpdateQuestInProgress(questInProgress.QuestId,goalIndex,currentGoalValue);
                                var questToUpdate = tamer.Progress.InProgressQuestData.FirstOrDefault(x => x.QuestId == questInProgress.QuestId);

                                targetClient.Send(new QuestGoalUpdatePacket(questInProgress.QuestId,(byte)goalIndex,currentGoalValue));
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
                                    var currentGoalValue = partyMemberClient.Tamer.Progress.GetQuestGoalProgress(questInProgress.QuestId,goalIndex);
                                    if (currentGoalValue < questInfo.QuestGoals[goalIndex].GoalAmount)
                                    {
                                        currentGoalValue++;
                                        partyMemberClient.Tamer.Progress.UpdateQuestInProgress(questInProgress.QuestId,goalIndex,currentGoalValue);
                                        var questToUpdate = partyMemberClient.Tamer.Progress.InProgressQuestData.FirstOrDefault(x => x.QuestId == questInProgress.QuestId);

                                        partyMemberClient.Send(new QuestGoalUpdatePacket(questInProgress.QuestId,(byte)goalIndex,currentGoalValue));
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

        private void ItemsReward(GameMap map,MobConfigModel mob)
        {
            if (mob.DropReward == null)
                return;

            QuestDropReward(map,mob);

            if (mob.Class == 8)
                RaidReward(map,mob);
            else
                DropReward(map,mob);
        }
        private long BonusPartnerExp(GameMap map,MobConfigModel mob)
        {
            long totalPartnerExp = 0;  // Initialize a variable to accumulate experience.

            foreach (var tamer in mob.TargetTamers)
            {
                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == tamer?.Id);
                if (targetClient == null)
                    continue;

                double expBonusMultiplier = tamer.BonusEXP / 100.0 + targetClient.ServerExperience / 100.0;
                double levelDifference = mob.Level - tamer.Partner.Level;

                // Calculate additional multiplier based on level difference
                long additionalMultiplier = (long)Math.Round(1 + levelDifference * 1);

                // Calculate the base experience to receive
                long partnerExpToReceive = (long)CalculateExperience(tamer.Partner.Level,mob.Level,mob.ExpReward.DigimonExperience);
                long bonusMultiplierExp = partnerExpToReceive * additionalMultiplier;
                // Apply bonus multipliers to the experience
                long finalExp = (long)(partnerExpToReceive * expBonusMultiplier);

                if (levelDifference > 0)
                {
                    finalExp += bonusMultiplierExp;
                }

                totalPartnerExp += (finalExp - partnerExpToReceive); // Add only the "bonus" experience


            }

            return totalPartnerExp;
        }




        private long BonusTamerExp(GameMap map,MobConfigModel mob)
        {
            long totalTamerExp = 0;  // Initialize a variable to accumulate experience.

            foreach (var tamer in mob.TargetTamers)
            {
                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == tamer?.Id);
                if (targetClient == null)
                    continue;

                // Calculate the experience bonus multiplier

                double expBonusMultiplier = tamer.BonusEXP / 100.0 + targetClient.ServerExperience / 100.0;
                double levelDifference = mob.Level - tamer.Partner.Level;

                // Calculate additional multiplier based on level difference
                long additionalMultiplier = (long)Math.Round(levelDifference * 1);  // Adjust multiplier factor as needed

                // Calculate the base experience to receive
                long tamerExpToReceive = (long)CalculateExperience(tamer.Partner.Level,mob.Level,mob.ExpReward.TamerExperience);

                // Calculate the bonus multiplier experience
                long bonusMultiplierExp = tamerExpToReceive * additionalMultiplier;

                // Apply bonus multipliers to the experience
                long finalExp = (long)(tamerExpToReceive * expBonusMultiplier);

                if (levelDifference > 1)  // Apply the multiplier conditionally
                {
                    finalExp += bonusMultiplierExp;  // Add the bonus multiplier experience if the level difference is significant
                }

                // Accumulate the additional experience gained
                totalTamerExp += (finalExp - tamerExpToReceive); // Add only the "bonus" experience
            }

            return totalTamerExp;
        }




        private void ExperienceReward(GameMap map,MobConfigModel mob)
        {
            if (mob.ExpReward == null)
                return;

            var partyIdList = new List<int>();

            foreach (var tamer in mob.TargetTamers)
            {
                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == tamer?.Id);
                if (targetClient == null)
                    continue;

                var tamerExpToReceive = (long)(CalculateExperience(tamer.Partner.Level,mob.Level,mob.ExpReward.TamerExperience)); //TODO: +bonus
                if (CalculateExperience(tamer.Partner.Level,mob.Level,mob.ExpReward.TamerExperience) == 0)
                    tamerExpToReceive = 0;

                if (tamerExpToReceive > 100) tamerExpToReceive += UtilitiesFunctions.RandomInt(-35,45);
                var tamerResult = ReceiveTamerExp(targetClient.Tamer,tamerExpToReceive);

                var partnerExpToReceive = (long)(CalculateExperience(tamer.Partner.Level,mob.Level,mob.ExpReward.DigimonExperience));



                if (CalculateExperience(tamer.Partner.Level,mob.Level,mob.ExpReward.DigimonExperience) == 0)
                    partnerExpToReceive = 0;

                if (partnerExpToReceive > 100) partnerExpToReceive += UtilitiesFunctions.RandomInt(-35,45);
                var partnerResult = ReceivePartnerExp(targetClient.Partner,mob,partnerExpToReceive);

                var totalTamerExp = BonusTamerExp(map,mob);

                var bonusTamerExp = ReceiveBonusTamerExp(targetClient.Tamer,totalTamerExp);

                var totalPartnerExp = BonusPartnerExp(map,mob);

                var bonusPartnerExp = ReceiveBonusPartnerExp(targetClient.Partner,mob,totalPartnerExp);


                targetClient.Send(
                    new ReceiveExpPacket(
                        tamerExpToReceive,
                        totalTamerExp,
                        targetClient.Tamer.CurrentExperience,
                        targetClient.Partner.GeneralHandler,
                        partnerExpToReceive,
                        totalPartnerExp,
                        targetClient.Partner.CurrentExperience,
                        targetClient.Partner.CurrentEvolution.SkillExperience
                    )
                );

                //TODO: importar o DMBase e tratar isso
                SkillExpReward(map,targetClient);

                if (tamerResult.LevelGain > 0 || partnerResult.LevelGain > 0)
                {
                    targetClient.Send(new UpdateStatusPacket(targetClient.Tamer));

                    map.BroadcastForTamerViewsAndSelf(targetClient.TamerId,
                        new UpdateMovementSpeedPacket(targetClient.Tamer).Serialize());
                }

                _sender.Send(new UpdateCharacterExperienceCommand(tamer));
                _sender.Send(new UpdateDigimonExperienceCommand(tamer.Partner));

                PartyExperienceReward(map,mob,partyIdList,targetClient,tamerExpToReceive,tamerResult,partnerExpToReceive,partnerResult);
            }

            partyIdList.Clear();
        }

        public long CalculateExperience(int tamerLevel,int mobLevel,long baseExperience)
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


        private void SkillExpReward(GameMap map,GameClient? targetClient)
        {


            var ExpNeed = int.MaxValue;
            var evolutionType = _assets.DigimonBaseInfo.First(x => x.Type == targetClient.Partner.CurrentEvolution.Type).EvolutionType;


            ExpNeed = SkillExperienceTable(evolutionType,targetClient.Partner.CurrentEvolution.SkillMastery);

            if (ExpNeed != -1 && targetClient.Partner.CurrentEvolution.SkillExperience >= ExpNeed)
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

                map.BroadcastForTamerViewsAndSelf(targetClient.TamerId,packet.Serialize());
            }
        }

        private async Task PartyExperienceReward(
            GameMap map,
            MobConfigModel mob,
            List<int> partyIdList,
            GameClient? targetClient,
             long tamerExpToReceive,
             ReceiveExpResult tamerResult,
             long partnerExpToReceive,
             ReceiveExpResult partnerResult)
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
                    if (tamerExpToReceive > 100) tamerExpToReceive += UtilitiesFunctions.RandomInt(-15,15);
                    tamerResult = ReceiveTamerExp(partyMemberClient.Tamer,tamerExpToReceive);

                    partnerExpToReceive = (long)((double)(mob.ExpReward.DigimonExperience) * 0.80); //TODO: +bonus
                    if (partnerExpToReceive > 100) partnerExpToReceive += UtilitiesFunctions.RandomInt(-15,15);
                    partnerResult = ReceivePartnerExp(partyMemberClient.Partner,mob,partnerExpToReceive);

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

                    await _sender.Send(new UpdateCharacterExperienceCommand(partyMemberClient.Tamer));
                    await _sender.Send(new UpdateDigimonExperienceCommand(partyMemberClient.Partner));
                }
            }
        }

        private async Task PartyExperienceReward(
          GameMap map,
          SummonMobModel mob,
          List<int> partyIdList,
          GameClient? targetClient,
           long tamerExpToReceive,
           ReceiveExpResult tamerResult,
           long partnerExpToReceive,
           ReceiveExpResult partnerResult)
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
                    if (tamerExpToReceive > 100) tamerExpToReceive += UtilitiesFunctions.RandomInt(-15,15);
                    tamerResult = ReceiveTamerExp(partyMemberClient.Tamer,tamerExpToReceive);

                    partnerExpToReceive = (long)((double)(mob.ExpReward.DigimonExperience) * 0.80); //TODO: +bonus
                    if (partnerExpToReceive > 100) partnerExpToReceive += UtilitiesFunctions.RandomInt(-15,15);
                    partnerResult = ReceivePartnerExp(partyMemberClient.Partner,mob,partnerExpToReceive);

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

                    await _sender.Send(new UpdateCharacterExperienceCommand(partyMemberClient.Tamer));
                    await _sender.Send(new UpdateDigimonExperienceCommand(partyMemberClient.Partner));
                }
            }
        }
        private void DropReward(GameMap map,MobConfigModel mob)
        {
            var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == mob.TargetTamer?.Id);
            if (targetClient == null)
                return;

            BitDropReward(map,mob,targetClient,_configuration);

            ItemDropReward(map,mob,targetClient,_configuration);
        }

        private void BitDropReward(GameMap map,MobConfigModel mob,GameClient? targetClient,IConfiguration configuration)
        {
            var gameConfig = new GameConfigurationModel();
            configuration.GetSection("GameConfigs").Bind(gameConfig);

            var bitsReward = mob.DropReward.BitsDrop;

            if (bitsReward != null && bitsReward.Chance >= UtilitiesFunctions.RandomDouble())
            {

                if (targetClient.Tamer.HasAura && targetClient.Tamer.Aura.ItemInfo.Section == 2100)
                {
                    var amount = UtilitiesFunctions.RandomInt(bitsReward.MinAmount,bitsReward.MaxAmount) * int.Parse(configuration["GameConfigs:BitDropCount:MultiplyDropCount"] ?? "0.1");

                    targetClient.Send(
                        new PickBitsPacket(
                            targetClient.Tamer.GeneralHandler,
                            amount
                        )
                    );

                    targetClient.Tamer.Inventory.AddBits(amount);

                    _sender.Send(new UpdateItemsCommand(targetClient.Tamer.Inventory));
                    _logger.Verbose($"Character {targetClient.TamerId} aquired {amount} bits from mob {mob.Id} with magnetic aura {targetClient.Tamer.Aura.ItemId}.");
                }
                else
                {
                    var drop = _dropManager.CreateBitDrop(
                        targetClient.TamerId,
                        targetClient.Tamer.GeneralHandler,
                        bitsReward.MinAmount * int.Parse(configuration["GameConfigs:BitDropCount:MultiplyDropCount"] ?? "0.1"),
                        bitsReward.MaxAmount * int.Parse(configuration["GameConfigs:BitDropCount:MultiplyDropCount"] ?? "0.1"),
                        mob.CurrentLocation.MapId,
                        mob.CurrentLocation.X,
                        mob.CurrentLocation.Y
                    );
                    map.AddMapDrop(drop);
                }
            }
        }

        private void ItemDropReward(GameMap map,MobConfigModel mob,GameClient? targetClient,IConfiguration configuration)
        {
            var gameConfig = new GameConfigurationModel();
            configuration.GetSection("GameConfigs").Bind(gameConfig);

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
                            newItem.Amount = UtilitiesFunctions.RandomInt(itemDrop.MinAmount,itemDrop.MaxAmount) * int.Parse(configuration["GameConfigs:ItemDropCount:MultiplyDropCount"] ?? "0.1");

                            var itemClone = (ItemModel)newItem.Clone();
                            if (targetClient.Tamer.Inventory.AddItem(newItem))
                            {
                                targetClient.Send(new ReceiveItemPacket(itemClone,InventoryTypeEnum.Inventory));
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
                                itemDrop.MinAmount * int.Parse(configuration["GameConfigs:ItemDropCount:MultiplyDropCount"] ?? "0.1"),
                                itemDrop.MaxAmount * int.Parse(configuration["GameConfigs:ItemDropCount:MultiplyDropCount"] ?? "0.1"),
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

        private void QuestDropReward(GameMap map,MobConfigModel mob)
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
                                    newItem.Amount = UtilitiesFunctions.RandomInt(itemDrop.MinAmount,itemDrop.MaxAmount);

                                    var itemClone = (ItemModel)newItem.Clone();
                                    if (tamer.Inventory.AddItem(newItem))
                                    {
                                        updateItemList = true;
                                        targetClient.Send(new ReceiveItemPacket(itemClone,InventoryTypeEnum.Inventory));
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

        private void RaidReward(GameMap map,MobConfigModel mob)
        {
            var raidResult = mob.RaidDamage.Where(x => x.Key > 0).DistinctBy(x => x.Key);

            var writer = new PacketWriter();
            writer.Type(1604);
            writer.WriteInt(raidResult.Count());

            int i = 1;

            var updateItemList = new List<ItemListModel>();
            var attackerName = string.Empty;
            var attackerType = 0;

            foreach (var raidTamer in raidResult.OrderByDescending(x => x.Value))
            {

                _logger.Verbose($"Character {raidTamer.Key} rank {i} on raid {mob.Id} - {mob.Name} with damage {raidTamer.Value}.");

                var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == raidTamer.Key);

                if (i == 1)
                {
                    if (targetClient != null)
                    {
                        attackerName = targetClient.Tamer.Name;
                        attackerType = targetClient.Tamer.Partner.CurrentType;
                    }
                }

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
                            newItem.Amount = UtilitiesFunctions.RandomInt(reward.MinAmount,reward.MaxAmount);

                            if (newItem.IsTemporary)
                                newItem.SetRemainingTime((uint)newItem.ItemInfo.UsageTimeMinutes);

                            var itemClone = (ItemModel)newItem.Clone();
                            if (targetClient.Tamer.Inventory.AddItem(newItem))
                            {
                                targetClient.Send(new ReceiveItemPacket(itemClone,InventoryTypeEnum.Inventory));
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

            map.BroadcastForTargetTamers(mob.RaidDamage.Select(x => x.Key).ToList(),writer.Serialize());
            updateItemList.ForEach(itemList => { _sender.Send(new UpdateItemsCommand(itemList)); });

            BlessingFIW(mob,attackerName,attackerType);
        }

        private async Task BlessingFIW(MobConfigModel mob,string attackerName,int attackerType)
        {
            if (mob.Bless)
            {
                for (int x = 0;x < 8;x++)
                {
                    var mapId = 1300 + x;

                    var currentMap = Maps.FirstOrDefault(x => x.Clients.Any() && x.MapId == mapId);

                    if (currentMap != null)
                    {
                        var clients = currentMap.Clients;

                        var targetItem = _assets.ItemInfo.FirstOrDefault(x => x.ItemId == 71552);

                        if (targetItem != null)
                        {
                            var buff = _assets.BuffInfo.FirstOrDefault(x => x.SkillCode == targetItem.SkillCode || x.DigimonSkillCode == targetItem.SkillCode);

                            if (buff != null)
                            {
                                foreach (var client in clients)
                                {
                                    var duration = UtilitiesFunctions.RemainingTimeSeconds(targetItem.TimeInSeconds);

                                    var newDigimonBuff = DigimonBuffModel.Create(buff.BuffId,buff.SkillId,targetItem.TypeN,targetItem.TimeInSeconds);
                                    newDigimonBuff.SetBuffInfo(buff);
                                    client.Tamer.Partner.BuffList.Add(newDigimonBuff);

                                    client.Send(new GlobalMessagePacket(mob.Type,attackerName,attackerType,targetItem.ItemId));
                                    client.Send(new UpdateStatusPacket(client.Tamer));

                                    BroadcastForTamerViewsAndSelf(client.TamerId,
                                        new AddBuffPacket(client.Partner.GeneralHandler,buff,(short)targetItem.TypeN,duration).Serialize());

                                    await _sender.Send(new UpdateDigimonBuffListCommand(client.Partner.BuffList));
                                }
                            }
                        }
                    }
                }
            }
        }

        private void RaidReward(GameMap map,SummonMobModel mob)
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
                            newItem.Amount = UtilitiesFunctions.RandomInt(reward.MinAmount,reward.MaxAmount);

                            if (newItem.IsTemporary)
                                newItem.SetRemainingTime((uint)newItem.ItemInfo.UsageTimeMinutes);

                            var itemClone = (ItemModel)newItem.Clone();
                            if (targetClient.Tamer.Inventory.AddItem(newItem))
                            {
                                targetClient.Send(new ReceiveItemPacket(itemClone,InventoryTypeEnum.Inventory));
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

            map.BroadcastForTargetTamers(mob.RaidDamage.Select(x => x.Key).ToList(),writer.Serialize());
            updateItemList.ForEach(itemList => { _sender.Send(new UpdateItemsCommand(itemList)); });
        }

        private void MobsOperation(GameMap map,SummonMobModel mob)
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
                        ItemsReward(map,mob);
                        QuestKillReward(map,mob);
                        ExperienceReward(map,mob);
                    }
                    break;

                case MobActionEnum.Wait:
                    {
                        if (mob.Respawn && DateTime.Now > mob.DieTime.AddSeconds(2))
                        {
                            mob.SetNextWalkTime(UtilitiesFunctions.RandomInt(7,14));
                            mob.SetAgressiveCheckTime(5);
                            mob.SetRespawn();
                        }
                        else
                        {
                            //map.AttackNearbyTamer(mob, mob.TamersViewing);
                        }
                    }
                    break;

                case MobActionEnum.Walk:
                    {
                        map.BroadcastForTargetTamers(mob.TamersViewing,new SyncConditionPacket(mob.GeneralHandler,ConditionEnum.Default).Serialize());
                        mob.Move();
                        map.BroadcastForTargetTamers(mob.TamersViewing,new MobWalkPacket(mob).Serialize());
                    }
                    break;

                case MobActionEnum.GiveUp:
                    {
                        map.BroadcastForTargetTamers(mob.TamersViewing,new SyncConditionPacket(mob.GeneralHandler,ConditionEnum.Immortal).Serialize());
                        mob.ResetLocation();
                        map.BroadcastForTargetTamers(mob.TamersViewing,new MobRunPacket(mob).Serialize());
                        map.BroadcastForTargetTamers(mob.TamersViewing,new SetCombatOffPacket(mob.GeneralHandler).Serialize());

                        foreach (var targetTamer in mob.TargetTamers)
                        {

                            targetTamer.StopBattle(true);
                            map.BroadcastForTamerViewsAndSelf(targetTamer.Id,new SetCombatOffPacket(targetTamer.Partner.GeneralHandler).Serialize());

                        }

                        mob.Reset(true);
                        map.BroadcastForTargetTamers(mob.TamersViewing,new UpdateCurrentHPRatePacket(mob.GeneralHandler,mob.CurrentHpRate).Serialize());
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

                            var range = Math.Max(mob.ARValue,mob.Target.BaseInfo.ARValue);
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
                                    map.BroadcastForTargetTamers(mob.TamersViewing,new MissHitPacket(mob.GeneralHandler,mob.TargetHandler).Serialize());
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
                                map.BroadcastForTamerViewsAndSelf(targetTamer.Id,new SetCombatOffPacket(targetTamer.Partner.GeneralHandler).Serialize());

                            }

                            break;
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

                        var targetSkill = skillList[random.Next(0,skillList.Count)];

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

                                map.SkillTarget(mob,targetSkill);



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
                                map.BroadcastForTamerViewsAndSelf(targetTamer.Id,new SetCombatOffPacket(targetTamer.Partner.GeneralHandler).Serialize());

                            }

                            break;
                        }
                    }
                    break;
            }
        }

        private void QuestKillReward(GameMap map,SummonMobModel mob)
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
                            var currentGoalValue = tamer.Progress.GetQuestGoalProgress(questInProgress.QuestId,goalIndex);
                            if (currentGoalValue < questInfo.QuestGoals[goalIndex].GoalAmount)
                            {
                                currentGoalValue++;
                                tamer.Progress.UpdateQuestInProgress(questInProgress.QuestId,goalIndex,currentGoalValue);

                                targetClient.Send(new QuestGoalUpdatePacket(questInProgress.QuestId,(byte)goalIndex,currentGoalValue));
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
                                    var currentGoalValue = partyMemberClient.Tamer.Progress.GetQuestGoalProgress(questInProgress.QuestId,goalIndex);
                                    if (currentGoalValue < questInfo.QuestGoals[goalIndex].GoalAmount)
                                    {
                                        currentGoalValue++;
                                        partyMemberClient.Tamer.Progress.UpdateQuestInProgress(questInProgress.QuestId,goalIndex,currentGoalValue);
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

        private void ItemsReward(GameMap map,SummonMobModel mob)
        {
            if (mob.DropReward == null)
                return;

            QuestDropReward(map,mob);

            if (mob.Class == 8)
                RaidReward(map,mob);
            else
                DropReward(map,mob);
        }

        private void ExperienceReward(GameMap map,SummonMobModel mob)
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

                var tamerExpToReceive = (long)(CalculateExperience(tamer.Partner.Level,mob.Level,mob.ExpReward.TamerExperience) * expBonusMultiplier); //TODO: +bonus

                if (CalculateExperience(tamer.Partner.Level,mob.Level,mob.ExpReward.TamerExperience) == 0)
                    tamerExpToReceive = 0;

                if (tamerExpToReceive > 100) tamerExpToReceive += UtilitiesFunctions.RandomInt(-15,15);
                var tamerResult = ReceiveTamerExp(targetClient.Tamer,tamerExpToReceive);

                var partnerExpToReceive = (long)(CalculateExperience(tamer.Partner.Level,mob.Level,mob.ExpReward.DigimonExperience) * expBonusMultiplier); //TODO: +bonus

                if (CalculateExperience(tamer.Partner.Level,mob.Level,mob.ExpReward.DigimonExperience) == 0)
                    partnerExpToReceive = 0;

                if (partnerExpToReceive > 100) partnerExpToReceive += UtilitiesFunctions.RandomInt(-15,15);
                var partnerResult = ReceivePartnerExp(targetClient.Partner,mob,partnerExpToReceive);

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
                SkillExpReward(map,targetClient);

                if (tamerResult.LevelGain > 0 || partnerResult.LevelGain > 0)
                {
                    targetClient.Send(new UpdateStatusPacket(targetClient.Tamer));

                    map.BroadcastForTamerViewsAndSelf(targetClient.TamerId,
                        new UpdateMovementSpeedPacket(targetClient.Tamer).Serialize());
                }

                _sender.Send(new UpdateCharacterExperienceCommand(tamer));
                _sender.Send(new UpdateDigimonExperienceCommand(tamer.Partner));

                PartyExperienceReward(map,mob,partyIdList,targetClient,tamerExpToReceive,tamerResult,partnerExpToReceive,partnerResult);
            }

            partyIdList.Clear();
        }
        private void DropReward(GameMap map,SummonMobModel mob)
        {
            var targetClient = map.Clients.FirstOrDefault(x => x.TamerId == mob.TargetTamer?.Id);
            if (targetClient == null)
                return;

            BitDropReward(map,mob,targetClient);

            ItemDropReward(map,mob,targetClient);
        }

        private void BitDropReward(GameMap map,SummonMobModel mob,GameClient? targetClient)
        {
            var bitsReward = mob.DropReward.BitsDrop;

            if (bitsReward != null && bitsReward.Chance >= UtilitiesFunctions.RandomDouble())
            {
                if (targetClient.Tamer.HasAura && targetClient.Tamer.Aura.ItemInfo.Section == 2100)
                {
                    var amount = UtilitiesFunctions.RandomInt(bitsReward.MinAmount,bitsReward.MaxAmount);

                    targetClient.Send(
                        new PickBitsPacket(
                            targetClient.Tamer.GeneralHandler,
                            amount
                        )
                    );

                    targetClient.Tamer.Inventory.AddBits(amount);

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

        private void ItemDropReward(GameMap map,SummonMobModel mob,GameClient? targetClient)
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
                            newItem.Amount = UtilitiesFunctions.RandomInt(itemDrop.MinAmount,itemDrop.MaxAmount);

                            var itemClone = (ItemModel)newItem.Clone();
                            if (targetClient.Tamer.Inventory.AddItem(newItem))
                            {
                                targetClient.Send(new ReceiveItemPacket(itemClone,InventoryTypeEnum.Inventory));
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

        private void QuestDropReward(GameMap map,SummonMobModel mob)
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
                                    newItem.Amount = UtilitiesFunctions.RandomInt(itemDrop.MinAmount,itemDrop.MaxAmount);

                                    var itemClone = (ItemModel)newItem.Clone();
                                    if (tamer.Inventory.AddItem(newItem))
                                    {
                                        updateItemList = true;
                                        targetClient.Send(new ReceiveItemPacket(itemClone,InventoryTypeEnum.Inventory));
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

        private int SkillExperienceTable(int evolutionType,int SkillMastery)
        {
            List<Tuple<int,int>> RockieExperienceTemp = new List<Tuple<int,int>>
             {
                    new Tuple<int, int>(0, 2940),
                    new Tuple<int, int>(1, 3538),
                    new Tuple<int, int>(2, 4196),
                    new Tuple<int, int>(3, 4920),
                    new Tuple<int, int>(4, 5710),
                    new Tuple<int, int>(5, 6566),
                    new Tuple<int, int>(6, 7490),
                    new Tuple<int, int>(7, 8484),
                    new Tuple<int, int>(8, 9552),
                    new Tuple<int, int>(9, 10680),
                    new Tuple<int, int>(10, 13216),
                    new Tuple<int, int>(11, 14636),
                    new Tuple<int, int>(12, 16138),
                    new Tuple<int, int>(13, 17728),
                    new Tuple<int, int>(14, 26992),
                    new Tuple<int, int>(15, 26998),
                    new Tuple<int, int>(16, 52616),
                    new Tuple<int, int>(17, 58636),
                    new Tuple<int, int>(18, 64138),
                    new Tuple<int, int>(19, 73728),
                    new Tuple<int, int>(20, 82992),
                    new Tuple<int, int>(21, 94998),
                    new Tuple<int, int>(22, 106998),
                    new Tuple<int, int>(23, 126998),
                    new Tuple<int, int>(24, 146998),
                    new Tuple<int, int>(25, 169598),
                    new Tuple<int, int>(26, 195498),
                    new Tuple<int, int>(27, 224598),
                    new Tuple<int, int>(28, 257898),
                    new Tuple<int, int>(29, 295498),
                    new Tuple<int, int>(30, 342497),
                    new Tuple<int, int>(31, 362497),
                    new Tuple<int, int>(32, 412497),
                    new Tuple<int, int>(33, 442497),
                    new Tuple<int, int>(34, 542497)
               };


            var ChampionExperienceTemp = new List<Tuple<int,int>>
                {
                    new Tuple<int, int>(0, 4410),
                    new Tuple<int, int>(1, 5307),
                    new Tuple<int, int>(2, 6294),
                    new Tuple<int, int>(3, 7380),
                    new Tuple<int, int>(4, 8565),
                    new Tuple<int, int>(5, 9849),
                    new Tuple<int, int>(6, 11235),
                    new Tuple<int, int>(7, 12726),
                    new Tuple<int, int>(8, 14328),
                    new Tuple<int, int>(9, 16020),
                    new Tuple<int, int>(10, 19824),
                    new Tuple<int, int>(11, 21954),
                    new Tuple<int, int>(12, 24207),
                    new Tuple<int, int>(13, 26592),
                    new Tuple<int, int>(14, 40488),
                    new Tuple<int, int>(15, 40497),
                    new Tuple<int, int>(16, 78924),
                    new Tuple<int, int>(17, 87954),
                    new Tuple<int, int>(18, 96207),
                    new Tuple<int, int>(19, 110592),
                    new Tuple<int, int>(20, 124488),
                    new Tuple<int, int>(21, 142497),
                    new Tuple<int, int>(22, 160497),
                    new Tuple<int, int>(23, 190497),
                    new Tuple<int, int>(24, 220997),
                    new Tuple<int, int>(25, 256497),
                    new Tuple<int, int>(26, 295497),
                    new Tuple<int, int>(27, 339597),
                    new Tuple<int, int>(28, 388497),
                    new Tuple<int, int>(29, 442497),
                    new Tuple<int, int>(30, 442497),
                    new Tuple<int, int>(31, 642497),
                    new Tuple<int, int>(32, 842497),
                    new Tuple<int, int>(33, 942497),
                    new Tuple<int, int>(34, 1142497)
                };


            var UltimateExperienceTemp = new List<Tuple<int,int>>
                {
                    new Tuple<int, int>(0, 5880),
                    new Tuple<int, int>(1, 7076),
                    new Tuple<int, int>(2, 8392),
                    new Tuple<int, int>(3, 9840),
                    new Tuple<int, int>(4, 11420),
                    new Tuple<int, int>(5, 13132),
                    new Tuple<int, int>(6, 14980),
                    new Tuple<int, int>(7, 16968),
                    new Tuple<int, int>(8, 19104),
                    new Tuple<int, int>(9, 21360),
                    new Tuple<int, int>(10, 26432),
                    new Tuple<int, int>(11, 29272),
                    new Tuple<int, int>(12, 32276),
                    new Tuple<int, int>(13, 35456),
                    new Tuple<int, int>(14, 53984),
                    new Tuple<int, int>(15, 54000),
                    new Tuple<int, int>(16, 105232),
                    new Tuple<int, int>(17, 117272),
                    new Tuple<int, int>(18, 128276),
                    new Tuple<int, int>(19, 147456),
                    new Tuple<int, int>(20, 165984),
                    new Tuple<int, int>(21, 189996),
                    new Tuple<int, int>(22, 213996),
                    new Tuple<int, int>(23, 253996),
                    new Tuple<int, int>(24, 293996),
                    new Tuple<int, int>(25, 337996),
                    new Tuple<int, int>(26, 387996),
                    new Tuple<int, int>(27, 443996),
                    new Tuple<int, int>(28, 505996),
                    new Tuple<int, int>(29, 573996),
                    new Tuple<int, int>(30, 742497),
                    new Tuple<int, int>(31, 942497),
                    new Tuple<int, int>(32, 1242497),
                    new Tuple<int, int>(33, 1542497),
                    new Tuple<int, int>(34, 1842497)
                };


            var MegaExperienceTemp = new List<Tuple<int,int>>
                {
                    new Tuple<int, int>(0, 7350),
                    new Tuple<int, int>(1, 8845),
                    new Tuple<int, int>(2, 10490),
                    new Tuple<int, int>(3, 12300),
                    new Tuple<int, int>(4, 14275),
                    new Tuple<int, int>(5, 16415),
                    new Tuple<int, int>(6, 18725),
                    new Tuple<int, int>(7, 21210),
                    new Tuple<int, int>(8, 23880),
                    new Tuple<int, int>(9, 26700),
                    new Tuple<int, int>(10, 33040),
                    new Tuple<int, int>(11, 36590),
                    new Tuple<int, int>(12, 40345),
                    new Tuple<int, int>(13, 44320),
                    new Tuple<int, int>(14, 67480),
                    new Tuple<int, int>(15, 67495),
                    new Tuple<int, int>(16, 131540),
                    new Tuple<int, int>(17, 146590),
                    new Tuple<int, int>(18, 160345),
                    new Tuple<int, int>(19, 184320),
                    new Tuple<int, int>(20, 207480),
                    new Tuple<int, int>(21, 237495),
                    new Tuple<int, int>(22, 267495),
                    new Tuple<int, int>(23, 317495),
                    new Tuple<int, int>(24, 367495),
                    new Tuple<int, int>(25, 420995),
                    new Tuple<int, int>(26, 477995),
                    new Tuple<int, int>(27, 538995),
                    new Tuple<int, int>(28, 604995),
                    new Tuple<int, int>(29, 675995),
                    new Tuple<int, int>(30, 942497),
                    new Tuple<int, int>(31, 1342497),
                    new Tuple<int, int>(32, 1642497),
                    new Tuple<int, int>(33, 1842497),
                    new Tuple<int, int>(34, 2142497)
                };


            var JogressExperienceTemp = new List<Tuple<int,int>>
                {
                    new Tuple<int, int>(0, 29400),
                    new Tuple<int, int>(1, 35380),
                    new Tuple<int, int>(2, 41960),
                    new Tuple<int, int>(3, 49200),
                    new Tuple<int, int>(4, 57100),
                    new Tuple<int, int>(5, 65660),
                    new Tuple<int, int>(6, 74900),
                    new Tuple<int, int>(7, 84840),
                    new Tuple<int, int>(8, 95520),
                    new Tuple<int, int>(9, 106800),
                    new Tuple<int, int>(10, 132160),
                    new Tuple<int, int>(11, 146360),
                    new Tuple<int, int>(12, 161380),
                    new Tuple<int, int>(13, 177280),
                    new Tuple<int, int>(14, 269920),
                    new Tuple<int, int>(15, 269980),
                    new Tuple<int, int>(16, 526160),
                    new Tuple<int, int>(17, 586360),
                    new Tuple<int, int>(18, 641380),
                    new Tuple<int, int>(19, 737280),
                    new Tuple<int, int>(20, 829920),
                    new Tuple<int, int>(21, 949980),
                    new Tuple<int, int>(22, 1069980),
                    new Tuple<int, int>(23, 1269980),
                    new Tuple<int, int>(24, 1469980),
                    new Tuple<int, int>(25, 1739970),
                    new Tuple<int, int>(26, 2059970),
                    new Tuple<int, int>(27, 2439970),
                    new Tuple<int, int>(28, 2879970),
                    new Tuple<int, int>(29, 3379970),
                    new Tuple<int, int>(30, 4442497),
                    new Tuple<int, int>(31, 6442497),
                    new Tuple<int, int>(32, 8442497),
                    new Tuple<int, int>(33, 11442497),
                    new Tuple<int, int>(34, 15442497)
                };


            var BurstModeExperienceTemp = new List<Tuple<int,int>>
                {
                    new Tuple<int, int>(0, 10290),
                    new Tuple<int, int>(1, 12383),
                    new Tuple<int, int>(2, 14686),
                    new Tuple<int, int>(3, 17220),
                    new Tuple<int, int>(4, 19985),
                    new Tuple<int, int>(5, 22981),
                    new Tuple<int, int>(6, 26215),
                    new Tuple<int, int>(7, 29694),
                    new Tuple<int, int>(8, 33432),
                    new Tuple<int, int>(9, 37380),
                    new Tuple<int, int>(10, 46256),
                    new Tuple<int, int>(11, 51226),
                    new Tuple<int, int>(12, 56483),
                    new Tuple<int, int>(13, 62048),
                    new Tuple<int, int>(14, 94472),
                    new Tuple<int, int>(15, 94493),
                    new Tuple<int, int>(16, 184156),
                    new Tuple<int, int>(17, 205226),
                    new Tuple<int, int>(18, 224483),
                    new Tuple<int, int>(19, 257088),
                    new Tuple<int, int>(20, 290472),
                    new Tuple<int, int>(21, 332493),
                    new Tuple<int, int>(22, 374493),
                    new Tuple<int, int>(23, 444493),
                    new Tuple<int, int>(24, 514493),
                    new Tuple<int, int>(25, 588493),
                    new Tuple<int, int>(26, 665493),
                    new Tuple<int, int>(27, 745493),
                    new Tuple<int, int>(28, 828493),
                    new Tuple<int, int>(29, 915493),
                    new Tuple<int, int>(30, 1342497),
                    new Tuple<int, int>(31, 1542497),
                    new Tuple<int, int>(32, 1742497),
                    new Tuple<int, int>(33, 2142497),
                    new Tuple<int, int>(34, 2342497)
                };


            var CapsuleExperienceTemp = new List<Tuple<int,int>>
                {
                    new Tuple<int, int>(0, 8820),
                    new Tuple<int, int>(1, 10614),
                    new Tuple<int, int>(2, 12588),
                    new Tuple<int, int>(3, 14760),
                    new Tuple<int, int>(4, 17130),
                    new Tuple<int, int>(5, 19698),
                    new Tuple<int, int>(6, 22470),
                    new Tuple<int, int>(7, 25452),
                    new Tuple<int, int>(8, 28656),
                    new Tuple<int, int>(9, 32040),
                    new Tuple<int, int>(10, 39648),
                    new Tuple<int, int>(11, 43908),
                    new Tuple<int, int>(12, 48414),
                    new Tuple<int, int>(13, 53184),
                    new Tuple<int, int>(14, 80976),
                    new Tuple<int, int>(15, 80994),
                    new Tuple<int, int>(16, 157848),
                    new Tuple<int, int>(17, 175908),
                    new Tuple<int, int>(18, 192414),
                    new Tuple<int, int>(19, 221184),
                    new Tuple<int, int>(20, 248976),
                    new Tuple<int, int>(21, 284994),
                    new Tuple<int, int>(22, 320994),
                    new Tuple<int, int>(23, 380994),
                    new Tuple<int, int>(24, 440994),
                    new Tuple<int, int>(25, 510594),
                    new Tuple<int, int>(26, 587994),
                    new Tuple<int, int>(27, 672594),
                    new Tuple<int, int>(28, 764994),
                    new Tuple<int, int>(29, 865194),
                    new Tuple<int, int>(30, 1142497),
                    new Tuple<int, int>(31, 1342497),
                    new Tuple<int, int>(32, 1742497),
                    new Tuple<int, int>(33, 1942497),
                    new Tuple<int, int>(34, 2342497)
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