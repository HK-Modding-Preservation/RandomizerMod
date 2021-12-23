﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RandomizerCore;
using RandomizerCore.Extensions;
using RandomizerCore.Logic;
using RandomizerCore.Randomization;
using RandomizerMod.Extensions;
using RandomizerMod.IC;
using RandomizerMod.Logging;
using RandomizerMod.RandomizerData;
using RandomizerMod.Settings;

namespace RandomizerMod.RC
{
    public class RandoController
    {
        public RandoModContext ctx;
        public RequestBuilder rb;
        public readonly GenerationSettings gs;
        public readonly RandoMonitor rm;
        public readonly SettingsPM pm;
        public readonly Random rng;
        private LogArguments args;

        public RandoController(GenerationSettings gs, SettingsPM pm, RandoMonitor rm)
        {
            this.gs = gs;
            this.rm = rm;
            this.pm = pm;
            rng = new Random(gs.Seed + 4);
        }

        public void Run()
        {
            gs.Clamp();
            SelectStart();
            ctx = new(gs);
            AssignNotchCosts();
            ctx.LM = RCData.GetNewLogicManager(gs);
            rb = new(gs, ctx.LM);
            rb.Run(out RandomizationStage[] stages, out ctx.Vanilla, out ctx.itemPlacements);
            Randomizer randomizer = new(new Random(gs.Seed), ctx, stages, rm);
            List<List<RandoPlacement>[]> stagedPlacements = randomizer.Run();
            for (int i = 0; i < stagedPlacements.Count; i++)
            {
                for (int j = 0; j < stagedPlacements[i].Length; j++)
                {
                    foreach (RandoPlacement placement in stagedPlacements[i][j])
                    {
                        if (placement.Item is RandoModItem item && placement.Location is RandoModLocation location)
                        {
                            item.onRandomizerFinish?.Invoke(placement);
                            location?.onRandomizerFinish?.Invoke(placement);
                            ctx.itemPlacements.Add(new(item, location));
                        }
                        else if (placement.Item is RandoModTransition target && placement.Location is RandoModTransition source)
                        {
                            ctx.transitionPlacements ??= new();
                            ctx.transitionPlacements.Add(new(source, target));
                        }
                        else
                        {
                            throw new InvalidOperationException($"Unknown placement type found in randomizer result with {placement.Item} at {placement.Location}");
                        }
                    }
                }
            }
            args = new()
            {
                ctx = new RandoContext // we clone the context for the loggers so that we can obfuscate progression on the ctx used for Export
                {
                    notchCosts = ctx.notchCosts?.ToList(),
                    itemPlacements = ctx.itemPlacements?.ToList(),
                    transitionPlacements = ctx.transitionPlacements?.ToList()
                },
                gs = gs,
                randomizer = randomizer,
            };
        }

        public int Hash()
        {
            using System.Security.Cryptography.SHA256Managed sHA256 = new();
            StringBuilder sb = new();
            sb.Append(JsonUtil.Serialize(gs));

            if (ctx.notchCosts != null) sb.Append(string.Join(";", ctx.notchCosts));

            if (ctx.itemPlacements != null)
            {
                foreach (var p in ctx.itemPlacements)
                {
                    sb.Append(p.item.Name);
                    sb.Append(p.location.Name);
                    sb.Append(';');
                }
            }

            if (ctx.transitionPlacements != null)
            {
                foreach (var p in ctx.transitionPlacements)
                {
                    sb.Append(p.source.Name);
                    sb.Append(p.target.Name);
                    sb.Append(';');
                }
            }

            byte[] sha = sHA256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));

            int seed = 17;
            for (int i = 0; i < sha.Length; i++) seed = 31 * seed ^ sha[i];
            return seed;
        }

        public void Save()
        {
            // We permute the ctx elements to reduce risk of leaking information if handled sloppily
            if (ctx.itemPlacements != null)
            {
                rng.PermuteInPlace(ctx.itemPlacements);
            }
            if (ctx.transitionPlacements != null)
            {
                rng.PermuteInPlace(ctx.transitionPlacements);
            }

            RandomizerMod.RS = new()
            {
                GenerationSettings = gs.Clone() as GenerationSettings,
                Context = ctx,
                ProfileID = GameManager.instance.profileID,
                TrackerData = new(),
            };
            RandomizerMod.RS.TrackerData.Setup(gs, ctx);

            Export.BeginExport(gs, ctx);
            Export.ExportStart(gs, ctx);
            if (ctx.itemPlacements != null) Export.ExportItemPlacements(rb, ctx.itemPlacements);
            if (ctx.transitionPlacements != null) Export.ExportTransitionPlacements(ctx.transitionPlacements);
            if (ctx.notchCosts != null)
            {
                for (int i = 0; i < ctx.notchCosts.Count; i++) PlayerData.instance.SetInt($"charmCost_{i + 1}", ctx.notchCosts[i]);
            }

            if (gs.CursedSettings.CursedNotches) PlayerData.instance.SetInt(nameof(PlayerData.charmSlots), 1);
            if (gs.CursedSettings.CursedMasks)
            {
                PlayerData.instance.SetInt(nameof(PlayerData.maxHealth), 1);
                PlayerData.instance.SetInt(nameof(PlayerData.maxHealthBase), 1);
            }

            LogManager.WriteLogs(args);
            WriteRawSpoiler(gs, ctx); // write it here and not in LogManager so that it uses the permuted context // write it after LogManager so it doesn't get deleted
        }

        private static void WriteRawSpoiler(GenerationSettings gs, RandoContext ctx)
        {
            using StringWriter sw = new();
            LogManager.Write((tw) =>
            {
                using JsonTextWriter jtr = new(tw);
                JsonUtil._js.Serialize(jtr, ctx);
            }, "RawSpoiler.json");
        }

        private void SelectStart()
        {
            var type = gs.StartLocationSettings.StartLocationType;
            if (type != StartLocationSettings.RandomizeStartLocationType.Fixed)
            {
                List<string> startNames = new(Data.GetStartNames().Where(s => pm.Evaluate(Data.GetStartDef(s).Logic)));
                if (type == StartLocationSettings.RandomizeStartLocationType.RandomExcludingKP) startNames.Remove("King's Pass");
                gs.StartLocationSettings.StartLocation = rng.Next(startNames);
            }

            LogHelper.LogDebug(gs.StartLocationSettings.StartLocation);
        }

        private void AssignNotchCosts()
        {
            if (!gs.MiscSettings.RandomizeNotchCosts)
            {
                ctx.notchCosts = Enumerable.Range(1, 40).Select(i => CharmNotchCosts.GetVanillaCost(i)).ToList();
            }
            else
            {
                ctx.notchCosts = CharmNotchCosts.GetUniformlyRandomCosts(rng, 70, 110).ToList();
            }
        }


    }
}
