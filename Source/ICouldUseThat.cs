using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ICouldUseThat
{
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            var harmony = new Harmony("vibecoded.icut");
            
            try 
            {
                var tryPlace = typeof(GenPlace).GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "TryPlaceThing" && m.GetParameters().Any(p => p.Name == "lastResultingThing"));
                if (tryPlace != null)
                    harmony.Patch(tryPlace, null, new HarmonyMethod(typeof(Patch_GenPlace_TryPlaceThing), nameof(Patch_GenPlace_TryPlaceThing.Postfix)));

                var tryDrop = AccessTools.Method(typeof(GenDrop), "TryDropSpawn");
                if (tryDrop != null)
                    harmony.Patch(tryDrop, null, new HarmonyMethod(typeof(Patch_GenDrop_TryDropSpawn), nameof(Patch_GenDrop_TryDropSpawn.Postfix)));

                var tryStartCarry = AccessTools.Method(typeof(Pawn_CarryTracker), "TryStartCarry", new Type[] { typeof(Thing), typeof(int), typeof(bool) });
                if (tryStartCarry != null)
                    harmony.Patch(tryStartCarry, null, new HarmonyMethod(typeof(Patch_Pawn_CarryTracker_TryStartCarry), nameof(Patch_Pawn_CarryTracker_TryStartCarry.Postfix)));

                Log.Message("[I Could Use That] All Harmony patches applied successfully.");
            }
            catch (Exception e)
            {
                Log.Error("[I Could Use That] Failed to apply patches: " + e.Message);
            }
        }
    }

    public class ICouldUseThatMapComp : MapComponent
    {
        private List<Pair<Thing, int>> queue = new List<Pair<Thing, int>>();
        public ICouldUseThatMapComp(Map map) : base(map) { }

        public void AddToQueue(Thing thing)
        {
            if (thing == null || queue.Any(q => q.First == thing)) return;
            if (thing.Faction == Faction.OfPlayer) return;
            queue.Add(new Pair<Thing, int>(thing, Find.TickManager.TicksGame + 60));
        }

        public override void MapComponentTick()
        {
            int currentTick = Find.TickManager.TicksGame;
            if (currentTick % 20 == 0 && queue.Count > 0)
            {
                for (int i = queue.Count - 1; i >= 0; i--)
                {
                    if (currentTick >= queue[i].Second)
                    {
                        ProcessThing(queue[i].First);
                        queue.RemoveAt(i);
                    }
                }
            }
            if (currentTick % 250 == 0) CleanStockpiles();
        }

        private void CleanStockpiles()
        {
            var designations = map.designationManager.AllDesignations;
            for (int i = designations.Count - 1; i >= 0; i--)
            {
                var des = designations[i];
                if (des.def == DesignationDefOf.Haul || des.def.defName.Contains("HaulUrgently") || des.def.defName.Contains("UrgentHaul"))
                {
                    if (des.target.HasThing && des.target.Thing.Spawned && des.target.Thing.Position.GetSlotGroup(map) != null)
                        map.designationManager.RemoveDesignation(des);
                }
            }
        }

        private void ProcessThing(Thing thing)
        {
            if (thing == null || !thing.Spawned || thing.Map != map) return;
            if (thing.Position.GetSlotGroup(map) != null) return;
            if (ICouldUseThatMod.Settings.unforbidAutomatically && thing.IsForbidden(Faction.OfPlayer))
                thing.SetForbidden(false, false);

            if (ICouldUseThatMod.Settings.markForHaulingAutomatically && Patch_GenPlace_TryPlaceThing.ShouldHaul(thing))
            {
                DesignationDef haulDef = Patch_GenPlace_TryPlaceThing.GetHaulDef();
                if (map.designationManager.DesignationOn(thing, haulDef) == null)
                    map.designationManager.AddDesignation(new Designation(thing, haulDef));
            }
        }
    }

    public static class Patch_GenDrop_TryDropSpawn
    {
        public static void Postfix(bool __result, Thing thing, Map map)
        {
            if (__result && thing != null && map != null)
                map.GetComponent<ICouldUseThatMapComp>()?.AddToQueue(thing);
        }
    }

    public static class Patch_Pawn_CarryTracker_TryStartCarry
    {
        public static void Postfix(int __result, Thing item, Pawn ___pawn)
        {
            if (__result > 0 && item != null && ___pawn != null && ___pawn.Map != null)
                Patch_GenPlace_TryPlaceThing.RemoveHaulDesignations(item, ___pawn.Map);
        }
    }

    public static class Patch_GenPlace_TryPlaceThing
    {
        public static void Postfix(bool __result, Thing thing, Map map, ref Thing lastResultingThing)
        {
            if (__result && map != null)
            {
                Thing target = lastResultingThing ?? thing;
                if (target != null && target.Spawned)
                    map.GetComponent<ICouldUseThatMapComp>()?.AddToQueue(target);
            }
        }

        public static DesignationDef GetHaulDef()
        {
            if (ICouldUseThatMod.Settings.useHaulUrgently)
                return DefDatabase<DesignationDef>.AllDefs.FirstOrDefault(d => d.defName.Contains("HaulUrgently") || d.defName.Contains("UrgentHaul")) ?? DesignationDefOf.Haul;
            return DesignationDefOf.Haul;
        }

        public static void RemoveHaulDesignations(Thing t, Map map)
        {
            if (map == null) return;
            var designations = map.designationManager.AllDesignationsOn(t).ToList();
            foreach (var des in designations)
            {
                if (des.def == DesignationDefOf.Haul || des.def.defName.Contains("HaulUrgently") || des.def.defName.Contains("UrgentHaul"))
                    map.designationManager.RemoveDesignation(des);
            }
        }

        public static bool ShouldHaul(Thing t)
        {
            if (t == null || t.def == null || t is Corpse) return false; // Added: Explicitly ignore corpses
            if (t.def.thingCategories != null && (t.def.IsStuff || t.def.thingCategories.Contains(ThingCategoryDefOf.Chunks))) return false;
            
            bool isRanged = t.def.IsRangedWeapon || (t.def.IsWeapon && !t.def.IsMeleeWeapon);
            bool isChemical = t.def.defName == "Neutroamine" || (t.def.thingCategories != null && t.def.thingCategories.Any(c => c.defName == "MedicineRaw"));
            bool isDrug = t.def.IsDrug;
            
            // Added: Tainted Apparel check
            bool isApparel = t.def.IsApparel;
            if (isApparel && !ICouldUseThatMod.Settings.haulTaintedApparel && t is Apparel a && a.WornByCorpse) return false;

            bool match = (t.def.IsMeleeWeapon && ICouldUseThatMod.Settings.haulMelee) ||
                         (isRanged && ICouldUseThatMod.Settings.haulRanged) ||
                         (isApparel && ICouldUseThatMod.Settings.haulApparel) ||
                         (t.def.IsMedicine && ICouldUseThatMod.Settings.haulMedicine) ||
                         (t.def.IsIngestible && t.def.ingestible != null && ICouldUseThatMod.Settings.haulFood) ||
                         (isDrug && ICouldUseThatMod.Settings.haulDrugs) ||
                         (isChemical && ICouldUseThatMod.Settings.haulChemicals);
            
            if (!match) return false;
            if (t.TryGetQuality(out QualityCategory qc) && qc < ICouldUseThatMod.Settings.minQuality) return false;
            if (t.def.useHitPoints && t.MaxHitPoints > 0 && ((float)t.HitPoints / t.MaxHitPoints) < ICouldUseThatMod.Settings.minHP) return false;
            return true;
        }
    }

    public class ICouldUseThatMod : Mod
    {
        public static ICouldUseThatSettings Settings;
        public ICouldUseThatMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<ICouldUseThatSettings>();
        }
        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("Unforbid automatically", ref Settings.unforbidAutomatically);
            listing.CheckboxLabeled("Mark for hauling automatically", ref Settings.markForHaulingAutomatically);
            listing.CheckboxLabeled("Use 'Haul Urgently' if available", ref Settings.useHaulUrgently);
            listing.Gap();
            listing.CheckboxLabeled("Haul Melee Weapons", ref Settings.haulMelee);
            listing.CheckboxLabeled("Haul Ranged Weapons", ref Settings.haulRanged);
            listing.CheckboxLabeled("Haul Apparel", ref Settings.haulApparel);
            listing.CheckboxLabeled("Haul Tainted Apparel", ref Settings.haulTaintedApparel); // Added: Tainted toggle
            listing.CheckboxLabeled("Haul Medicine", ref Settings.haulMedicine);
            listing.CheckboxLabeled("Haul Food", ref Settings.haulFood);
            listing.CheckboxLabeled("Haul Drugs (Combat & Recreational)", ref Settings.haulDrugs);
            listing.CheckboxLabeled("Haul Chemicals (Neutroamine, ingredients)", ref Settings.haulChemicals);
            listing.Gap();
            listing.Label($"Min Quality: {Settings.minQuality}");
            Settings.minQuality = (QualityCategory)Math.Round(listing.Slider((int)Settings.minQuality, 0, 6));
            listing.Label($"Min HP: {Math.Round(Settings.minHP * 100)}%");
            Settings.minHP = listing.Slider(Settings.minHP, 0f, 1f);
            listing.End();
        }
        public override string SettingsCategory() => "I Could Use That";
    }

    public class ICouldUseThatSettings : ModSettings
    {
        public bool unforbidAutomatically = true, markForHaulingAutomatically = true, useHaulUrgently = true;
        public bool haulMelee = true, haulRanged = true, haulApparel = true, haulTaintedApparel = false; // Default: false
        public bool haulMedicine = true, haulFood = true, haulDrugs = true, haulChemicals = true;
        public QualityCategory minQuality = QualityCategory.Normal;
        public float minHP = 0.0f;
        public override void ExposeData()
        {
            Scribe_Values.Look(ref unforbidAutomatically, "unforbidAutomatically", true);
            Scribe_Values.Look(ref markForHaulingAutomatically, "markForHaulingAutomatically", true);
            Scribe_Values.Look(ref useHaulUrgently, "useHaulUrgently", true);
            Scribe_Values.Look(ref haulMelee, "haulMelee", true);
            Scribe_Values.Look(ref haulRanged, "haulRanged", true);
            Scribe_Values.Look(ref haulApparel, "haulApparel", true);
            Scribe_Values.Look(ref haulTaintedApparel, "haulTaintedApparel", false);
            Scribe_Values.Look(ref haulMedicine, "haulMedicine", true);
            Scribe_Values.Look(ref haulFood, "haulFood", true);
            Scribe_Values.Look(ref haulDrugs, "haulDrugs", true);
            Scribe_Values.Look(ref haulChemicals, "haulChemicals", true);
            Scribe_Values.Look(ref minQuality, "minQuality", QualityCategory.Normal);
            Scribe_Values.Look(ref minHP, "minHP", 0.0f);
        }
    }
}
